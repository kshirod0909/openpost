using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;
using OpenPost.Api.Data;
using OpenPost.Api.Data.Entities;
using OpenPost.Api.Mail.Authentication;
using OpenPost.Api.Security;

namespace OpenPost.Api.Mail.Inbound;

/// <summary>
/// Hosts the inbound SMTP (25) and submission (587) listeners. One hosted service owns both
/// ports so a single deployment is a complete MTA for its domains.
/// </summary>
public sealed class SmtpServerHostedService : BackgroundService
{
    private readonly MailServerOptions _options;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SmtpServerHostedService> _log;
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset WindowStart)> _rate = new();
    private X509Certificate2? _certificate;
    private int _activeConnections;

    public SmtpServerHostedService(IOptions<MailServerOptions> options, IServiceScopeFactory scopes, ILogger<SmtpServerHostedService> log)
    {
        _options = options.Value;
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _log.LogInformation("Inbound SMTP listener disabled by configuration.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(_options.TlsCertificatePath) && File.Exists(_options.TlsCertificatePath))
        {
            try
            {
                _certificate = X509CertificateLoader.LoadPkcs12FromFile(
                    _options.TlsCertificatePath,
                    _options.TlsCertificatePassword);
                _log.LogInformation("Loaded TLS certificate for SMTP STARTTLS ({Subject})", _certificate.Subject);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Could not load SMTP TLS certificate; STARTTLS disabled");
            }
        }
        else
        {
            _log.LogWarning("No SMTP TLS certificate configured; STARTTLS is unavailable (AUTH is refused without TLS).");
        }

        var ports = new List<int> { _options.SmtpPort };
        if (_options.SubmissionPort != _options.SmtpPort) ports.Add(_options.SubmissionPort);

        var tasks = ports.Select(port => ListenAsync(port, stoppingToken)).ToArray();
        await Task.WhenAll(tasks);
    }

    private async Task ListenAsync(int port, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        try
        {
            listener.Start(512);
            _log.LogInformation("SMTP listening on port {Port} as {HostName}", port, _options.HostName);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not bind SMTP port {Port}", port);
            return;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (Interlocked.Increment(ref _activeConnections) > 200)
                {
                    Interlocked.Decrement(ref _activeConnections);
                    client.Dispose();
                    _log.LogWarning("Connection limit reached; dropping connection from {Remote}", client.Client.RemoteEndPoint);
                    continue;
                }

                _ = HandleClientAsync(client, port == _options.SubmissionPort, ct);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, bool submissionPort, CancellationToken ct)
    {
        try
        {
            var session = new SmtpSession(_scopes, _options, _certificate, _rate, _log);
            await session.RunAsync(client, submissionPort, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SMTP session aborted");
            client.Dispose();
        }
        finally
        {
            Interlocked.Decrement(ref _activeConnections);
        }
    }
}

/// <summary>A single SMTP conversation (RFC 5321 + RFC 6409 submission).</summary>
internal sealed class SmtpSession
{
    private readonly IServiceScopeFactory _scopes;
    private readonly MailServerOptions _options;
    private readonly X509Certificate2? _certificate;
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset WindowStart)> _rate;
    private readonly ILogger _log;

    private Stream _stream = Stream.Null;
    private LineReader _reader = new(Stream.Null);
    private string _remoteIp = "unknown";
    private string _helo = "unknown";
    private bool _secure;
    private bool _authenticated;
    private string? _mailFrom;
    private readonly List<string> _recipients = new();

    public SmtpSession(
        IServiceScopeFactory scopes,
        MailServerOptions options,
        X509Certificate2? certificate,
        ConcurrentDictionary<string, (int Count, DateTimeOffset WindowStart)> rate,
        ILogger log)
    {
        _scopes = scopes;
        _options = options;
        _certificate = certificate;
        _rate = rate;
        _log = log;
    }

    public async Task RunAsync(TcpClient client, bool submissionPort, CancellationToken ct)
    {
        using var _ = client;
        client.ReceiveTimeout = 300_000;
        client.SendTimeout = 60_000;

        _remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
        await using var network = client.GetStream();
        _stream = network;
        _reader = new LineReader(network);

        await ReplyAsync($"220 {_options.HostName} ESMTP OpenPost ready");

        while (!ct.IsCancellationRequested)
        {
            var line = await _reader.ReadLineAsync(8192, ct);
            if (line is null) return;

            if (line.Length == 0) continue;
            var space = line.IndexOf(' ');
            var verb = (space < 0 ? line : line[..space]).ToUpperInvariant();
            var argument = space < 0 ? string.Empty : line[(space + 1)..].Trim();

            switch (verb)
            {
                case "EHLO":
                case "HELO":
                    _helo = argument.Length > 0 ? argument : "unknown";
                    _mailFrom = null;
                    _recipients.Clear();
                    if (verb == "EHLO") await EhloAsync(submissionPort);
                    else await ReplyAsync($"250 {_options.HostName}");
                    break;

                case "STARTTLS":
                    if (_certificate is null) { await ReplyAsync("454 4.7.0 TLS not available"); break; }
                    if (_secure) { await ReplyAsync("503 5.5.1 TLS already active"); break; }
                    if (_mailFrom is not null) { await ReplyAsync("503 5.5.1 STARTTLS not allowed during a transaction"); break; }
                    await ReplyAsync("220 2.0.0 Ready to start TLS");
                    await UpgradeToTlsAsync(ct);
                    break;

                case "AUTH":
                    await AuthAsync(argument, submissionPort);
                    break;

                case "MAIL":
                    await MailFromAsync(argument);
                    break;

                case "RCPT":
                    await RcptToAsync(argument, submissionPort);
                    break;

                case "DATA":
                    await DataAsync(ct);
                    break;

                case "RSET":
                    _mailFrom = null;
                    _recipients.Clear();
                    await ReplyAsync("250 2.0.0 Reset");
                    break;

                case "NOOP":
                    await ReplyAsync("250 2.0.0 OK");
                    break;

                case "VRFY":
                    await ReplyAsync("252 2.5.2 Cannot verify user");
                    break;

                case "QUIT":
                    await ReplyAsync($"221 2.0.0 {_options.HostName} closing connection");
                    return;

                default:
                    await ReplyAsync("500 5.5.1 Command unrecognised");
                    break;
            }
        }
    }

    private async Task EhloAsync(bool submissionPort)
    {
        var lines = new List<string> { $"250-{_options.HostName} greets {_helo}" };
        lines.Add($"250-SIZE {_options.MaxMessageBytes}");
        lines.Add("250-8BITMIME");
        lines.Add("250-PIPELINING");
        lines.Add("250-ENHANCEDSTATUSCODES");
        if (_certificate is not null && !_secure) lines.Add("250-STARTTLS");
        if (_secure || submissionPort) lines.Add("250-AUTH PLAIN LOGIN");
        lines.Add("250 SMTPUTF8");
        await ReplyAsync(string.Join("\r\n", lines));
    }

    private async Task UpgradeToTlsAsync(CancellationToken ct)
    {
        var ssl = new SslStream(_stream, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = _certificate,
            ClientCertificateRequired = false,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        }, ct);

        _stream = ssl;
        _reader = new LineReader(ssl);
        _secure = true;
        _helo = "unknown";
    }

    private async Task AuthAsync(string argument, bool submissionPort)
    {
        if (_authenticated) { await ReplyAsync("503 5.5.1 Already authenticated"); return; }
        if (!_secure && !submissionPort)
        {
            await ReplyAsync("538 5.7.11 Encryption required for requested authentication mechanism");
            return;
        }

        var parts = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) { await ReplyAsync("501 5.5.4 AUTH mechanism required"); return; }

        string? username = null;
        string? password = null;

        switch (parts[0].ToUpperInvariant())
        {
            case "PLAIN":
            {
                var payload = parts.Length > 1 ? parts[1] : await ChallengeAsync("334 ");
                if (payload is null) return;
                var decoded = TryBase64(payload);
                if (decoded is null) { await ReplyAsync("501 5.5.2 Invalid base64"); return; }
                var fields = decoded.Split('\0');
                if (fields.Length < 3) { await ReplyAsync("501 5.5.2 Invalid AUTH PLAIN payload"); return; }
                username = fields[1];
                password = fields[2];
                break;
            }
            case "LOGIN":
            {
                var userPayload = parts.Length > 1 ? parts[1] : await ChallengeAsync("334 VXNlcm5hbWU6");
                if (userPayload is null) return;
                var passPayload = await ChallengeAsync("334 UGFzc3dvcmQ6");
                if (passPayload is null) return;
                username = TryBase64(userPayload);
                password = TryBase64(passPayload);
                break;
            }
            default:
                await ReplyAsync("504 5.5.4 Unrecognised authentication mechanism");
                return;
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            await ReplyAsync("501 5.5.2 Missing credentials");
            return;
        }

        var (ok, _) = await ValidateCredentialsAsync(username, password);
        if (!ok)
        {
            _log.LogWarning("SMTP auth failed for {User} from {Ip}", username, _remoteIp);
            await ReplyAsync("535 5.7.8 Authentication credentials invalid");
            return;
        }

        _authenticated = true;
        await ReplyAsync("235 2.7.0 Authentication successful");
    }

    private async Task<string?> ChallengeAsync(string prompt)
    {
        await ReplyAsync(prompt);
        var response = await _reader.ReadLineAsync(4096, CancellationToken.None);
        if (response is null) return null;
        if (response == "*") { await ReplyAsync("501 5.7.0 Authentication cancelled"); return null; }
        return response.Trim();
    }

    private async Task<(bool Ok, string? Address)> ValidateCredentialsAsync(string username, string password)
    {
        using var scope = _scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var db = scope.ServiceProvider.GetRequiredService<OpenPostDbContext>();

        var normalised = username.Trim().ToLowerInvariant();
        var user = await users.FindByEmailAsync(normalised) ?? await users.FindByNameAsync(normalised);
        if (user is null || !user.IsActive) return (false, null);
        if (!await users.CheckPasswordAsync(user, password)) return (false, null);

        var mailbox = await db.Mailboxes.FirstOrDefaultAsync(m => m.Address == normalised && m.IsActive);
        return (true, mailbox?.Address ?? user.Email ?? normalised);
    }

    private async Task MailFromAsync(string argument)
    {
        if (_mailFrom is not null) { await ReplyAsync("503 5.5.1 Nested MAIL command"); return; }
        if (_helo == "unknown") { await ReplyAsync("503 5.5.1 Send HELO/EHLO first"); return; }

        var address = ExtractAddress(argument, "FROM:");
        if (address is null) { await ReplyAsync("501 5.5.4 Syntax: MAIL FROM:<address>"); return; }

        if (!_authenticated && string.IsNullOrEmpty(address))
        {
            await ReplyAsync("550 5.7.1 Null sender not accepted here");
            return;
        }

        _mailFrom = address;
        _recipients.Clear();
        await ReplyAsync("250 2.1.0 Sender OK");
    }

    private async Task RcptToAsync(string argument, bool submissionPort)
    {
        if (_mailFrom is null) { await ReplyAsync("503 5.5.1 Need MAIL command first"); return; }
        if (_recipients.Count >= _options.MaxRecipientsPerMessage)
        {
            await ReplyAsync("452 4.5.3 Too many recipients");
            return;
        }

        var address = ExtractAddress(argument, "TO:");
        if (address is null) { await ReplyAsync("501 5.5.4 Syntax: RCPT TO:<address>"); return; }

        using var scope = _scopes.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<RecipientResolver>();
        var isLocal = await resolver.CanAcceptAsync(address, CancellationToken.None);

        if (!isLocal && !_authenticated)
        {
            await ReplyAsync("550 5.7.1 Relay access denied");
            return;
        }

        _recipients.Add(address);
        await ReplyAsync("250 2.1.5 Recipient OK");
    }

    private async Task DataAsync(CancellationToken ct)
    {
        if (_mailFrom is null) { await ReplyAsync("503 5.5.1 Need MAIL command first"); return; }
        if (_recipients.Count == 0) { await ReplyAsync("503 5.5.1 Need RCPT command first"); return; }

        await ReplyAsync("354 End data with <CR><LF>.<CR><LF>");

        using var buffer = new MemoryStream();
        while (true)
        {
            var line = await _reader.ReadLineAsync((int)_options.MaxMessageBytes, ct);
            if (line is null) return;
            if (line == ".") break;

            // RFC 5321 transparency: a leading dot is doubled by the client.
            if (line.StartsWith('.')) line = line[1..];

            var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
            if (buffer.Length + bytes.Length > _options.MaxMessageBytes)
            {
                await ReplyAsync("552 5.3.4 Message size exceeds fixed maximum message size");
                _mailFrom = null;
                _recipients.Clear();
                return;
            }
            buffer.Write(bytes);
        }

        if (!TryConsumeRateLimit())
        {
            await ReplyAsync("421 4.7.0 Too many messages from this client; try again later");
            _mailFrom = null;
            _recipients.Clear();
            return;
        }

        buffer.Position = 0;
        MimeMessage message;
        try
        {
            message = await MimeMessage.LoadAsync(buffer, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Unparseable MIME from {Ip}", _remoteIp);
            await ReplyAsync("554 5.6.0 Message could not be parsed");
            _mailFrom = null;
            _recipients.Clear();
            return;
        }

        message.Headers.Insert(0, "Received",
            $"from {_helo} ({_remoteIp}) by {_options.HostName} with {(_secure ? "ESMTPS" : "ESMTP")} id {Guid.NewGuid():N}; {DateTimeOffset.UtcNow:R}");

        var recipients = _recipients.ToList();
        var mailFrom = _mailFrom!;
        _mailFrom = null;
        _recipients.Clear();

        try
        {
            if (_authenticated)
            {
                var result = await AcceptSubmissionAsync(message, mailFrom, recipients, ct);
                if (result is null)
                {
                    await ReplyAsync("553 5.7.1 Sender address does not belong to the authenticated account");
                    return;
                }
                await ReplyAsync($"250 2.0.0 Ok: queued as {result}");
            }
            else
            {
                await AcceptInboundAsync(message, mailFrom, recipients, ct);
                await ReplyAsync("250 2.0.0 Ok: delivered");
            }
        }
        catch (VirusRejectedException ex)
        {
            await ReplyAsync($"550 5.7.1 Message rejected: malware detected ({ex.Signature})");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Message processing failed from {Ip} for {Recipients}", _remoteIp, string.Join(",", recipients));
            await ReplyAsync("451 4.3.0 Temporary processing failure");
        }
    }

    private async Task<Guid?> AcceptSubmissionAsync(MimeMessage message, string mailFrom, List<string> recipients, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenPostDbContext>();
        var submission = scope.ServiceProvider.GetRequiredService<IMailSubmissionService>();

        var sender = await db.Mailboxes.FirstOrDefaultAsync(m => m.Address == mailFrom && m.IsActive, ct);
        if (sender is null) return null;

        var response = await submission.SendMimeAsync(sender, message, recipients, ct);
        return response.MessageId;
    }

    private async Task AcceptInboundAsync(MimeMessage message, string mailFrom, List<string> recipients, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var evaluator = scope.ServiceProvider.GetRequiredService<AuthenticationEvaluator>();
        var spamFilter = scope.ServiceProvider.GetRequiredService<ISpamFilter>();
        var antivirus = scope.ServiceProvider.GetRequiredService<IAntivirusScanner>();
        var router = scope.ServiceProvider.GetRequiredService<MailRouter>();

        var scan = await antivirus.ScanAsync(message, ct);
        if (!scan.Clean)
        {
            _log.LogWarning("Rejected virus {Signature} from {Ip}", scan.Signature, _remoteIp);
            throw new VirusRejectedException(scan.Signature ?? "unknown");
        }

        var auth = await evaluator.EvaluateAsync(message, _remoteIp, ct);
        var spam = await spamFilter.CheckAsync(message, new SpamContext(auth, _helo, mailFrom, _remoteIp), ct);

        if (spam.Verdict == SpamVerdict.Spam)
            _log.LogInformation("Inbound scored {Score} ({Rules}) from {Ip}", spam.Score, string.Join(",", spam.Rules.Take(5)), _remoteIp);

        await router.RouteInboundAsync(message, mailFrom, recipients, spam, auth, ct);
    }

    private bool TryConsumeRateLimit()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = _rate.AddOrUpdate(_remoteIp,
            _ => (1, now),
            (_, existing) => now - existing.WindowStart > TimeSpan.FromMinutes(1)
                ? (1, now)
                : (existing.Count + 1, existing.WindowStart));

        return entry.Count <= _options.MaxMessagesPerMinutePerIp;
    }

    private static string? ExtractAddress(string argument, string prefix)
    {
        if (!argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = argument[prefix.Length..].Trim();
        var start = rest.IndexOf('<');
        var end = rest.IndexOf('>');
        if (start >= 0 && end > start) return rest[(start + 1)..end].Trim().ToLowerInvariant();
        var token = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return token?.Trim().ToLowerInvariant();
    }

    private static string? TryBase64(string value)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
        catch (FormatException) { return null; }
    }

    private async Task ReplyAsync(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\r\n");
        await _stream.WriteAsync(bytes);
        await _stream.FlushAsync();
    }
}

internal sealed class VirusRejectedException(string signature) : Exception($"Malware detected: {signature}")
{
    public string Signature { get; } = signature;
}

/// <summary>Minimal buffered CRLF line reader that works across a STARTTLS stream swap.</summary>
internal sealed class LineReader
{
    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[16 * 1024];
    private int _position;
    private int _length;

    public LineReader(Stream stream) => _stream = stream;

    public async Task<string?> ReadLineAsync(int maxBytes, CancellationToken ct)
    {
        var line = new MemoryStream(256);
        while (true)
        {
            if (_position >= _length)
            {
                _length = await _stream.ReadAsync(_buffer, ct);
                _position = 0;
                if (_length == 0) return line.Length == 0 ? null : Encoding.UTF8.GetString(line.ToArray());
            }

            var b = _buffer[_position++];
            if (b == (byte)'\n')
            {
                var text = Encoding.UTF8.GetString(line.ToArray());
                return text.EndsWith('\r') ? text[..^1] : text;
            }

            line.WriteByte(b);
            if (line.Length > maxBytes) throw new InvalidOperationException("SMTP line exceeds the configured maximum length.");
        }
    }
}
