import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { FileSizePipe, TimeAgoPipe } from '../../core/pipes';
import { ToastService } from '../../core/toast.service';
import { AdminMailbox, AdminUser, AuditEntry, Domain, DnsRecord, QueueItem, Stats } from '../../core/models';

type Tab = 'overview' | 'domains' | 'mailboxes' | 'users' | 'queue' | 'audit';

@Component({
  selector: 'app-admin',
  standalone: true,
  imports: [FormsModule, TimeAgoPipe, FileSizePipe],
  templateUrl: './admin.component.html',
  styleUrl: './admin.component.css',
})
export class AdminComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);
  readonly auth = inject(AuthService);

  readonly tab = signal<Tab>('overview');
  readonly loading = signal(false);

  readonly stats = signal<Stats | null>(null);
  readonly domains = signal<Domain[]>([]);
  readonly mailboxes = signal<AdminMailbox[]>([]);
  readonly users = signal<AdminUser[]>([]);
  readonly queue = signal<QueueItem[]>([]);
  readonly audit = signal<AuditEntry[]>([]);
  readonly dns = signal<{ domain: string; records: DnsRecord[] } | null>(null);

  readonly selectedDomain = signal<string>('');
  readonly queueFilter = signal<string>('');

  readonly maxVolume = computed(() => {
    const volume = this.stats()?.volume ?? [];
    return Math.max(1, ...volume.map((day) => Math.max(day.received, day.sent)));
  });

  // forms
  domainForm = { name: '', displayName: '' };
  mailboxForm = { localPart: '', displayName: '', password: '' };
  userForm = { email: '', displayName: '', password: '' };
  busy = signal(false);

  ngOnInit(): void {
    this.load('overview');
  }

  load(tab: Tab): void {
    this.tab.set(tab);
    this.loading.set(true);

    switch (tab) {
      case 'overview':
        this.api.stats().subscribe({
          next: (stats) => this.done(() => this.stats.set(stats)),
          error: (error) => this.fail(error),
        });
        break;
      case 'domains':
        this.api.domains().subscribe({
          next: (domains) => this.done(() => this.domains.set(domains)),
          error: (error) => this.fail(error),
        });
        break;
      case 'mailboxes':
        this.api.adminMailboxes(this.selectedDomain() || undefined).subscribe({
          next: (mailboxes) => this.done(() => this.mailboxes.set(mailboxes)),
          error: (error) => this.fail(error),
        });
        break;
      case 'users':
        this.api.users().subscribe({
          next: (users) => this.done(() => this.users.set(users)),
          error: (error) => this.fail(error),
        });
        break;
      case 'queue':
        this.api.queue(this.queueFilter() || undefined).subscribe({
          next: (result) => this.done(() => this.queue.set(result.items)),
          error: (error) => this.fail(error),
        });
        break;
      case 'audit':
        this.api.audit().subscribe({
          next: (result) => this.done(() => this.audit.set(result.items)),
          error: (error) => this.fail(error),
        });
        break;
    }
  }

  createDomain(): void {
    if (!this.domainForm.name.trim() || this.busy()) return;
    this.busy.set(true);
    this.api.createDomain(this.domainForm).subscribe({
      next: (domain) => {
        this.busy.set(false);
        this.domainForm = { name: '', displayName: '' };
        this.toast.success(`Domain ${domain.name} added`);
        this.load('domains');
        this.showDns(domain.id);
      },
      error: (error) => {
        this.busy.set(false);
        this.toast.error(ApiService.toError(error).detail);
      },
    });
  }

  showDns(domainId: string): void {
    this.api.dnsRecords(domainId).subscribe({
      next: (records) => this.dns.set(records),
      error: (error) => this.toast.error(ApiService.toError(error).detail),
    });
  }

  rotateDkim(domainId: string): void {
    this.api.rotateDkim(domainId).subscribe({
      next: (records) => {
        this.toast.success('DKIM key rotated - publish the new DNS record');
        this.dns.set(records);
        this.load('domains');
      },
      error: (error) => this.toast.error(ApiService.toError(error).detail),
    });
  }

  toggleDomain(domain: Domain): void {
    this.api.updateDomain(domain.id, { isActive: !domain.isActive }).subscribe({
      next: () => this.load('domains'),
      error: (error) => this.toast.error(ApiService.toError(error).detail),
    });
  }

  createMailbox(): void {
    const domain = this.domains().find((d) => d.id === this.selectedDomain());
    if (!domain || !this.mailboxForm.localPart.trim() || this.busy()) {
      this.toast.error('Pick a domain first');
      return;
    }

    this.busy.set(true);
    this.api
      .createMailbox({
        domainId: domain.id,
        localPart: this.mailboxForm.localPart,
        displayName: this.mailboxForm.displayName || undefined,
        password: this.mailboxForm.password || undefined,
      })
      .subscribe({
        next: (result) => {
          this.busy.set(false);
          this.mailboxForm = { localPart: '', displayName: '', password: '' };
          if (result.generatedPassword) {
            this.toast.success(`Mailbox created. Temporary password: ${result.generatedPassword}`);
          } else {
            this.toast.success('Mailbox created');
          }
          this.load('mailboxes');
        },
        error: (error) => {
          this.busy.set(false);
          this.toast.error(ApiService.toError(error).detail);
        },
      });
  }

  createUser(): void {
    if (!this.userForm.email.trim() || this.busy()) return;
    this.busy.set(true);
    this.api.createUser({ ...this.userForm, roles: ['User'] }).subscribe({
      next: (result) => {
        this.busy.set(false);
        this.userForm = { email: '', displayName: '', password: '' };
        this.toast.success(
          result.generatedPassword ? `User created. Temporary password: ${result.generatedPassword}` : 'User created',
        );
        this.load('users');
      },
      error: (error) => {
        this.busy.set(false);
        this.toast.error(ApiService.toError(error).detail);
      },
    });
  }

  resetPassword(user: AdminUser): void {
    this.api.resetPassword(user.id).subscribe({
      next: (result) => this.toast.success(`New password for ${user.email}: ${result.password}`),
      error: (error) => this.toast.error(ApiService.toError(error).detail),
    });
  }

  retryQueue(item: QueueItem): void {
    this.api.retryQueueItem(item.id).subscribe({
      next: () => {
        this.toast.success('Queued for retry');
        this.load('queue');
      },
      error: (error) => this.toast.error(ApiService.toError(error).detail),
    });
  }

  cancelQueue(item: QueueItem): void {
    this.api.cancelQueueItem(item.id).subscribe({
      next: () => {
        this.toast.info('Delivery cancelled');
        this.load('queue');
      },
      error: (error) => this.toast.error(ApiService.toError(error).detail),
    });
  }

  processQueue(): void {
    this.api.processQueue().subscribe({
      next: (result) => {
        this.toast.success(`Delivery pass finished (${result.processed} message(s))`);
        this.load('queue');
      },
      error: (error) => this.toast.error(ApiService.toError(error).detail),
    });
  }

  copy(value: string): void {
    void navigator.clipboard?.writeText(value);
    this.toast.info('Copied to clipboard');
  }

  statusClass(status: string): string {
    switch (status) {
      case 'Sent': return 'badge-ok';
      case 'Failed': return 'badge-danger';
      case 'Deferred': return 'badge-warn';
      case 'Cancelled': return 'badge';
      default: return 'badge-info';
    }
  }

  barHeight(value: number): number {
    return Math.max(3, Math.round((value / this.maxVolume()) * 100));
  }

  private done(action: () => void): void {
    action();
    this.loading.set(false);
  }

  private fail(error: unknown): void {
    this.loading.set(false);
    this.toast.error(ApiService.toError(error).detail);
  }
}
