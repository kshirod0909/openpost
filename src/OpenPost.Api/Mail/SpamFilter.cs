using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MimeKit;
using OpenPost.Api.Data.Entities;
using OpenPost.Api.Mail.Authentication;
using OpenPost.Api.Security;

namespace OpenPost.Api.Mail;

public sealed record SpamResult(double Score, SpamVerdict Verdict, IReadOnlyList<string> Rules);

public sealed record SpamContext(
    AuthenticationResult? Authentication,
    string? Helo,
    string? EnvelopeFrom,
    string? ClientIp);

public interface ISpamFilter
{
    Task<SpamResult> CheckAsync(MimeMessage message, SpamContext context, CancellationToken ct);
}

/// <summary>
/// Rspamd first (when configured), built-in heuristics as fallback. Filtering never fails a
/// delivery: an unreachable scanner degrades to the heuristic score rather than bouncing mail.
/// </summary>
public sealed class SpamFilterChain : ISpamFilter
{
    private readonly SpamOptions _options;
    private readonly IHttpClientFactory _http;
    private readonly HeuristicSpamFilter _heuristic;
    private readonly ILogger<SpamFilterChain> _log;

    public SpamFilterChain(IOptions<SpamOptions> options, IHttpClientFactory http, HeuristicSpamFilter heuristic, ILogger<SpamFilterChain> log)
    {
        _options = options.Value;
        _http = http;
        _heuristic = heuristic;
        _log = log;
    }

    public async Task<SpamResult> CheckAsync(MimeMessage message, SpamContext context, CancellationToken ct)
    {
        var heuristic = await _heuristic.CheckAsync(message, context, ct);
        if (!_options.Enabled) return new SpamResult(0, SpamVerdict.Clean, Array.Empty<string>());
        if (string.IsNullOrWhiteSpace(_options.RspamdUrl)) return heuristic;

        try
        {
            using var buffer = new MemoryStream();
            await message.WriteToAsync(buffer, ct);
            buffer.Position = 0;

            var client = _http.CreateClient("rspamd");
            using var content = new ByteArrayContent(buffer.ToArray());
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.RspamdUrl.TrimEnd('/') + "/checkv2") { Content = content };
            if (context.ClientIp is not null) request.Headers.TryAddWithoutValidation("IP", context.ClientIp);
            if (context.Helo is not null) request.Headers.TryAddWithoutValidation("Helo", context.Helo);
            if (context.EnvelopeFrom is not null) request.Headers.TryAddWithoutValidation("From", context.EnvelopeFrom);
            request.Headers.TryAddWithoutValidation("Rcpt", message.To.ToString());

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return heuristic;

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = json.RootElement;
            var score = root.TryGetProperty("score", out var s) && s.TryGetDouble(out var value) ? value : heuristic.Score;

            var rules = new List<string>();
            if (root.TryGetProperty("symbols", out var symbols) && symbols.ValueKind == JsonValueKind.Object)
                rules.AddRange(symbols.EnumerateObject().Select(p => p.Name).Take(25));

            return new SpamResult(score, Classify(score), rules);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Rspamd scan failed; using heuristic score");
            return heuristic;
        }
    }

    public SpamVerdict Classify(double score) =>
        score >= _options.SpamThreshold ? SpamVerdict.Spam
        : score >= _options.SuspectThreshold ? SpamVerdict.Suspect
        : SpamVerdict.Clean;
}

/// <summary>
/// Small, explainable scoring model for deployments that do not run Rspamd. It is intentionally
/// conservative: it flags obvious bulk/phishing patterns instead of trying to replace a real engine.
/// </summary>
public sealed class HeuristicSpamFilter : ISpamFilter
{
    private static readonly string[] UrgentWords =
    {
        "verify your account", "urgent action", "wire transfer", "gift card", "crypto wallet",
        "your account will be suspended", "click here to claim", "limited time offer",
        "you have won", "lottery", "inheritance", "seed phrase"
    };

    private static readonly string[] SuspiciousTlds = { ".top", ".xyz", ".click", ".zip", ".mov", ".rest", ".country", ".gq", ".tk" };

    private readonly IOptions<SpamOptions> _options;

    public HeuristicSpamFilter(IOptions<SpamOptions> options) => _options = options;

    public Task<SpamResult> CheckAsync(MimeMessage message, SpamContext context, CancellationToken ct)
    {
        double score = 0;
        var rules = new List<string>();

        void Add(double weight, string rule)
        {
            score += weight;
            rules.Add(rule);
        }

        if (context.Authentication is { } auth)
        {
            var weight = auth.SpamWeight();
            if (weight > 0) Add(weight, $"auth:{auth.SpfText}/dkim={(auth.DkimValid switch { true => "pass", false => "fail", null => "none" })}");
        }

        var from = message.From.OfType<MailboxAddress>().FirstOrDefault();
        var fromDomain = from?.Address is null ? string.Empty : Common.Addresses.DomainPart(from.Address);
        var replyTo = message.ReplyTo.OfType<MailboxAddress>().FirstOrDefault();
        var replyDomain = replyTo?.Address is null ? string.Empty : Common.Addresses.DomainPart(replyTo.Address);

        if (replyDomain.Length > 0 && fromDomain.Length > 0 && replyDomain != fromDomain)
            Add(1.5, "reply-to-mismatch");

        if (SuspiciousTlds.Any(t => fromDomain.EndsWith(t, StringComparison.OrdinalIgnoreCase)))
            Add(1.5, "suspicious-sender-tld");

        if (string.IsNullOrWhiteSpace(message.MessageId))
            Add(0.8, "missing-message-id");

        if (message.Date == DateTimeOffset.MinValue)
            Add(0.8, "missing-date");

        var text = ((message.TextBody ?? string.Empty) + " " + (message.HtmlBody ?? string.Empty)).ToLowerInvariant();
        if (UrgentWords.Any(w => text.Contains(w, StringComparison.Ordinal)))
            Add(1.2, "phishing-keywords");

        if (message.HtmlBody is not null && string.IsNullOrWhiteSpace(message.TextBody))
            Add(0.5, "html-only");

        var linkCount = CountLinks(message.HtmlBody);
        if (linkCount > 25) Add(1.0, "excessive-links");
        if (linkCount > 0 && linkCount < 3 && text.Contains("verify", StringComparison.Ordinal)) Add(1.0, "credential-harvest-pattern");

        if (message.Headers.Contains(HeaderId.ListUnsubscribe)) score -= 1.0;
        if (context.Authentication is { DkimValid: true, Spf: SpfOutcome.Pass }) score -= 1.0;

        score = Math.Max(0, Math.Round(score, 2));

        var verdict = score >= _options.Value.SpamThreshold ? SpamVerdict.Spam
            : score >= _options.Value.SuspectThreshold ? SpamVerdict.Suspect
            : SpamVerdict.Clean;

        return Task.FromResult(new SpamResult(score, verdict, rules));
    }

    private static int CountLinks(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return 0;
        var matches = System.Text.RegularExpressions.Regex.Matches(html, "href\\s*=\\s*[\"']https?://", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return matches.Count;
    }
}
