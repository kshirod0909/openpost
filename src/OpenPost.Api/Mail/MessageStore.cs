using Microsoft.EntityFrameworkCore;
using MimeKit;
using OpenPost.Api.Common;
using OpenPost.Api.Data;
using OpenPost.Api.Data.Entities;

namespace OpenPost.Api.Mail;

public sealed record StoreOptions
{
    public double SpamScore { get; init; }
    public SpamVerdict SpamVerdict { get; init; } = SpamVerdict.Clean;
    public string? AuthResultsJson { get; init; }
    public bool IsDraft { get; init; }
    public bool IsRead { get; init; }
    public bool EnforceQuota { get; init; } = true;
    public bool DeliverToJunk { get; init; }
    public bool KeepRaw { get; init; } = true;
}

public interface IMessageStore
{
    Task<StoredMessage> StoreAsync(Guid mailboxId, FolderKind folder, MimeMessage mime, StoreOptions options, CancellationToken ct = default);
    Task DeleteAsync(StoredMessage message, CancellationToken ct = default);
    Task<Stream> OpenRawAsync(StoredMessage message, CancellationToken ct = default);
}

public sealed class MessageStore : IMessageStore
{
    private readonly OpenPostDbContext _db;
    private readonly IMessageStorage _storage;
    private readonly ILogger<MessageStore> _log;

    public MessageStore(OpenPostDbContext db, IMessageStorage storage, ILogger<MessageStore> log)
    {
        _db = db;
        _storage = storage;
        _log = log;
    }

    public async Task<StoredMessage> StoreAsync(Guid mailboxId, FolderKind folder, MimeMessage mime, StoreOptions options, CancellationToken ct = default)
    {
        var mailbox = await _db.Mailboxes.FirstOrDefaultAsync(m => m.Id == mailboxId, ct)
            ?? throw new NotFoundException("Mailbox not found.");

        // Serialise the MIME tree once: it is the authoritative size and the raw copy on disk.
        using var buffer = new MemoryStream();
        await mime.WriteToAsync(buffer, ct);
        var size = buffer.Length;

        if (options.EnforceQuota && mailbox.QuotaBytes > 0 && mailbox.UsedBytes + size > mailbox.QuotaBytes)
        {
            _log.LogWarning("Mailbox {Address} over quota ({Used}/{Quota}); message rejected.", mailbox.Address, mailbox.UsedBytes, mailbox.QuotaBytes);
            throw new ConflictException($"Mailbox {mailbox.Address} is over quota.");
        }

        var folderId = await ResolveFolderIdAsync(mailboxId, folder, ct);
        var uid = await AllocateUidAsync(mailboxId, ct);

        var message = new StoredMessage
        {
            Id = Guid.NewGuid(),
            MailboxId = mailboxId,
            FolderId = folderId,
            Uid = uid,
            IsDraft = options.IsDraft,
            IsRead = options.IsRead,
            SpamScore = options.SpamScore,
            SpamVerdict = options.SpamVerdict,
            AuthResultsJson = options.AuthResultsJson,
            CreatedAt = DateTimeOffset.UtcNow
        };

        MimeMapper.Apply(message, mime, size);

        if (options.KeepRaw)
        {
            var rawPath = LocalDiskStorage.RawPath(mailboxId, message.Id, message.ReceivedAt);
            buffer.Position = 0;
            await _storage.WriteAsync(rawPath, buffer, ct);
        }

        await SaveAttachmentsAsync(message, mime, mailboxId, ct);

        _db.Messages.Add(message);
        mailbox.UsedBytes += size;
        await _db.SaveChangesAsync(ct);

        return message;
    }

    public async Task DeleteAsync(StoredMessage message, CancellationToken ct = default)
    {
        var mailbox = await _db.Mailboxes.FirstOrDefaultAsync(m => m.Id == message.MailboxId, ct);
        if (mailbox is not null)
            mailbox.UsedBytes = Math.Max(0, mailbox.UsedBytes - message.SizeBytes);

        foreach (var attachment in message.Attachments)
        {
            await _storage.DeleteAsync(attachment.StoragePath, ct);
            _db.Attachments.Remove(attachment);
        }

        if (!message.IsDraft)
        {
            var rawPath = LocalDiskStorage.RawPath(message.MailboxId, message.Id, message.ReceivedAt);
            try { await _storage.DeleteAsync(rawPath, ct); }
            catch (Exception ex) { _log.LogDebug(ex, "Raw message file already gone for {Id}", message.Id); }
        }

        _db.Messages.Remove(message);
        await _db.SaveChangesAsync(ct);
    }

    public Task<Stream> OpenRawAsync(StoredMessage message, CancellationToken ct = default) =>
        _storage.OpenReadAsync(LocalDiskStorage.RawPath(message.MailboxId, message.Id, message.ReceivedAt), ct);

    private async Task SaveAttachmentsAsync(StoredMessage message, MimeMessage mime, Guid mailboxId, CancellationToken ct)
    {
        foreach (var entity in mime.Attachments)
        {
            if (entity is not MimePart part) continue;

            var attachmentId = Guid.NewGuid();
            var fileName = LocalDiskStorage.SanitiseFileName(part.FileName ?? "attachment");
            var path = LocalDiskStorage.AttachmentPath(mailboxId, attachmentId, fileName);

            await using (var content = await part.Content.OpenAsync(ct))
            {
                await _storage.WriteAsync(path, content, ct);
            }

            var size = await _storage.GetSizeAsync(path, ct);
            message.Attachments.Add(new Attachment
            {
                Id = attachmentId,
                MailboxId = mailboxId,
                MessageId = message.Id,
                FileName = fileName,
                ContentType = part.ContentType.MimeType,
                SizeBytes = size,
                StoragePath = path,
                ContentId = part.ContentId,
                IsInline = part.ContentDisposition?.Disposition == ContentDisposition.Inline,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
    }

    private async Task<Guid> ResolveFolderIdAsync(Guid mailboxId, FolderKind kind, CancellationToken ct)
    {
        var folder = await _db.Folders.FirstOrDefaultAsync(f => f.MailboxId == mailboxId && f.Kind == kind, ct);
        if (folder is not null) return folder.Id;

        // Mailboxes created before a folder existed (or custom installs) get it created on demand.
        folder = new Folder
        {
            Id = Guid.NewGuid(),
            MailboxId = mailboxId,
            Name = kind.ToString(),
            Kind = kind,
            SortOrder = (int)kind
        };
        _db.Folders.Add(folder);
        await _db.SaveChangesAsync(ct);
        return folder.Id;
    }

    /// <summary>Atomic per-mailbox UID allocation; safe under concurrent SMTP sessions.</summary>
    private async Task<long> AllocateUidAsync(Guid mailboxId, CancellationToken ct)
    {
        var next = await RawSql.QueryAsync<long>(_db,
            "UPDATE \"Mailboxes\" SET \"NextUid\" = \"NextUid\" + 1 WHERE \"Id\" = @id RETURNING \"NextUid\"",
            ct, ("@id", mailboxId));

        return next.Count > 0 ? next[0] : 1;
    }
}
