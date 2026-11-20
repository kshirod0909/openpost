namespace OpenPost.Api.Data.Entities;

public enum OutboundStatus
{
    Queued = 0,
    InProgress = 1,
    Sent = 2,
    Deferred = 3,
    Failed = 4,
    Cancelled = 5
}

/// <summary>
/// One row per outbound delivery job. The row owns the fully rendered MIME payload so the
/// worker can retry without re-signing or re-reading mailbox state.
/// </summary>
public sealed class OutboundMessage
{
    public Guid Id { get; set; }
    public Guid? MailboxId { get; set; }
    public Guid? SourceMessageId { get; set; }
    public string EnvelopeFrom { get; set; } = string.Empty;
    public string EnvelopeTo { get; set; } = string.Empty;     // comma separated
    public string Subject { get; set; } = string.Empty;
    public string MimePath { get; set; } = string.Empty;       // relative to attachment root
    public long SizeBytes { get; set; }

    public OutboundStatus Status { get; set; } = OutboundStatus.Queued;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; set; }
    public string? LastError { get; set; }
}

public sealed class AuditLog
{
    public Guid Id { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? ActorAddress { get; set; }
    public string? IpAddress { get; set; }
    public string Action { get; set; } = string.Empty;         // e.g. domain.create
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string? DetailJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Key/value instance settings editable by a system administrator.</summary>
public sealed class InstanceSetting
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
