import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { FileSizePipe, InitialsPipe, ShortDatePipe, TimeAgoPipe } from '../../core/pipes';
import { ToastService } from '../../core/toast.service';
import { Folder, Mailbox, MessageDetail, MessageSummary } from '../../core/models';
import { ComposerComponent, ComposerPayload } from './composer.component';

@Component({
  selector: 'app-mail',
  standalone: true,
  imports: [FormsModule, ComposerComponent, TimeAgoPipe, ShortDatePipe, FileSizePipe, InitialsPipe],
  template: `
    <div class="mail">
      <!-- ------------------------------------------------ folders -->
      <aside class="folders">
        <div class="mailbox-picker">
          <select class="select" [ngModel]="mailboxId()" (ngModelChange)="selectMailbox($event)" aria-label="Mailbox">
            @for (mailbox of mailboxes(); track mailbox.id) {
              <option [value]="mailbox.id">{{ mailbox.address }}</option>
            }
          </select>
          <button class="btn btn-primary" style="width: 100%; margin-top: 10px" (click)="openComposer('new')">
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 5v14M5 12h14" stroke-linecap="round" /></svg>
            Compose
          </button>
        </div>

        <nav class="folder-list">
          @for (folder of folders(); track folder.id) {
            <button
              class="folder"
              [class.active]="currentFolder() === folder.kind"
              (click)="openFolder(folder)"
            >
              <span class="folder-icon" [attr.data-kind]="folder.kind">
                @switch (folder.kind) {
                  @case ('Inbox') { <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9"><path d="M3 12h5l2 3h4l2-3h5" stroke-linejoin="round"/><path d="M3 12l2.5-7h13L21 12v7H3z" stroke-linejoin="round"/></svg> }
                  @case ('Sent') { <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9"><path d="M4 12l16-8-6 16-3-6-7-2z" stroke-linejoin="round"/></svg> }
                  @case ('Drafts') { <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9"><path d="M5 3h9l5 5v13H5z" stroke-linejoin="round"/><path d="M14 3v5h5"/></svg> }
                  @case ('Trash') { <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9"><path d="M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13" stroke-linejoin="round"/></svg> }
                  @case ('Junk') { <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9"><circle cx="12" cy="12" r="9"/><path d="M12 8v5M12 16.5v.5" stroke-linecap="round"/></svg> }
                  @case ('Archive') { <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9"><rect x="3" y="4" width="18" height="4" rx="1"/><path d="M5 8v12h14V8M10 12h4" stroke-linejoin="round"/></svg> }
                  @default { <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9"><path d="M3 7h6l2 2h10v10H3z" stroke-linejoin="round"/></svg> }
                }
              </span>
              <span class="grow truncate" style="text-align: left">{{ folder.name }}</span>
              @if (folder.unreadCount > 0) {
                <span class="badge badge-brand">{{ folder.unreadCount }}</span>
              }
            </button>
          }
        </nav>

        <div class="quota">
          @if (currentMailbox(); as mailbox) {
            <div class="row-between small">
              <span class="muted">Storage</span>
              <span class="mono">{{ mailbox.usedBytes | fileSize }} / {{ mailbox.quotaBytes | fileSize }}</span>
            </div>
            <div class="meter"><span [style.width.%]="mailbox.quotaUsedPercent"></span></div>
          }
        </div>
      </aside>

      <!-- ------------------------------------------------ list -->
      <section class="list" [class.hidden-mobile]="selected()">
        <div class="list-toolbar">
          <div class="search">
            <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="11" cy="11" r="7"/><path d="M20 20l-3.5-3.5" stroke-linecap="round"/></svg>
            <input
              class="search-input"
              placeholder="Search mail"
              [ngModel]="search()"
              (ngModelChange)="onSearch($event)"
              aria-label="Search mail"
            />
            @if (search()) {
              <button class="btn btn-ghost btn-icon btn-sm" (click)="clearSearch()" aria-label="Clear search">&#10005;</button>
            }
          </div>
          <button class="btn btn-ghost btn-icon" (click)="reload()" title="Refresh" aria-label="Refresh">
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M20 11a8 8 0 10-2.3 6" stroke-linecap="round"/><path d="M20 4v7h-7" stroke-linecap="round" stroke-linejoin="round"/></svg>
          </button>
        </div>

        @if (selectedIds().size > 0) {
          <div class="bulk">
            <span class="small"><strong>{{ selectedIds().size }}</strong> selected</span>
            <div class="row" style="gap: 6px">
              <button class="btn btn-sm" (click)="bulk('markRead')">Read</button>
              <button class="btn btn-sm" (click)="bulk('markUnread')">Unread</button>
              <button class="btn btn-sm" (click)="bulk('archive')">Archive</button>
              <button class="btn btn-sm btn-danger" (click)="bulk('delete')">Delete</button>
              <button class="btn btn-ghost btn-sm" (click)="clearSelection()">Cancel</button>
            </div>
          </div>
        }

        <div class="list-scroll">
          @if (loading()) {
            @for (row of [1,2,3,4,5,6]; track row) {
              <div style="padding: 14px 16px; border-bottom: 1px solid var(--border)">
                <div class="skeleton" style="height: 10px; width: 40%; margin-bottom: 8px"></div>
                <div class="skeleton" style="height: 10px; width: 75%"></div>
              </div>
            }
          } @else if (messages().length === 0) {
            <div class="empty">
              <svg width="34" height="34" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5"><rect x="3" y="5" width="18" height="14" rx="2"/><path d="M3 7l9 6 9-6"/></svg>
              <div style="font-weight: 600">{{ search() ? 'No matches' : 'Nothing here yet' }}</div>
              <div class="small">{{ search() ? 'Try a different search term.' : 'Messages will appear here as they arrive.' }}</div>
            </div>
          } @else {
            @for (message of messages(); track message.id) {
              <article
                class="row-item"
                [class.unread]="!message.isRead"
                [class.active]="selected()?.id === message.id"
                (click)="open(message)"
              >
                <input
                  type="checkbox"
                  class="row-check"
                  [checked]="selectedIds().has(message.id)"
                  (click)="$event.stopPropagation()"
                  (change)="toggleSelect(message.id)"
                  [attr.aria-label]="'Select ' + message.subject"
                />
                <div class="grow" style="min-width: 0">
                  <div class="row-between" style="gap: 8px">
                    <span class="row-from truncate">{{ message.fromName || message.fromAddress }}</span>
                    <span class="row-date">{{ message.receivedAt | shortDate }}</span>
                  </div>
                  <div class="row-subject truncate">{{ message.subject }}</div>
                  <div class="row-preview truncate">{{ message.preview }}</div>
                  <div class="row" style="gap: 6px; margin-top: 6px">
                    @if (message.isFlagged) { <span class="badge badge-warn">Flagged</span> }
                    @if (message.hasAttachments) {
                      <span class="badge">
                        <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M21 12l-8.5 8.5a5 5 0 01-7-7L14 5a3.5 3.5 0 015 5l-8.5 8.5a2 2 0 01-3-3L15 8" stroke-linecap="round" stroke-linejoin="round"/></svg>
                        File
                      </span>
                    }
                    @if (message.spamVerdict === 'Spam') { <span class="badge badge-danger">Spam</span> }
                    @if (message.spamVerdict === 'Suspect') { <span class="badge badge-warn">Suspicious</span> }
                  </div>
                </div>
              </article>
            }

            @if (hasNext()) {
              <div style="padding: 14px; text-align: center">
                <button class="btn btn-sm" (click)="loadMore()">Load more</button>
              </div>
            }
          }
        </div>
      </section>

      <!-- ------------------------------------------------ reading pane -->
      <section class="reader">
        @if (selected(); as message) {
          <div class="reader-header">
            <div class="row-between" style="align-items: flex-start">
              <div style="min-width: 0">
                <h2 style="font-size: 17px; line-height: 1.35">{{ message.subject }}</h2>
                <div class="row" style="gap: 8px; margin-top: 10px">
                  <span class="avatar">{{ message.from.name || message.from.address | initials }}</span>
                  <div style="min-width: 0">
                    <div style="font-weight: 600" class="truncate">{{ message.from.name || message.from.address }}</div>
                    <div class="muted small truncate">{{ message.from.address }}</div>
                  </div>
                </div>
                <div class="muted small" style="margin-top: 8px">
                  to {{ recipientLine(message) }} · {{ message.receivedAt | timeAgo }} ago
                  @if (message.spamScore > 0) { · spam score {{ message.spamScore }} }
                </div>
              </div>

              <div class="row" style="gap: 4px; flex: none">
                <button class="btn btn-ghost btn-icon" title="Reply" (click)="openComposer('reply', message)">&#8617;</button>
                <button class="btn btn-ghost btn-icon" title="Forward" (click)="openComposer('forward', message)">&#8618;</button>
                <button class="btn btn-ghost btn-icon" [title]="message.isFlagged ? 'Unflag' : 'Flag'" (click)="toggleFlag(message)">
                  <svg width="16" height="16" viewBox="0 0 24 24" [attr.fill]="message.isFlagged ? 'currentColor' : 'none'" stroke="currentColor" stroke-width="1.8"><path d="M6 21V4h11l-2 4 2 4H6" stroke-linejoin="round"/></svg>
                </button>
                <a class="btn btn-ghost btn-icon" [href]="rawUrl(message.id)" title="Download .eml">
                  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><path d="M12 4v11M7.5 11L12 15.5 16.5 11" stroke-linecap="round" stroke-linejoin="round"/><path d="M5 19h14" stroke-linecap="round"/></svg>
                </a>
                <button class="btn btn-ghost btn-icon" title="Delete" (click)="deleteSelected(message)">
                  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><path d="M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13" stroke-linejoin="round"/></svg>
                </button>
              </div>
            </div>
          </div>

          @if (message.attachments.length > 0) {
            <div class="attachments">
              @for (attachment of message.attachments; track attachment.id) {
                <a class="attachment" [href]="attachmentUrl(message.id, attachment.id)" target="_blank" rel="noopener">
                  <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><path d="M21 12l-8.5 8.5a5 5 0 01-7-7L14 5a3.5 3.5 0 015 5l-8.5 8.5a2 2 0 01-3-3L15 8" stroke-linecap="round" stroke-linejoin="round"/></svg>
                  <span class="truncate">{{ attachment.fileName }}</span>
                  <span class="subtle small">{{ attachment.sizeBytes | fileSize }}</span>
                </a>
              }
            </div>
          }

          <div class="reader-body">
            @if (safeHtml(); as html) {
              <iframe class="mail-frame" title="Message body" sandbox="" referrerpolicy="no-referrer" [srcdoc]="html"></iframe>
            } @else {
              <pre class="plain-body">{{ message.bodyText }}</pre>
            }
          </div>
        } @else {
          <div class="empty" style="height: 100%">
            <svg width="42" height="42" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.3"><rect x="3" y="5" width="18" height="14" rx="2"/><path d="M3 7l9 6 9-6"/></svg>
            <div style="font-weight: 600">Select a message</div>
            <div class="small">Choose a conversation on the left to read it here.</div>
          </div>
        }
      </section>
    </div>

    @if (composer(); as state) {
      <app-composer
        [mailboxId]="mailboxId()!"
        [fromAddress]="currentMailbox()?.address ?? ''"
        [mode]="state.mode"
        [source]="state.source"
        [signature]="signature()"
        (close)="composer.set(null)"
        (sent)="onSent($event)"
      />
    }
  `,
  styles: [
    `
      .mail {
        display: grid;
        grid-template-columns: 236px 372px 1fr;
        width: 100%;
        height: calc(100vh - 58px);
        min-height: 0;
      }
      .folders {
        border-right: 1px solid var(--border);
        background: var(--surface);
        display: flex;
        flex-direction: column;
        padding: 14px 12px;
        gap: 14px;
        overflow: auto;
      }
      .mailbox-picker select { font-size: 12.5px; }
      .folder-list { display: flex; flex-direction: column; gap: 2px; }
      .folder {
        display: flex;
        align-items: center;
        gap: 10px;
        width: 100%;
        padding: 8px 10px;
        border: 0;
        background: transparent;
        border-radius: 9px;
        color: var(--text-muted);
        font: inherit;
        font-size: 13px;
        font-weight: 550;
        cursor: pointer;
      }
      .folder:hover { background: var(--surface-3); color: var(--text); }
      .folder.active { background: var(--brand-50); color: var(--brand-600); }
      .folder-icon { display: grid; place-items: center; width: 18px; }
      .quota { margin-top: auto; display: grid; gap: 6px; padding: 10px; border-radius: 10px; background: var(--surface-2); }
      .meter { height: 6px; border-radius: 999px; background: var(--surface-3); overflow: hidden; }
      .meter span { display: block; height: 100%; background: linear-gradient(90deg, var(--brand-500), var(--violet-500)); }

      .list {
        border-right: 1px solid var(--border);
        background: var(--surface);
        display: flex;
        flex-direction: column;
        min-height: 0;
      }
      .list-toolbar { display: flex; gap: 8px; align-items: center; padding: 10px 12px; border-bottom: 1px solid var(--border); }
      .search { flex: 1; display: flex; align-items: center; gap: 8px; background: var(--surface-2); border: 1px solid var(--border); border-radius: 9px; padding: 0 10px; height: 34px; color: var(--text-subtle); }
      .search:focus-within { border-color: var(--brand-500); box-shadow: 0 0 0 3px var(--ring); }
      .search-input { flex: 1; border: 0; background: transparent; color: var(--text); font: inherit; font-size: 13px; outline: none; min-width: 0; }
      .bulk { display: flex; align-items: center; justify-content: space-between; gap: 8px; padding: 8px 12px; background: var(--brand-50); border-bottom: 1px solid var(--border); flex-wrap: wrap; }
      .list-scroll { overflow: auto; flex: 1; min-height: 0; }

      .row-item { display: flex; gap: 10px; padding: 12px 14px; border-bottom: 1px solid var(--border); cursor: pointer; align-items: flex-start; }
      .row-item:hover { background: var(--surface-2); }
      .row-item.active { background: var(--brand-50); box-shadow: inset 3px 0 0 var(--brand-500); }
      .row-check { margin-top: 3px; accent-color: var(--brand-600); }
      .row-from { font-size: 13px; font-weight: 500; color: var(--text-muted); }
      .row-item.unread .row-from { color: var(--text); font-weight: 700; }
      .row-date { font-size: 11.5px; color: var(--text-subtle); flex: none; }
      .row-subject { font-size: 13px; color: var(--text-muted); margin-top: 2px; }
      .row-item.unread .row-subject { color: var(--text); font-weight: 600; }
      .row-preview { font-size: 12.5px; color: var(--text-subtle); margin-top: 2px; }

      .reader { display: flex; flex-direction: column; min-height: 0; background: var(--surface); }
      .reader-header { padding: 18px 22px; border-bottom: 1px solid var(--border); }
      .reader-body { flex: 1; min-height: 0; overflow: auto; padding: 18px 22px; }
      .mail-frame { width: 100%; height: 100%; min-height: 420px; border: 0; background: #fff; border-radius: 10px; }
      .plain-body { white-space: pre-wrap; font-family: var(--font); font-size: 13.5px; line-height: 1.7; margin: 0; }
      .attachments { display: flex; flex-wrap: wrap; gap: 8px; padding: 12px 22px; border-bottom: 1px solid var(--border); }
      .attachment { display: inline-flex; align-items: center; gap: 8px; padding: 7px 11px; border: 1px solid var(--border); border-radius: 9px; font-size: 12.5px; color: var(--text); background: var(--surface-2); max-width: 260px; }
      .attachment:hover { border-color: var(--brand-500); text-decoration: none; }

      @media (max-width: 1180px) {
        .mail { grid-template-columns: 210px 320px 1fr; }
      }
      @media (max-width: 980px) {
        .mail { grid-template-columns: 1fr; }
        .folders, .list { display: none; }
        .list.hidden-mobile { display: none; }
      }
    `,
  ],
})
export class MailComponent {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly toast = inject(ToastService);
  private readonly sanitizer = inject(DomSanitizer);

  readonly mailboxes = this.auth.mailboxes;
  readonly signature = computed(() => this.auth.user()?.signature ?? '');

  readonly mailboxId = signal<string | null>(null);
  readonly currentMailbox = computed(() => this.mailboxes().find((m) => m.id === this.mailboxId()) ?? null);
  readonly folders = signal<Folder[]>([]);
  readonly messages = signal<MessageSummary[]>([]);
  readonly selected = signal<MessageDetail | null>(null);
  readonly selectedIds = signal<Set<string>>(new Set());
  readonly currentFolder = signal<string>('Inbox');
  readonly search = signal('');
  readonly loading = signal(false);
  readonly hasNext = signal(false);
  readonly composer = signal<{ mode: 'new' | 'reply' | 'forward'; source: MessageDetail | null } | null>(null);

  private page = 1;
  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    const first = this.mailboxes()[0];
    if (first) {
      this.mailboxId.set(first.id);
      this.loadFolders();
      this.loadMessages();
    }
  }

  safeHtml(): SafeHtml | null {
    const message = this.selected();
    if (!message?.bodyHtml) return null;
    return this.sanitizer.bypassSecurityTrustHtml(message.bodyHtml);
  }

  rawUrl(id: string): string {
    return this.api.rawUrl(id);
  }

  attachmentUrl(messageId: string, attachmentId: string): string {
    return this.api.attachmentUrl(messageId, attachmentId);
  }

  recipientLine(message: MessageDetail): string {
    const names = message.to.map((a) => a.name || a.address);
    return names.length > 2 ? `${names.slice(0, 2).join(', ')} +${names.length - 2}` : names.join(', ');
  }

  selectMailbox(id: string): void {
    this.mailboxId.set(id);
    this.selected.set(null);
    this.currentFolder.set('Inbox');
    this.search.set('');
    this.loadFolders();
    this.loadMessages();
  }

  openFolder(folder: Folder): void {
    this.currentFolder.set(folder.kind);
    this.selected.set(null);
    this.loadMessages();
  }

  reload(): void {
    this.loadFolders();
    this.loadMessages();
  }

  onSearch(value: string): void {
    this.search.set(value);
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => this.loadMessages(), 280);
  }

  clearSearch(): void {
    this.search.set('');
    this.loadMessages();
  }

  loadMore(): void {
    this.page += 1;
    this.loadMessages(true);
  }

  open(message: MessageSummary): void {
    this.api.message(message.id).subscribe({
      next: (detail) => {
        this.selected.set(detail);
        this.messages.update((list) =>
          list.map((item) => (item.id === detail.id ? { ...item, isRead: true } : item)),
        );
        this.refreshUnread(detail);
      },
      error: (error) => this.toast.error(ApiService.toError(error).detail),
    });
  }

  toggleFlag(message: MessageDetail): void {
    const next = !message.isFlagged;
    this.api.updateMessage(message.id, { isFlagged: next }).subscribe({
      next: (updated) => {
        this.selected.set(updated);
        this.messages.update((list) => list.map((m) => (m.id === updated.id ? { ...m, isFlagged: next } : m)));
      },
      error: (error) => this.toast.error(ApiService.toError(error).detail),
    });
  }

  deleteSelected(message: MessageDetail): void {
    this.api.deleteMessage(message.id).subscribe({
      next: () => {
        this.selected.set(null);
        this.messages.update((list) => list.filter((m) => m.id !== message.id));
        this.toast.success('Moved to trash');
        this.loadFolders();
      },
      error: (error) => this.toast.error(ApiService.toError(error).detail),
    });
  }

  toggleSelect(id: string): void {
    this.selectedIds.update((set) => {
      const next = new Set(set);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  clearSelection(): void {
    this.selectedIds.set(new Set());
  }

  bulk(action: string): void {
    const mailboxId = this.mailboxId();
    if (!mailboxId) return;

    this.api.bulk(mailboxId, action, Array.from(this.selectedIds())).subscribe({
      next: (result) => {
        this.toast.success(`${result.affected} message(s) updated`);
        this.clearSelection();
        this.reload();
      },
      error: (error) => this.toast.error(ApiService.toError(error).detail),
    });
  }

  openComposer(mode: 'new' | 'reply' | 'forward', source: MessageDetail | null = null): void {
    this.composer.set({ mode, source });
  }

  onSent(payload: ComposerPayload): void {
    this.composer.set(null);
    this.toast.success('Message sent');
    if (payload.draftId) this.reload();
    this.loadFolders();
  }

  private refreshUnread(detail: MessageDetail): void {
    this.folders.update((folders) =>
      folders.map((folder) =>
        folder.id === detail.folderId && folder.unreadCount > 0
          ? { ...folder, unreadCount: folder.unreadCount - 1 }
          : folder,
      ),
    );
  }

  private loadFolders(): void {
    const mailboxId = this.mailboxId();
    if (!mailboxId) return;
    this.api.folders(mailboxId).subscribe({
      next: (folders) => this.folders.set(folders),
      error: (error) => this.toast.error(ApiService.toError(error).detail),
    });
  }

  private loadMessages(append = false): void {
    const mailboxId = this.mailboxId();
    if (!mailboxId) return;

    if (!append) this.page = 1;
    this.loading.set(true);

    this.api
      .messages(mailboxId, {
        folder: this.currentFolder(),
        q: this.search() || undefined,
        page: this.page,
        size: 30,
      })
      .subscribe({
        next: (result) => {
          this.messages.update((list) => (append ? [...list, ...result.items] : result.items));
          this.hasNext.set(result.hasNext);
          this.loading.set(false);
        },
        error: (error) => {
          this.loading.set(false);
          this.toast.error(ApiService.toError(error).detail);
        },
      });
  }
}
