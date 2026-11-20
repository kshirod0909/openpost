using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenPost.Api.Common;
using OpenPost.Api.Contracts;
using OpenPost.Api.Data;
using OpenPost.Api.Data.Entities;
using System.Security.Cryptography;
using System.Text;

namespace OpenPost.Api.Security;

public interface ITokenService
{
    Task<TokenResponse> IssueAsync(AppUser user, string? userAgent, string? ip, CancellationToken ct);
    Task<TokenResponse?> RefreshAsync(string refreshToken, string? userAgent, string? ip, CancellationToken ct);
    Task RevokeAllAsync(Guid userId, CancellationToken ct);
}

public sealed class TokenService : ITokenService
{
    private readonly OpenPostDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly JwtOptions _options;
    private readonly TimeProvider _clock;

    public TokenService(OpenPostDbContext db, UserManager<AppUser> users, IOptions<JwtOptions> options, TimeProvider clock)
    {
        _db = db;
        _users = users;
        _options = options.Value;
        _clock = clock;
    }

    public async Task<TokenResponse> IssueAsync(AppUser user, string? userAgent, string? ip, CancellationToken ct)
    {
        var roles = await _users.GetRolesAsync(user);
        var now = _clock.GetUtcNow();
        var expires = now.AddMinutes(_options.AccessTokenMinutes);

        var accessToken = CreateAccessToken(user, roles, now, expires);
        var refresh = await CreateRefreshTokenAsync(user.Id, userAgent, ip, ct);

        var mailboxes = await _db.Mailboxes
            .Include(m => m.Domain)
            .Where(m => m.UserId == user.Id && m.IsActive)
            .OrderBy(m => m.Address)
            .ToListAsync(ct);

        var me = new MeResponse(
            user.Id,
            user.Email ?? user.UserName ?? string.Empty,
            user.DisplayName,
            roles,
            user.TwoFactorEnabled,
            user.Signature,
            mailboxes.Select(m => new MailboxDto(
                m.Id, m.Address, m.DisplayName, m.DomainId, m.Domain?.Name ?? string.Empty,
                m.QuotaBytes, m.UsedBytes, m.IsShared, m.IsSystem)).ToList());

        return new TokenResponse(accessToken, refresh, expires, me);
    }

    public async Task<TokenResponse?> RefreshAsync(string refreshToken, string? userAgent, string? ip, CancellationToken ct)
    {
        var hash = Hash(refreshToken);
        var stored = await _db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (stored is null) return null;
        if (stored.RevokedAt is not null || stored.ExpiresAt <= _clock.GetUtcNow()) return null;
        if (stored.User is null || !stored.User.IsActive) return null;

        // Rotate: revoke the presented token and issue a new pair.
        stored.RevokedAt = _clock.GetUtcNow();
        var replacement = await CreateRefreshTokenAsync(stored.UserId, userAgent, ip, ct);
        stored.ReplacedByHash = Hash(replacement);

        var response = await IssueAsyncInternal(stored.User, replacement, userAgent, ip, ct);
        await _db.SaveChangesAsync(ct);
        return response;
    }

    public async Task RevokeAllAsync(Guid userId, CancellationToken ct)
    {
        var tokens = await _db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var token in tokens) token.RevokedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
    }

    private async Task<TokenResponse> IssueAsyncInternal(AppUser user, string refreshToken, string? userAgent, string? ip, CancellationToken ct)
    {
        var roles = await _users.GetRolesAsync(user);
        var now = _clock.GetUtcNow();
        var expires = now.AddMinutes(_options.AccessTokenMinutes);
        var accessToken = CreateAccessToken(user, roles, now, expires);

        var mailboxes = await _db.Mailboxes.Include(m => m.Domain)
            .Where(m => m.UserId == user.Id && m.IsActive)
            .ToListAsync(ct);

        var me = new MeResponse(
            user.Id, user.Email ?? string.Empty, user.DisplayName, roles, user.TwoFactorEnabled,
            user.Signature,
            mailboxes.Select(m => new MailboxDto(
                m.Id, m.Address, m.DisplayName, m.DomainId, m.Domain?.Name ?? string.Empty,
                m.QuotaBytes, m.UsedBytes, m.IsShared, m.IsSystem)).ToList());

        return new TokenResponse(accessToken, refreshToken, expires, me);
    }

    private string CreateAccessToken(AppUser user, IList<string> roles, DateTimeOffset now, DateTimeOffset expires)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new(ClaimTypes.Email, user.Email ?? string.Empty),
            new("name", user.DisplayName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private async Task<string> CreateRefreshTokenAsync(Guid userId, string? userAgent, string? ip, CancellationToken ct)
    {
        var raw = Base64Url(RandomNumberGenerator.GetBytes(48));
        _db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = Hash(raw),
            ExpiresAt = _clock.GetUtcNow().AddDays(_options.RefreshTokenDays),
            UserAgent = userAgent?.Length > 300 ? userAgent[..300] : userAgent,
            IpAddress = ip
        });
        await _db.SaveChangesAsync(ct);
        return raw;
    }

    public static string Hash(string value) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
