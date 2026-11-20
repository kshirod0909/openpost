export interface Address {
  address: string;
  name?: string | null;
}

export interface Mailbox {
  id: string;
  address: string;
  displayName: string;
  domainId: string;
  domainName: string;
  quotaBytes: number;
  usedBytes: number;
  quotaUsedPercent: number;
  isShared: boolean;
  isSystem: boolean;
}

export interface Me {
  id: string;
  email: string;
  displayName: string;
  roles: string[];
  twoFactorEnabled: boolean;
  signature?: string | null;
  mailboxes: Mailbox[];
}

export interface TokenResponse {
  accessToken: string;
  refreshToken: string;
  accessTokenExpiresAt: string;
  user: Me;
}

export interface Folder {
  id: string;
  name: string;
  kind: string;
  sortOrder: number;
  unreadCount: number;
  totalCount: number;
}

export interface Label {
  id: string;
  name: string;
  color: string;
}

export interface MessageSummary {
  id: string;
  folderId: string;
  folderKind: string;
  uid: number;
  subject: string;
  fromName: string;
  fromAddress: string;
  to: Address[];
  preview: string;
  isRead: boolean;
  isFlagged: boolean;
  isDraft: boolean;
  hasAttachments: boolean;
  sizeBytes: number;
  spamScore: number;
  spamVerdict: string;
  receivedAt: string;
  threadId?: string | null;
  labels: string[];
}

export interface Attachment {
  id: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  isInline: boolean;
}

export interface MessageDetail {
  id: string;
  folderId: string;
  folderKind: string;
  uid: number;
  subject: string;
  from: Address;
  to: Address[];
  cc: Address[];
  bcc: Address[];
  replyTo: Address[];
  bodyText?: string | null;
  bodyHtml?: string | null;
  rfcMessageId?: string | null;
  inReplyTo?: string | null;
  threadId?: string | null;
  isRead: boolean;
  isFlagged: boolean;
  isAnswered: boolean;
  isDraft: boolean;
  sizeBytes: number;
  spamScore: number;
  spamVerdict: string;
  attachments: Attachment[];
  labels: string[];
  receivedAt: string;
}

export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
  hasNext: boolean;
}

export interface SendMessageRequest {
  to: string[];
  cc?: string[];
  bcc?: string[];
  subject: string;
  textBody?: string | null;
  htmlBody?: string | null;
  attachmentIds?: string[];
  draftId?: string | null;
  inReplyTo?: string | null;
  references?: string | null;
}

export interface UploadResponse {
  id: string;
  fileName: string;
  sizeBytes: number;
  contentType: string;
}

// ---- admin ----
export interface Domain {
  id: string;
  name: string;
  displayName: string;
  isActive: boolean;
  mailboxCount: number;
  aliasCount: number;
  dkimSelector: string;
  dmarcPolicy: string;
  catchAllMailboxId?: string | null;
  createdAt: string;
}

export interface DnsRecord {
  type: string;
  host: string;
  value: string;
  priority?: number | null;
  purpose: string;
}

export interface AdminMailbox {
  id: string;
  address: string;
  displayName: string;
  domainName: string;
  userId?: string | null;
  ownerEmail?: string | null;
  quotaBytes: number;
  usedBytes: number;
  isActive: boolean;
  isShared: boolean;
  createdAt: string;
}

export interface AdminUser {
  id: string;
  email: string;
  displayName: string;
  isActive: boolean;
  twoFactorEnabled: boolean;
  createdAt: string;
  lastLoginAt?: string | null;
  roles: string[];
  mailboxes: string[];
}

export interface QueueItem {
  id: string;
  envelopeFrom: string;
  envelopeTo: string;
  subject: string;
  status: string;
  attempts: number;
  createdAt: string;
  nextAttemptAt: string;
  sentAt?: string | null;
  lastError?: string | null;
  sizeBytes: number;
}

export interface Stats {
  domains: number;
  mailboxes: number;
  users: number;
  messages: number;
  messagesLast24h: number;
  sentLast24h: number;
  receivedLast24h: number;
  queuePending: number;
  queueFailed: number;
  storageUsedBytes: number;
  volume: { date: string; received: number; sent: number; spam: number }[];
}

export interface AuditEntry {
  id: string;
  action: string;
  actorAddress?: string | null;
  ipAddress?: string | null;
  targetType?: string | null;
  targetId?: string | null;
  detailJson?: string | null;
  createdAt: string;
}

export interface InstanceInfo {
  product: string;
  version: string;
  environment: string;
  serverHostname: string;
  serverTime: string;
  database: string;
  enabledFeatures: string[];
}
