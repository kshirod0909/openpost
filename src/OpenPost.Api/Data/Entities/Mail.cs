namespace OpenPost.Api.Data.Entities;

/// <summary>A mail domain hosted on this instance (e.g. example.org).</summary>
public sealed class MailDomain
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;          // normalised lowercase
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Owner (a domain administrator). Null for system-created domains.</summary>
    public Guid? OwnerUserId { get; set; }

    /// <summary>Mailbox that receives mail for unknown local parts when catch-all is enabled.</summary>
    public Guid? CatchAllMailboxId { get; set; }

    public int MaxMailboxes { get; set; } = 100;
    public long DefaultMailboxQuotaBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    // DKIM
    public string DkimSelector { get; set; } = "openpost";
    public string? DkimPrivateKeyPem { get; set; }
    public string? DkimPublicKeyBase64 { get; set; }

    // DMARC policy advertised in generated DNS records
    public string DmarcPolicy { get; set; } = "quarantine";    // none | quarantine | reject

    public ICollection<Mailbox> Mailboxes { get; set; } = new List<Mailbox>();
}

/// <summary>A single mailbox (user@domain). Also used for role accounts and shared inboxes.</summary>
public sealed class Mailbox
{
    public Guid Id { get; set; }
    public Guid DomainId { get; set; }
    public string LocalPart { get; set; } = string.Empty;      // normalised lowercase
    public string Address { get; set; } = string.Empty;        // localpart@domain, unique
    public string DisplayName { get; set; } = string.Empty;
    public Guid? UserId { get; set; }
    public long QuotaBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public long UsedBytes { get; set; }
    public long NextUid { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public bool IsSystem { get; set; }                          // postmaster/abuse, not deletable
    public bool IsShared { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public MailDomain? Domain { get; set; }
    public AppUser? User { get; set; }
    public ICollection<Folder> Folders { get; set; } = new List<Folder>();
    public ICollection<Label> Labels { get; set; } = new List<Label>();
}

/// <summary>Alias / distribution address that forwards to one or more targets.</summary>
public sealed class MailAlias
{
    public Guid Id { get; set; }
    public Guid DomainId { get; set; }
    public string LocalPart { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;        // unique
    public bool IsActive { get; set; } = true;

    /// <summary>JSON array of destination addresses (local or remote).</summary>
    public string TargetsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum FolderKind
{
    Inbox = 0,
    Sent = 1,
    Drafts = 2,
    Trash = 3,
    Junk = 4,
    Archive = 5,
    Custom = 6
}

public sealed class Folder
{
    public Guid Id { get; set; }
    public Guid MailboxId { get; set; }
    public string Name { get; set; } = string.Empty;
    public FolderKind Kind { get; set; } = FolderKind.Custom;
    public int SortOrder { get; set; }
    public Mailbox? Mailbox { get; set; }
}

public sealed class Label
{
    public Guid Id { get; set; }
    public Guid MailboxId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#6366f1";
    public Mailbox? Mailbox { get; set; }
}

public enum SpamVerdict
{
    Clean = 0,
    Suspect = 1,
    Spam = 2
}

/// <summary>A stored message inside a mailbox folder.</summary>
public sealed class StoredMessage
{
    public Guid Id { get; set; }
    public Guid MailboxId { get; set; }
    public Guid FolderId { get; set; }

    /// <summary>Per-mailbox IMAP-style UID, monotonically increasing.</summary>
    public long Uid { get; set; }

    public string? RfcMessageId { get; set; }
    public string? ThreadId { get; set; }
    public string? InReplyTo { get; set; }
    public string? References { get; set; }

    public string Subject { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string ToJson { get; set; } = "[]";
    public string CcJson { get; set; } = "[]";
    public string BccJson { get; set; } = "[]";
    public string? ReplyToJson { get; set; }

    public string Preview { get; set; } = string.Empty;
    public string? BodyText { get; set; }
    public string? BodyHtml { get; set; }

    public bool IsRead { get; set; }
    public bool IsFlagged { get; set; }
    public bool IsAnswered { get; set; }
    public bool IsDraft { get; set; }
    public bool IsDeleted { get; set; }
    public bool HasAttachments { get; set; }

    public long SizeBytes { get; set; }
    public double SpamScore { get; set; }
    public SpamVerdict SpamVerdict { get; set; }
    public string? AuthResultsJson { get; set; }

    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Label ids as a JSON array (kept denormalised to avoid a join table on hot paths).</summary>
    public string LabelsJson { get; set; } = "[]";

    public Mailbox? Mailbox { get; set; }
    public Folder? Folder { get; set; }
    public ICollection<Attachment> Attachments { get; set; } = new List<Attachment>();
}

public sealed class Attachment
{
    public Guid Id { get; set; }
    public Guid? MessageId { get; set; }          // null while uploading a draft/compose attachment
    public Guid MailboxId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public string StoragePath { get; set; } = string.Empty;   // relative to configured root
    public string? ContentId { get; set; }
    public bool IsInline { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public StoredMessage? Message { get; set; }
}
