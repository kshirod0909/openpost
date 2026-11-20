namespace OpenPost.Api.Security;

public static class Policies
{
    public const string SystemAdmin = "SystemAdmin";
    public const string DomainAdmin = "DomainAdmin";
    public const string User = "User";

    /// <summary>System administrators only.</summary>
    public const string RequireSystemAdmin = "RequireSystemAdmin";

    /// <summary>System administrators or domain administrators (domain-scoped checks happen in handlers).</summary>
    public const string RequireAdmin = "RequireAdmin";
}

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "openpost";
    public string Audience { get; set; } = "openpost-web";
    public string SigningKey { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 20;
    public int RefreshTokenDays { get; set; } = 14;
}

public sealed class MailServerOptions
{
    /// <summary>Set to false to run the API without an inbound SMTP listener (e.g. behind a relay).</summary>
    public bool Enabled { get; set; } = true;
    public int SmtpPort { get; set; } = 25;
    public int SubmissionPort { get; set; } = 587;
    public string HostName { get; set; } = "mail.example.org";
    public long MaxMessageBytes { get; set; } = 25 * 1024 * 1024;
    public int MaxRecipientsPerMessage { get; set; } = 100;
    public int MaxMessagesPerMinutePerIp { get; set; } = 60;

    /// <summary>Path to a PFX used for STARTTLS on the submission port. Empty disables STARTTLS.</summary>
    public string? TlsCertificatePath { get; set; }
    public string? TlsCertificatePassword { get; set; }

    /// <summary>Require authenticated submission for mail relayed to external domains.</summary>
    public bool RequireAuthForRelay { get; set; } = true;
}

public sealed class StorageOptions
{
    /// <summary>Root directory for message bodies and attachments.</summary>
    public string RootPath { get; set; } = "/var/lib/openpost";
    public long MaxAttachmentBytes { get; set; } = 25 * 1024 * 1024;
}

public sealed class SpamOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Rspamd HTTP endpoint, e.g. http://rspamd:11333. When empty the built-in heuristics run.</summary>
    public string? RspamdUrl { get; set; }
    public double SpamThreshold { get; set; } = 6.0;
    public double SuspectThreshold { get; set; } = 3.0;
    public bool DeliverSpamToJunk { get; set; } = true;
}

public sealed class AntivirusOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "clamav";
    public int Port { get; set; } = 3310;
}

public sealed class DeliveryOptions
{
    public int MaxAttempts { get; set; } = 8;
    public int SmtpTimeoutSeconds { get; set; } = 30;
    public int MaxParallelDeliveries { get; set; } = 4;
    public bool RequireTls { get; set; } = true;
    /// <summary>Send a DSN bounce into the sender's mailbox after the final failed attempt.</summary>
    public bool SendBounces { get; set; } = true;
}
