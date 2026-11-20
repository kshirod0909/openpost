using Microsoft.EntityFrameworkCore;
using MimeKit;
using OpenPost.Api.Common;
using OpenPost.Api.Contracts;
using OpenPost.Api.Data;
using OpenPost.Api.Data.Entities;
using OpenPost.Api.Mail.Authentication;

namespace OpenPost.Api.Mail;

public interface IMailSubmissionService
{
    Task<SendMessageResponse> SendAsync(Mailbox sender, SendMessageRequest request, CancellationToken ct);

    Task<SendMessageResponse> SendMimeAsync(
        Mailbox sender,
        MimeMessage message,
        IReadOnlyList<string> recipients,
        CancellationToken ct);
}

/// <summary>
/// One submission path for the web API and for SMTP submission: store a copy in Sent, deliver
/// to local recipients immediately, queue everything remote.
/// </summary>
public sealed class MailSubmissionService : IMailSubmissionService
{
    private readonly OpenPostDbContext _db;
    private readonly IMessageStore _store;
    private readonly IOutboundQueue _outbound;
    private readonly MailRouter _router;
    private readonly RecipientResolver _resolver;
    private readonly MimeFactory _factory;
    private readonly ILogger<MailSubmissionService> _log;

    public MailSubmissionService(
        OpenPostDbContext db,
        IMessageStore store,
        IOutboundQueue outbound,
        MailRouter router,
        RecipientResolver resolver,
        MimeFactory factory,
        ILogger<MailSubmissionService> log)
    {
        _db = db;
        _store = store;
        _outbound = outbound;
        _router = router;
        _resolver = resolver;
        _factory = factory;
        _log = log;
    }

    public async Task<SendMessageResponse> SendAsync(Mailbox sender, SendMessageRequest request, CancellationToken ct)
    {
        var allRecipients = (request.To ?? Array.Empty<string>())
            .Concat(request.Cc ?? Array.Empty<string>())
            .Concat(request.Bcc ?? Array.Empty<string>())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .ToList();

        if (allRecipients.Count == 0) throw new ValidationFailedException("At least one recipient is required.");
        if (allRecipients.Count > 200) throw new ValidationFailedException("Too many recipients (max 200).");

        foreach (var address in allRecipients)
            if (!Addresses.LooksValid(address))
                throw new ValidationFailedException($"'{address}' is not a valid email address.");

        var attachments = Array.Empty<Attachment>();
        if (request.AttachmentIds is { Length: > 0 })
        {
            attachments = await _db.Attachments
                .Where(a => request.AttachmentIds.Contains(a.Id) && a.MailboxId == sender.Id && a.MessageId == null)
                .ToArrayAsync(ct);
        }

        var message = await _factory.BuildAsync(sender, request, attachments, ct);

        if (request.DraftId is { } draftId)
        {
            var draft = await _db.Messages.Include(m => m.Attachments)
                .FirstOrDefaultAsync(m => m.Id == draftId && m.MailboxId == sender.Id && m.IsDraft, ct);
            if (draft is not null) await _store.DeleteAsync(draft, ct);
        }

        return await SendMimeAsync(sender, message, allRecipients, ct);
    }

    public async Task<SendMessageResponse> SendMimeAsync(
        Mailbox sender,
        MimeMessage message,
        IReadOnlyList<string> recipients,
        CancellationToken ct)
    {
        var sent = await _store.StoreAsync(sender.Id, FolderKind.Sent, message,
            new StoreOptions { IsRead = true, SpamScore = 0, SpamVerdict = SpamVerdict.Clean, EnforceQuota = false }, ct);

        var remote = new List<string>();
        var local = new List<string>();

        foreach (var recipient in recipients.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (await _resolver.CanAcceptAsync(recipient, ct)) local.Add(recipient);
            else remote.Add(recipient);
        }

        if (local.Count > 0)
        {
            var clean = new SpamResult(0, SpamVerdict.Clean, Array.Empty<string>());
            await _router.RouteInboundAsync(message, sender.Address, local, clean, null, ct);
        }

        Guid outboundId = Guid.Empty;
        if (remote.Count > 0)
        {
            var queued = await _outbound.EnqueueAsync(message, sender.Address, remote, sender.Id, sent.Id, ct);
            outboundId = queued.Id;
        }

        _log.LogInformation("Message {Id} submitted from {Sender}: {Local} local, {Remote} remote",
            sent.Id, sender.Address, local.Count, remote.Count);

        return new SendMessageResponse(sent.Id, outboundId, sent.RfcMessageId ?? string.Empty);
    }

    /// <summary>Persists a draft and returns the stored row.</summary>
    public async Task<StoredMessage> SaveDraftAsync(Mailbox sender, DraftRequest request, CancellationToken ct)
    {
        var attachments = Array.Empty<Attachment>();
        if (request.AttachmentIds is { Length: > 0 })
        {
            attachments = await _db.Attachments
                .Where(a => request.AttachmentIds.Contains(a.Id) && a.MailboxId == sender.Id && a.MessageId == null)
                .ToArrayAsync(ct);
        }

        var send = new SendMessageRequest(
            request.To ?? Array.Empty<string>(),
            request.Cc, request.Bcc,
            request.Subject ?? "(no subject)",
            request.TextBody, request.HtmlBody,
            request.AttachmentIds, null,
            request.InReplyTo, request.References);

        var mime = await _factory.BuildAsync(sender, send, attachments, ct);

        return await _store.StoreAsync(sender.Id, FolderKind.Drafts, mime,
            new StoreOptions { IsDraft = true, IsRead = true, EnforceQuota = false }, ct);
    }
}
