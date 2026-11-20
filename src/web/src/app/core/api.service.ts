import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, throwError } from 'rxjs';
import {
  AdminMailbox,
  AdminUser,
  AuditEntry,
  Domain,
  DnsRecord,
  Folder,
  InstanceInfo,
  Label,
  Mailbox,
  Me,
  MessageDetail,
  MessageSummary,
  PagedResult,
  QueueItem,
  SendMessageRequest,
  Stats,
  TokenResponse,
  UploadResponse,
} from './models';

const BASE = '/api/v1';

export interface ApiError {
  status: number;
  title: string;
  detail: string;
}

/** Thin typed wrapper over the OpenPost REST API. */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  // ------------------------------------------------------------ auth
  login(email: string, password: string, totpCode?: string) {
    return this.http.post<TokenResponse>(`${BASE}/auth/login`, { email, password, totpCode, rememberMe: true });
  }
  refresh(refreshToken: string) {
    return this.http.post<TokenResponse>(`${BASE}/auth/refresh`, { refreshToken });
  }
  logout() {
    return this.http.post<void>(`${BASE}/auth/logout`, {});
  }
  me() {
    return this.http.get<Me>(`${BASE}/auth/me`);
  }
  updateProfile(displayName: string | null, signature: string | null) {
    return this.http.patch<Me>(`${BASE}/auth/me`, { displayName, signature });
  }
  changePassword(currentPassword: string, newPassword: string) {
    return this.http.post<void>(`${BASE}/auth/change-password`, { currentPassword, newPassword });
  }
  setupTwoFactor() {
    return this.http.post<{ sharedKey: string; authenticatorUri: string }>(`${BASE}/auth/2fa/setup`, {});
  }
  enableTwoFactor(code: string) {
    return this.http.post<void>(`${BASE}/auth/2fa/enable`, { code });
  }
  disableTwoFactor(code: string) {
    return this.http.post<void>(`${BASE}/auth/2fa/disable`, { code });
  }

  // ------------------------------------------------------------ mail
  systemInfo() {
    return this.http.get<InstanceInfo>(`${BASE}/system/info`);
  }
  mailboxes() {
    return this.http.get<Mailbox[]>(`${BASE}/mailboxes`);
  }
  folders(mailboxId: string) {
    return this.http.get<Folder[]>(`${BASE}/mailboxes/${mailboxId}/folders`);
  }
  labels(mailboxId: string) {
    return this.http.get<Label[]>(`${BASE}/mailboxes/${mailboxId}/labels`);
  }
  messages(
    mailboxId: string,
    options: { folderId?: string; folder?: string; q?: string; unread?: boolean; flagged?: boolean; page?: number; size?: number } = {},
  ) {
    let params = new HttpParams();
    if (options.folderId) params = params.set('folderId', options.folderId);
    if (options.folder) params = params.set('folder', options.folder);
    if (options.q) params = params.set('q', options.q);
    if (options.unread) params = params.set('unread', true);
    if (options.flagged) params = params.set('flagged', true);
    params = params.set('page', options.page ?? 1).set('size', options.size ?? 30);
    return this.http.get<PagedResult<MessageSummary>>(`${BASE}/mailboxes/${mailboxId}/messages`, { params });
  }
  message(id: string) {
    return this.http.get<MessageDetail>(`${BASE}/messages/${id}`);
  }
  updateMessage(id: string, patch: { isRead?: boolean; isFlagged?: boolean; folderId?: string; labelIds?: string[] }) {
    return this.http.patch<MessageDetail>(`${BASE}/messages/${id}`, patch);
  }
  deleteMessage(id: string) {
    return this.http.delete<void>(`${BASE}/messages/${id}`);
  }
  bulk(mailboxId: string, action: string, messageIds: string[], targetFolderId?: string) {
    return this.http.post<{ affected: number }>(`${BASE}/mailboxes/${mailboxId}/messages/bulk`, {
      action,
      messageIds,
      targetFolderId,
    });
  }
  send(mailboxId: string, request: SendMessageRequest) {
    return this.http.post<{ messageId: string; outboundId: string; rfcMessageId: string }>(
      `${BASE}/mailboxes/${mailboxId}/send`,
      request,
    );
  }
  saveDraft(mailboxId: string, request: Partial<SendMessageRequest>) {
    return this.http.post<MessageDetail>(`${BASE}/mailboxes/${mailboxId}/drafts`, request);
  }
  upload(mailboxId: string, file: File) {
    const form = new FormData();
    form.append('file', file, file.name);
    return this.http.post<UploadResponse>(`${BASE}/mailboxes/${mailboxId}/attachments`, form);
  }
  rawUrl(id: string) {
    return `${BASE}/messages/${id}/raw`;
  }
  attachmentUrl(messageId: string, attachmentId: string) {
    return `${BASE}/messages/${messageId}/attachments/${attachmentId}`;
  }

  // ------------------------------------------------------------ admin
  domains() {
    return this.http.get<Domain[]>(`${BASE}/admin/domains`);
  }
  createDomain(body: { name: string; displayName?: string }) {
    return this.http.post<Domain>(`${BASE}/admin/domains`, body);
  }
  updateDomain(id: string, body: Record<string, unknown>) {
    return this.http.patch<Domain>(`${BASE}/admin/domains/${id}`, body);
  }
  deleteDomain(id: string) {
    return this.http.delete<void>(`${BASE}/admin/domains/${id}`);
  }
  dnsRecords(id: string) {
    return this.http.get<{ domain: string; records: DnsRecord[] }>(`${BASE}/admin/domains/${id}/dns-records`);
  }
  rotateDkim(id: string) {
    return this.http.post<{ domain: string; records: DnsRecord[] }>(`${BASE}/admin/domains/${id}/dkim/rotate`, {});
  }
  adminMailboxes(domainId?: string, q?: string) {
    let params = new HttpParams();
    if (domainId) params = params.set('domainId', domainId);
    if (q) params = params.set('q', q);
    return this.http.get<AdminMailbox[]>(`${BASE}/admin/mailboxes`, { params });
  }
  createMailbox(body: {
    domainId: string;
    localPart: string;
    displayName?: string;
    quotaBytes?: number;
    isShared?: boolean;
    password?: string;
  }) {
    return this.http.post<{ mailbox: AdminMailbox; generatedPassword?: string | null }>(`${BASE}/admin/mailboxes`, body);
  }
  deleteMailbox(id: string) {
    return this.http.delete<void>(`${BASE}/admin/mailboxes/${id}`);
  }
  users(q?: string) {
    let params = new HttpParams();
    if (q) params = params.set('q', q);
    return this.http.get<AdminUser[]>(`${BASE}/admin/users`, { params });
  }
  createUser(body: { email: string; displayName: string; password?: string; roles?: string[] }) {
    return this.http.post<{ user: AdminUser; generatedPassword?: string | null }>(`${BASE}/admin/users`, body);
  }
  resetPassword(id: string) {
    return this.http.post<{ password: string }>(`${BASE}/admin/users/${id}/reset-password`, {});
  }
  queue(status?: string) {
    let params = new HttpParams().set('size', 100);
    if (status) params = params.set('status', status);
    return this.http.get<PagedResult<QueueItem>>(`${BASE}/admin/queue`, { params });
  }
  retryQueueItem(id: string) {
    return this.http.post<void>(`${BASE}/admin/queue/${id}/retry`, {});
  }
  cancelQueueItem(id: string) {
    return this.http.delete<void>(`${BASE}/admin/queue/${id}`);
  }
  processQueue() {
    return this.http.post<{ processed: number }>(`${BASE}/admin/queue/process`, {});
  }
  stats() {
    return this.http.get<Stats>(`${BASE}/admin/stats`);
  }
  audit(page = 1) {
    let params = new HttpParams().set('page', page).set('size', 50);
    return this.http.get<PagedResult<AuditEntry>>(`${BASE}/admin/audit`, { params });
  }

  static toError(error: unknown): ApiError {
    if (error instanceof HttpErrorResponse) {
      const body = error.error ?? {};
      return {
        status: error.status,
        title: body.title ?? (error.status === 0 ? 'Cannot reach the server' : error.statusText),
        detail: body.detail ?? body.message ?? 'Please try again.',
      };
    }
    return { status: 0, title: 'Unexpected error', detail: String(error) };
  }
}

export function handleError<T>(source: Observable<T>): Observable<T> {
  return source.pipe(catchError((error) => throwError(() => ApiService.toError(error))));
}
