namespace OpenPost.Api.Contracts;

// ---------- Auth ----------
public sealed record LoginRequest(string Email, string Password, string? TotpCode, bool RememberMe = false);
public sealed record RefreshRequest(string RefreshToken);
public sealed record TokenResponse(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiresAt,
    MeResponse User);

public sealed record MeResponse(
    Guid Id,
    string Email,
    string DisplayName,
    IReadOnlyList<string> Roles,
    bool TwoFactorEnabled,
    string? Signature,
    IReadOnlyList<MailboxDto> Mailboxes);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record UpdateProfileRequest(string? DisplayName, string? Signature);
public sealed record TwoFactorSetupResponse(string SharedKey, string AuthenticatorUri);
public sealed record TwoFactorVerifyRequest(string Code);
public sealed record TwoFactorStatusResponse(bool Enabled, int RecoveryCodesRemaining);

// ---------- Mailboxes ----------
public sealed record MailboxDto(
    Guid Id,
    string Address,
    string DisplayName,
    Guid DomainId,
    string DomainName,
    long QuotaBytes,
    long UsedBytes,
    bool IsShared,
    bool IsSystem)
{
    public int QuotaUsedPercent => QuotaBytes <= 0 ? 0 : (int)Math.Min(100, UsedBytes * 100 / QuotaBytes);
}

public sealed record UpdateMailboxRequest(string? DisplayName, long? QuotaBytes);

// ---------- Folders / labels ----------
public sealed record FolderDto(Guid Id, string Name, string Kind, int SortOrder, int UnreadCount, int TotalCount);
public sealed record CreateFolderRequest(string Name);
public sealed record UpdateFolderRequest(string Name);
public sealed record LabelDto(Guid Id, string Name, string Color);
public sealed record CreateLabelRequest(string Name, string? Color);

// ---------- Messages ----------
public sealed record AddressDto(string Address, string? Name);

public sealed record MessageSummaryDto(
    Guid Id,
    Guid FolderId,
    string FolderKind,
    long Uid,
    string Subject,
    string FromName,
    string FromAddress,
    IReadOnlyList<AddressDto> To,
    string Preview,
    bool IsRead,
    bool IsFlagged,
    bool IsDraft,
    bool HasAttachments,
    long SizeBytes,
    double SpamScore,
    string SpamVerdict,
    DateTimeOffset ReceivedAt,
    string? ThreadId,
    IReadOnlyList<Guid> Labels);

public sealed record AttachmentDto(Guid Id, string FileName, string ContentType, long SizeBytes, bool IsInline);

public sealed record MessageDetailDto(
    Guid Id,
    Guid FolderId,
    string FolderKind,
    long Uid,
    string Subject,
    AddressDto From,
    IReadOnlyList<AddressDto> To,
    IReadOnlyList<AddressDto> Cc,
    IReadOnlyList<AddressDto> Bcc,
    IReadOnlyList<AddressDto> ReplyTo,
    string? BodyText,
    string? BodyHtml,
    string? RfcMessageId,
    string? InReplyTo,
    string? ThreadId,
    bool IsRead,
    bool IsFlagged,
    bool IsAnswered,
    bool IsDraft,
    long SizeBytes,
    double SpamScore,
    string SpamVerdict,
    IReadOnlyList<AttachmentDto> Attachments,
    IReadOnlyList<Guid> Labels,
    DateTimeOffset ReceivedAt);

public sealed record UpdateMessageRequest(bool? IsRead, bool? IsFlagged, Guid? FolderId, string[]? LabelIds);

public sealed record BulkActionRequest(
    string Action,                 // markRead | markUnread | flag | unflag | move | delete | spam | notSpam
    Guid[] MessageIds,
    Guid? TargetFolderId);

public sealed record SendMessageRequest(
    string[] To,
    string[]? Cc,
    string[]? Bcc,
    string Subject,
    string? TextBody,
    string? HtmlBody,
    Guid[]? AttachmentIds,
    Guid? DraftId,
    string? InReplyTo,
    string? References);

public sealed record SendMessageResponse(Guid MessageId, Guid OutboundId, string RfcMessageId);

public sealed record DraftRequest(
    string[]? To,
    string[]? Cc,
    string[]? Bcc,
    string? Subject,
    string? TextBody,
    string? HtmlBody,
    Guid[]? AttachmentIds,
    string? InReplyTo,
    string? References);

public sealed record UploadResponse(Guid Id, string FileName, long SizeBytes, string ContentType);

// ---------- Admin ----------
public sealed record DomainDto(
    Guid Id,
    string Name,
    string DisplayName,
    bool IsActive,
    int MailboxCount,
    int AliasCount,
    string DkimSelector,
    string DmarcPolicy,
    Guid? CatchAllMailboxId,
    DateTimeOffset CreatedAt);

public sealed record CreateDomainRequest(string Name, string? DisplayName, long? DefaultMailboxQuotaBytes, int? MaxMailboxes);
public sealed record UpdateDomainRequest(string? DisplayName, bool? IsActive, string? DmarcPolicy, Guid? CatchAllMailboxId, long? DefaultMailboxQuotaBytes, int? MaxMailboxes);
public sealed record DnsRecordDto(string Type, string Host, string Value, int? Priority, string Purpose);
public sealed record DnsRecordsResponse(string Domain, IReadOnlyList<DnsRecordDto> Records);

public sealed record UserDto(
    Guid Id,
    string Email,
    string DisplayName,
    bool IsActive,
    bool TwoFactorEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Mailboxes);

public sealed record CreateUserRequest(string Email, string DisplayName, string? Password, string[]? Roles, Guid[]? MailboxDomainIds);
public sealed record UpdateUserRequest(string? DisplayName, bool? IsActive, string[]? Roles);
public sealed record ResetPasswordResponse(string Password);

public sealed record AdminMailboxDto(
    Guid Id,
    string Address,
    string DisplayName,
    string DomainName,
    Guid? UserId,
    string? OwnerEmail,
    long QuotaBytes,
    long UsedBytes,
    bool IsActive,
    bool IsShared,
    DateTimeOffset CreatedAt);

public sealed record CreateMailboxRequest(
    Guid DomainId,
    string LocalPart,
    string? DisplayName,
    Guid? UserId,
    long? QuotaBytes,
    bool IsShared,
    string? Password);

public sealed record UpdateAdminMailboxRequest(string? DisplayName, long? QuotaBytes, bool? IsActive, Guid? UserId);

public sealed record AliasDto(Guid Id, string Address, IReadOnlyList<string> Targets, bool IsActive);
public sealed record CreateAliasRequest(Guid DomainId, string LocalPart, string[] Targets);

public sealed record QueueItemDto(
    Guid Id,
    string EnvelopeFrom,
    string EnvelopeTo,
    string Subject,
    string Status,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset? SentAt,
    string? LastError,
    long SizeBytes);

public sealed record StatsDto(
    int Domains,
    int Mailboxes,
    int Users,
    long Messages,
    long MessagesLast24h,
    long SentLast24h,
    long ReceivedLast24h,
    int QueuePending,
    int QueueFailed,
    long StorageUsedBytes,
    IReadOnlyList<DailyVolumeDto> Volume);

public sealed record DailyVolumeDto(DateOnly Date, long Received, long Sent, long Spam);

public sealed record AuditLogDto(
    Guid Id,
    string Action,
    string? ActorAddress,
    string? IpAddress,
    string? TargetType,
    string? TargetId,
    string? DetailJson,
    DateTimeOffset CreatedAt);

public sealed record InstanceInfoDto(
    string Product,
    string Version,
    string Environment,
    string ServerHostname,
    DateTimeOffset ServerTime,
    string Database,
    IReadOnlyList<string> EnabledFeatures);
