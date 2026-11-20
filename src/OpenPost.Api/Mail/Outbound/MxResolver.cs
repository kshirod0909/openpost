using DnsClient;
using DnsClient.Protocol;

namespace OpenPost.Api.Mail;

public interface IMxResolver
{
    /// <summary>MX hosts in preference order. Falls back to the implicit MX (A/AAAA) per RFC 5321.</summary>
    Task<IReadOnlyList<string>> ResolveAsync(string domain, CancellationToken ct);
}

public sealed class MxResolver : IMxResolver
{
    private readonly LookupClient _dns;
    private readonly ILogger<MxResolver> _log;

    public MxResolver(ILogger<MxResolver> log)
    {
        _log = log;
        _dns = new LookupClient(new LookupClientOptions { Timeout = TimeSpan.FromSeconds(5), Retries = 2, UseCache = true });
    }

    public async Task<IReadOnlyList<string>> ResolveAsync(string domain, CancellationToken ct)
    {
        try
        {
            var mx = await _dns.QueryAsync(domain, QueryType.MX, cancellationToken: ct);
            var hosts = mx.Answers.MxRecords()
                .OrderBy(r => r.Preference)
                .Select(r => r.Exchange.Value.TrimEnd('.'))
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .Distinct()
                .ToList();

            if (hosts.Count > 0) return hosts;

            var a = await _dns.QueryAsync(domain, QueryType.A, cancellationToken: ct);
            if (a.Answers.ARecords().Any()) return new[] { domain };

            var aaaa = await _dns.QueryAsync(domain, QueryType.AAAA, cancellationToken: ct);
            if (aaaa.Answers.AaaaRecords().Any()) return new[] { domain };

            return Array.Empty<string>();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "MX lookup failed for {Domain}", domain);
            return Array.Empty<string>();
        }
    }
}
