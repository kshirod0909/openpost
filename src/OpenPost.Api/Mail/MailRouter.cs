using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;
using OpenPost.Api.Common;
using OpenPost.Api.Data;
using OpenPost.Api.Data.Entities;
using OpenPost.Api.Mail.Authentication;
using OpenPost.Api.Security;

namespace OpenPost.Api.Mail;

public sealed record RouteResult(IReadOnlyList<Guid> DeliveredMailboxIds, IReadOnlyList<string> UnknownRecipients, int Forwarded);

/// <summary>Maps an address to whatever it resolves to on this instance.</summary>
public sealed class RecipientResolver
{
    private readonly OpenPostDbContext _db;

    public RecipientResolver(OpenPostDbContext db) => _db = db;

    public Task<Mailbox?> FindMailboxAsync(string address, CancellationToken ct) =>
        _db.Mailboxes.FirstOrDefaultAsync(m => m.Address == Addresses.Normalise(address) && m.IsActive, ct);

    public Task<MailAlias?> FindAliasAsync(string address, CancellationToken ct) =>
        _db.Aliases.FirstOrDefaultAsync(a => a.Address == Addresses.Normalise(address) && a.IsActive, ct);

    public async Task<MailDomain?> FindDomainAsync(string domainName, CancellationToken ct) =>
        await _db.Domains.FirstOrDefaultAsync(d => d.Name == domainName.ToLowerInvariant() && d.IsActive, ct);

    public async Task<Mailbox?> FindCatchAllAsync(string domainName, CancellationToken ct)
    {
        var domain = await FindDomainAsync(domainName, ct);
        if (domain?.CatchAllMailboxId is null) return null;
        return await _db.Mailboxes.FirstOrDefaultAsync(m => m.Id == domain.CatchAllMailboxId && m.IsActive, ct);
    }

    /// <summary>True when this instance accepts mail for the domain.</summary>
    public async Task<bool> IsLocalDomainAsync(string domainName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(domainName)) return false;
        return await _db.Domains.AnyAsync(d => d.Name == domainName.ToLowerInvariant() && d.IsActive, ct);
    }

    /// <summary>Can mail for this address be accepted for local delivery?</summary>
    public async Task<bool> CanAcceptAsync(string address, CancellationToken ct)
    {
        if (!Addresses.LooksValid(address)) return false;
        if (await FindMailboxAsync(address, ct) is not null) return true;
        if (await FindAliasAsync(address, ct) is not null) return true;
        var domain = Addresses.DomainPart(address);
        return await FindCatchAllAsync(domain, ct) is not null;
    }
}

/// <summary>
/// Delivers inbound mail: local mailboxes, alias expansion (local + forwarded) and catch-all.
/// Spam scoring and virus scanning happen once, in the caller, before routing.
/// </summary>
public sealed class MailRouter
{
    private readonly RecipientResolver _resolver;
    private readonly IMessageStore _store;
    private readonly IOutboundQueue _outbound;
    private readonly SpamOptions _spam;
    private readonly ILogger<MailRouter> _log;

    public MailRouter(
        RecipientResolver resolver,
        IMessageStore store,
        IOutboundQueue outbound,
        IOptions<SpamOptions> spam,
        ILogger<MailRouter> log)
    {
        _resolver = resolver;
        _store = store;
        _outbound = outbound;
        _spam = spam.Value;
        _log = log;
    }

    public async Task<RouteResult> RouteInboundAsync(
        MimeMessage message,
        string envelopeFrom,
        IReadOnlyList<string> recipients,
        SpamResult spam,
        AuthenticationResult? auth,
        CancellationToken ct)
    {
        var delivered = new List<Guid>();
        var unknown = new List<string>();
        var forwarded = 0;

        var options = new StoreOptions
        {
            SpamScore = spam.Score,
            SpamVerdict = spam.Verdict,
            AuthResultsJson = auth?.ToJson(),
            DeliverToJunk = _spam.DeliverSpamToJunk && spam.Verdict == SpamVerdict.Spam,
            IsRead = false
        };

        foreach (var recipient in recipients.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var mailbox = await _resolver.FindMailboxAsync(recipient, ct);
            if (mailbox is not null)
            {
                if (await TryDeliverAsync(mailbox, message, options, ct)) delivered.Add(mailbox.Id);
                continue;
            }

            var alias = await _resolver.FindAliasAsync(recipient, ct);
            if (alias is not null)
            {
                var targets = MimeMapper.FromJson(alias.TargetsJson).Select(t => t.Address).ToList();
                foreach (var target in targets)
                {
                    var localTarget = await _resolver.FindMailboxAsync(target, ct);
                    if (localTarget is not null)
                    {
                        if (await TryDeliverAsync(localTarget, message, options, ct)) delivered.Add(localTarget.Id);
                    }
                    else
                    {
                        // Forwarded mail leaves with our postmaster envelope sender so SPF still
                        // passes for the forwarding hop (full SRS rewriting is on the roadmap).
                        var envelopeSender = await BuildForwardEnvelopeSenderAsync(recipient, ct);
                        await _outbound.EnqueueAsync(message, envelopeSender, new[] { target }, null, null, ct);
                        forwarded++;
                    }
                }
                continue;
            }

            var catchAll = await _resolver.FindCatchAllAsync(Addresses.DomainPart(recipient), ct);
            if (catchAll is not null)
            {
                if (await TryDeliverAsync(catchAll, message, options, ct)) delivered.Add(catchAll.Id);
                continue;
            }

            unknown.Add(recipient);
        }

        return new RouteResult(delivered, unknown, forwarded);
    }

    private async Task<bool> TryDeliverAsync(Mailbox mailbox, MimeMessage message, StoreOptions options, CancellationToken ct)
    {
        var folder = options.DeliverToJunk ? FolderKind.Junk : FolderKind.Inbox;
        try
        {
            await _store.StoreAsync(mailbox.Id, folder, message, options, ct);
            return true;
        }
        catch (ConflictException ex)
        {
            _log.LogWarning("Delivery to {Address} skipped: {Reason}", mailbox.Address, ex.Message);
            return false;
        }
    }

    private async Task<string> BuildForwardEnvelopeSenderAsync(string recipient, CancellationToken ct)
    {
        var domainName = Addresses.DomainPart(recipient);
        var domain = await _resolver.FindDomainAsync(domainName, ct);
        return $"postmaster@{domain?.Name ?? domainName}";
    }
}
