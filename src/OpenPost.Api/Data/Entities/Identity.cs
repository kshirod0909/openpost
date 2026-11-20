using Microsoft.AspNetCore.Identity;

namespace OpenPost.Api.Data.Entities;

/// <summary>Application user. One user may own several mailboxes across domains.</summary>
public sealed class AppUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Per-user default quota applied to mailboxes created for this user.</summary>
    public long DefaultQuotaBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Signature appended by the web client when composing.</summary>
    public string? Signature { get; set; }

    public ICollection<Mailbox> Mailboxes { get; set; } = new List<Mailbox>();
}

public sealed class AppRole : IdentityRole<Guid>
{
    public AppRole() { }
    public AppRole(string name) : base(name) { }
}

/// <summary>Long-lived refresh token, stored as a SHA-256 hash only.</summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
    public string? ReplacedByHash { get; set; }
    public string? UserAgent { get; set; }
    public string? IpAddress { get; set; }
    public AppUser? User { get; set; }
}
