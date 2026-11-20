import { Component, EventEmitter, Input, OnInit, Output, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../core/api.service';
import { FileSizePipe } from '../../core/pipes';
import { ToastService } from '../../core/toast.service';
import { MessageDetail, UploadResponse } from '../../core/models';

export interface ComposerPayload {
  draftId?: string | null;
}

@Component({
  selector: 'app-composer',
  standalone: true,
  imports: [FormsModule, FileSizePipe],
  template: `
    <div class="modal-backdrop" (click)="cancel()">
      <div class="modal" (click)="$event.stopPropagation()">
        <div class="modal-header">
          <div>
            <div class="card-title">{{ title() }}</div>
            <div class="muted small">from {{ fromAddress }}</div>
          </div>
          <button class="btn btn-ghost btn-icon" (click)="cancel()" aria-label="Close">&#10005;</button>
        </div>

        <div class="modal-body stack">
          <div class="field">
            <label for="to">To</label>
            <input id="to" class="input" [(ngModel)]="to" placeholder="name@example.org, other@example.org" />
          </div>

          <div class="row" style="gap: 8px">
            <button class="btn btn-ghost btn-sm" type="button" (click)="showCc = !showCc">{{ showCc ? 'Hide' : 'Add' }} Cc/Bcc</button>
          </div>

          @if (showCc) {
            <div class="grid-2">
              <div class="field">
                <label for="cc">Cc</label>
                <input id="cc" class="input" [(ngModel)]="cc" placeholder="optional" />
              </div>
              <div class="field">
                <label for="bcc">Bcc</label>
                <input id="bcc" class="input" [(ngModel)]="bcc" placeholder="optional" />
              </div>
            </div>
          }

          <div class="field">
            <label for="subject">Subject</label>
            <input id="subject" class="input" [(ngModel)]="subject" placeholder="Subject" />
          </div>

          <div class="field">
            <label for="body">Message</label>
            <textarea id="body" class="textarea" rows="12" [(ngModel)]="body" placeholder="Write your message..."></textarea>
          </div>

          @if (attachments().length > 0) {
            <div class="row" style="flex-wrap: wrap; gap: 8px">
              @for (file of attachments(); track file.id) {
                <span class="badge badge-info" style="padding: 6px 10px">
                  {{ file.fileName }} · {{ file.sizeBytes | fileSize }}
                  <button class="btn btn-ghost btn-icon btn-sm" (click)="removeAttachment(file.id)" aria-label="Remove attachment">&#10005;</button>
                </span>
              }
            </div>
          }
        </div>

        <div class="modal-footer">
          <div class="row" style="gap: 8px">
            <button class="btn btn-primary" (click)="send()" [disabled]="sending() || !to.trim()">
              @if (sending()) { <span class="spinner" style="width: 14px; height: 14px; border-color: rgba(255,255,255,.4); border-top-color: #fff"></span> }
              Send
            </button>
            <button class="btn" (click)="saveDraft()" [disabled]="saving()">Save draft</button>
          </div>

          <div class="row" style="gap: 8px">
            <label class="btn btn-ghost btn-sm" style="cursor: pointer">
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M21 12l-8.5 8.5a5 5 0 01-7-7L14 5a3.5 3.5 0 015 5l-8.5 8.5a2 2 0 01-3-3L15 8" stroke-linecap="round" stroke-linejoin="round"/></svg>
              Attach
              <input type="file" hidden multiple (change)="onFiles($event)" />
            </label>
            <span class="subtle small">Ctrl+Enter to send</span>
          </div>
        </div>
      </div>
    </div>
  `,
  host: {
    '(document:keydown.control.enter)': 'send()',
    '(document:keydown.escape)': 'cancel()',
  },
})
export class ComposerComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);

  @Input({ required: true }) mailboxId!: string;
  @Input() mode: 'new' | 'reply' | 'forward' = 'new';
  @Input() source: MessageDetail | null = null;
  @Input() signature = '';
  @Input() fromAddress = '';

  @Output() close = new EventEmitter<void>();
  @Output() sent = new EventEmitter<ComposerPayload>();

  to = '';
  cc = '';
  bcc = '';
  subject = '';
  body = '';
  showCc = false;

  readonly sending = signal(false);
  readonly saving = signal(false);
  readonly attachments = signal<UploadResponse[]>([]);

  private draftId: string | null = null;
  private inReplyTo: string | null = null;
  private references: string | null = null;

  ngOnInit(): void {
    if (!this.fromAddress) this.fromAddress = this.mailboxId;

    if (this.source) {
      this.subject = this.source.subject.startsWith('Re:') || this.source.subject.startsWith('Fwd:')
        ? this.source.subject
        : `${this.mode === 'reply' ? 'Re' : 'Fwd'}: ${this.source.subject}`;

      if (this.mode === 'reply') {
        this.to = this.source.from.address;
        this.inReplyTo = this.source.rfcMessageId ?? null;
        this.references = this.source.rfcMessageId ?? null;
        this.body = `\n\n---\nOn ${new Date(this.source.receivedAt).toLocaleString()}, ${this.source.from.address} wrote:\n${quote(this.source.bodyText ?? '')}`;
      } else {
        this.body = `\n\n--- Forwarded message ---\nFrom: ${this.source.from.address}\nSubject: ${this.source.subject}\n\n${this.source.bodyText ?? ''}`;
        this.attachments.set(
          this.source.attachments.map((a) => ({
            id: a.id,
            fileName: a.fileName,
            sizeBytes: a.sizeBytes,
            contentType: a.contentType,
          })),
        );
      }
    } else if (this.signature) {
      this.body = `\n\n-- \n${this.signature}`;
    }
  }

  title(): string {
    return this.mode === 'reply' ? 'Reply' : this.mode === 'forward' ? 'Forward' : 'New message';
  }

  onFiles(event: Event): void {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []);
    input.value = '';

    for (const file of files) {
      this.api.upload(this.mailboxId, file).subscribe({
        next: (uploaded) => this.attachments.update((list) => [...list, uploaded]),
        error: (error) => this.toast.error(ApiService.toError(error).detail),
      });
    }
  }

  removeAttachment(id: string): void {
    this.attachments.update((list) => list.filter((file) => file.id !== id));
  }

  send(): void {
    if (this.sending() || !this.to.trim()) return;
    this.sending.set(true);

    this.api
      .send(this.mailboxId, {
        to: split(this.to),
        cc: split(this.cc),
        bcc: split(this.bcc),
        subject: this.subject || '(no subject)',
        textBody: this.body,
        attachmentIds: this.attachments().map((file) => file.id),
        draftId: this.draftId,
        inReplyTo: this.inReplyTo,
        references: this.references,
      })
      .subscribe({
        next: () => {
          this.sending.set(false);
          this.sent.emit({ draftId: this.draftId });
        },
        error: (error) => {
          this.sending.set(false);
          this.toast.error(ApiService.toError(error).detail);
        },
      });
  }

  saveDraft(): void {
    if (this.saving()) return;
    this.saving.set(true);

    this.api
      .saveDraft(this.mailboxId, {
        to: split(this.to),
        cc: split(this.cc),
        bcc: split(this.bcc),
        subject: this.subject || '(no subject)',
        textBody: this.body,
        attachmentIds: this.attachments().map((file) => file.id),
        inReplyTo: this.inReplyTo,
        references: this.references,
      })
      .subscribe({
        next: (draft) => {
          this.saving.set(false);
          this.draftId = draft.id;
          this.toast.success('Draft saved');
          this.sent.emit({ draftId: draft.id });
        },
        error: (error) => {
          this.saving.set(false);
          this.toast.error(ApiService.toError(error).detail);
        },
      });
  }

  cancel(): void {
    this.close.emit();
  }
}

function split(value: string): string[] {
  return value
    .split(/[,;\s]+/)
    .map((part) => part.trim())
    .filter(Boolean);
}

function quote(text: string): string {
  return text
    .split('\n')
    .map((line) => `> ${line}`)
    .join('\n');
}
