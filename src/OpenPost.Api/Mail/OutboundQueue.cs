using MimeKit;
using OpenPost.Api.Data;
using OpenPost.Api.Data.Entities;

namespace OpenPost.Api.Mail;

public interface IOutboundQueue
{
    Task<OutboundMessage> EnqueueAsync(
        MimeMessage message,
        string envelopeFrom,
        IEnumerable<string> recipients,
        Guid? mailboxId,
        Guid? sourceMessageId,
        CancellationToken ct);
}

/// <summary>
/// Signs, persists and queues a message for outbound delivery. The rendered MIME is written to
/// disk so retries are byte-identical and survive a restart.
/// </summary>
public sealed class OutboundQueue : IOutboundQueue
{
    private readonly OpenPostDbContext _db;
    private readonly IMessageStorage _storage;
    private readonly IDkimSigner _dkim;
    private readonly ILogger<OutboundQueue> _log;

    public OutboundQueue(OpenPostDbContext db, IMessageStorage storage, IDkimSigner dkim, ILogger<OutboundQueue> log)
    {
        _db = db;
        _storage = storage;
        _dkim = dkim;
        _log = log;
    }

    public async Task<OutboundMessage> EnqueueAsync(
        MimeMessage message,
        string envelopeFrom,
        IEnumerable<string> recipients,
        Guid? mailboxId,
        Guid? sourceMessageId,
        CancellationToken ct)
    {
        var signed = await _dkim.SignAsync(message, envelopeFrom, ct);

        var id = Guid.NewGuid();
        var path = LocalDiskStorage.OutboundPath(id);

        using var buffer = new MemoryStream();
        await signed.WriteToAsync(buffer, ct);
        buffer.Position = 0;
        await _storage.WriteAsync(path, buffer, ct);

        var row = new OutboundMessage
        {
            Id = id,
            MailboxId = mailboxId,
            SourceMessageId = sourceMessageId,
            EnvelopeFrom = envelopeFrom,
            EnvelopeTo = string.Join(',', recipients.Select(Common.Addresses.Normalise)),
            Subject = message.Subject ?? string.Empty,
            MimePath = path,
            SizeBytes = buffer.Length,
            Status = OutboundStatus.Queued,
            NextAttemptAt = DateTimeOffset.UtcNow
        };

        _db.Outbound.Add(row);
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Queued outbound {Id} from {From} to {To}", id, envelopeFrom, row.EnvelopeTo);
        return row;
    }
}
