using System.Net;
using System.Text.Json;
using DnsClient;
using DnsClient.Protocol;
using MimeKit;
using MimeKit.Cryptography;

namespace OpenPost.Api.Mail.Authentication;

public enum SpfOutcome { None, Neutral, Pass, Fail, SoftFail, TempError, PermError }

public sealed record AuthenticationResult(
    string ClientIp,
    string FromDomain,
    SpfOutcome Spf,
    bool? DkimValid,
    string? DmarcPolicy,
    bool DmarcAligned)
{
    public string SpfText => Spf.ToString().ToLowerInvariant();

    public string ToJson() => JsonSerializer.Serialize(new
    {
        clientIp = ClientIp,
        fromDomain = FromDomain,
        spf = SpfText,
        dkim = DkimValid switch { true => "pass", false => "fail", null => "none" },
        dmarc = DmarcPolicy,
        dmarcAligned = DmarcAligned
    });

    /// <summary>Score contribution used by the built-in spam heuristics.</summary>
    public double SpamWeight()
    {
        double score = 0;
        if (Spf is SpfOutcome.Fail or SpfOutcome.SoftFail) score += 1.5;
        if (Spf is SpfOutcome.None) score += 0.5;
        if (DkimValid == false) score += 1.5;
        if (DkimValid is null) score += 0.3;
        if (DmarcPolicy is "reject" or "quarantine" && !DmarcAligned) score += 2.0;
        return score;
    }
}

/// <summary>
/// Evaluates SPF, DKIM and DMARC for an inbound message. This is a deliberately small
/// implementation of RFC 7208 / 6376 / 7489 covering the mechanisms self-hosted deployments
/// actually use; a full policy engine (redirect=, macros, PSL) is delegated to Rspamd when
/// enabled - see SpamOptions.RspamdUrl.
/// </summary>
public sealed class AuthenticationEvaluator
{
    private readonly LookupClient _dns;
    private readonly ILogger<AuthenticationEvaluator> _log;

    public AuthenticationEvaluator(ILogger<AuthenticationEvaluator> log)
    {
        _log = log;
        _dns = new LookupClient(new LookupClientOptions { Timeout = TimeSpan.FromSeconds(3), Retries = 1, UseCache = true });
    }

    public async Task<AuthenticationResult> EvaluateAsync(MimeMessage message, string clientIp, CancellationToken ct)
    {
        var from = message.From.OfType<MailboxAddress>().FirstOrDefault();
        var fromDomain = from?.Address is null ? string.Empty : Common.Addresses.DomainPart(from.Address);

        var spf = SpfOutcome.None;
        if (IPAddress.TryParse(clientIp, out var ip) && fromDomain.Length > 0)
        {
            try { spf = await CheckSpfAsync(ip, fromDomain, ct); }
            catch (Exception ex) { _log.LogDebug(ex, "SPF lookup failed for {Domain}", fromDomain); }
        }

        bool? dkim = null;
        try
        {
            var verifier = new DkimVerifier(_dns);
            dkim = await verifier.Verify(message, ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "DKIM verification failed");
        }

        string? dmarcPolicy = null;
        var aligned = false;
        if (fromDomain.Length > 0)
        {
            try
            {
                dmarcPolicy = await GetDmarcPolicyAsync(fromDomain, ct);
                var dkimDomain = message.Headers[HeaderId.DkimSignature] ?? string.Empty;
                aligned = (dkim == true && dkimDomain.Contains(fromDomain, StringComparison.OrdinalIgnoreCase))
                          || spf == SpfOutcome.Pass;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "DMARC lookup failed for {Domain}", fromDomain);
            }
        }

        return new AuthenticationResult(clientIp, fromDomain, spf, dkim, dmarcPolicy, aligned);
    }

    private async Task<SpfOutcome> CheckSpfAsync(IPAddress ip, string domain, CancellationToken ct, int depth = 0)
    {
        if (depth > 5) return SpfOutcome.PermError;

        var txt = await _dns.QueryAsync(domain, QueryType.TXT, cancellationToken: ct);
        var record = txt.Answers.TxtRecords()
            .Select(r => string.Concat(r.Text))
            .FirstOrDefault(r => r.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase));

        if (record is null) return SpfOutcome.None;

        var qualifier = SpfOutcome.Neutral;
        foreach (var raw in record.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var term = raw.Trim();
            var q = SpfOutcome.Pass;
            if (term.StartsWith('-')) { q = SpfOutcome.Fail; term = term[1..]; }
            else if (term.StartsWith('~')) { q = SpfOutcome.SoftFail; term = term[1..]; }
            else if (term.StartsWith('?')) { q = SpfOutcome.Neutral; term = term[1..]; }
            else if (term.StartsWith('+')) { term = term[1..]; }

            if (term.Equals("all", StringComparison.OrdinalIgnoreCase)) return q;

            if (term.StartsWith("ip4:", StringComparison.OrdinalIgnoreCase)
                && IPAddress.TryParse(term[4..], out var v4) && v4.Equals(ip))
                return q;

            if (term.StartsWith("ip6:", StringComparison.OrdinalIgnoreCase)
                && IPAddress.TryParse(term[4..], out var v6) && v6.Equals(ip))
                return q;

            if (term.StartsWith("include:", StringComparison.OrdinalIgnoreCase))
            {
                var included = await CheckSpfAsync(ip, term[8..], ct, depth + 1);
                if (included == SpfOutcome.Pass) return q;
                if (included is SpfOutcome.PermError or SpfOutcome.TempError) qualifier = included;
                continue;
            }

            if (term.Equals("a", StringComparison.OrdinalIgnoreCase) || term.StartsWith("a:", StringComparison.OrdinalIgnoreCase))
            {
                var host = term.Length > 2 ? term[2..] : domain;
                var addresses = await ResolveHostAddressesAsync(host, ct);
                if (addresses.Contains(ip)) return q;
                continue;
            }

            if (term.Equals("mx", StringComparison.OrdinalIgnoreCase))
            {
                var mx = await _dns.QueryAsync(domain, QueryType.MX, cancellationToken: ct);
                foreach (var exchange in mx.Answers.MxRecords())
                {
                    var addresses = await ResolveHostAddressesAsync(exchange.Exchange.Value, ct);
                    if (addresses.Contains(ip)) return q;
                }
            }
        }

        return qualifier;
    }

    private async Task<HashSet<IPAddress>> ResolveHostAddressesAsync(string host, CancellationToken ct)
    {
        var result = new HashSet<IPAddress>();
        try
        {
            var a = await _dns.QueryAsync(host, QueryType.A, cancellationToken: ct);
            foreach (var record in a.Answers.ARecords()) result.Add(record.Address);
            var aaaa = await _dns.QueryAsync(host, QueryType.AAAA, cancellationToken: ct);
            foreach (var record in aaaa.Answers.AaaaRecords()) result.Add(record.Address);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "DNS resolution failed for {Host}", host);
        }
        return result;
    }

    private async Task<string?> GetDmarcPolicyAsync(string domain, CancellationToken ct)
    {
        var answer = await _dns.QueryAsync("_dmarc." + domain, QueryType.TXT, cancellationToken: ct);
        var record = answer.Answers.TxtRecords()
            .Select(r => string.Concat(r.Text))
            .FirstOrDefault(r => r.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase));

        if (record is null) return null;
        var policy = record.Split(';')
            .Select(p => p.Trim())
            .FirstOrDefault(p => p.StartsWith("p=", StringComparison.OrdinalIgnoreCase));
        return policy?[2..].Trim().ToLowerInvariant();
    }
}
