using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using OpenPost.Api.Common;
using OpenPost.Api.Contracts;
using OpenPost.Api.Data;
using OpenPost.Api.Data.Entities;
using OpenPost.Api.Security;
using OpenPost.Api.Services;

namespace OpenPost.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly UserManager<AppUser> _users;
    private readonly OpenPostDbContext _db;
    private readonly ITokenService _tokens;
    private readonly ICurrentUser _current;
    private readonly AuditWriter _audit;
    private readonly ILogger<AuthController> _log;

    public AuthController(
        UserManager<AppUser> users,
        OpenPostDbContext db,
        ITokenService tokens,
        ICurrentUser current,
        AuditWriter audit,
        ILogger<AuthController> log)
    {
        _users = users;
        _db = db;
        _tokens = tokens;
        _current = current;
        _audit = audit;
        _log = log;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<TokenResponse>> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var user = await _users.FindByEmailAsync(request.Email?.Trim() ?? string.Empty);
        if (user is null || !user.IsActive)
        {
            _log.LogWarning("Login attempt for unknown or inactive account {Email} from {Ip}", request.Email, _current.IpAddress);
            return Unauthorized(new ProblemDetails { Title = "Invalid credentials", Detail = "Email or password is incorrect." });
        }

        if (!await _users.CheckPasswordAsync(user, request.Password))
        {
            _log.LogWarning("Failed password for {Email} from {Ip}", request.Email, _current.IpAddress);
            return Unauthorized(new ProblemDetails { Title = "Invalid credentials", Detail = "Email or password is incorrect." });
        }

        if (user.TwoFactorEnabled)
        {
            if (string.IsNullOrWhiteSpace(request.TotpCode))
                return Unauthorized(new ProblemDetails { Title = "Two-factor code required", Detail = "totpRequired" });

            var valid = await _users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, request.TotpCode.Replace(" ", string.Empty));
            if (!valid)
                return Unauthorized(new ProblemDetails { Title = "Invalid two-factor code", Detail = "totpRequired" });
        }

        user.LastLoginAt = DateTimeOffset.UtcNow;
        await _users.UpdateAsync(user);

        var token = await _tokens.IssueAsync(user, Request.Headers.UserAgent.ToString(), _current.IpAddress, ct);
        await _audit.WriteAsync("auth.login", "user", user.Id.ToString(), new { request.RememberMe }, ct);
        return Ok(token);
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<TokenResponse>> Refresh([FromBody] RefreshRequest request, CancellationToken ct)
    {
        var token = await _tokens.RefreshAsync(request.RefreshToken, Request.Headers.UserAgent.ToString(), _current.IpAddress, ct);
        if (token is null)
            return Unauthorized(new ProblemDetails { Title = "Invalid refresh token", Detail = "Sign in again." });
        return Ok(token);
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await _tokens.RevokeAllAsync(_current.RequireUserId(), ct);
        await _audit.WriteAsync("auth.logout", "user", _current.Id?.ToString(), ct: ct);
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<MeResponse>> Me(CancellationToken ct)
    {
        var user = await _users.FindByIdAsync(_current.RequireUserId().ToString());
        if (user is null) return Unauthorized();

        var roles = await _users.GetRolesAsync(user);
        var mailboxes = await _dbMailboxes(ct, user.Id);

        return Ok(new MeResponse(user.Id, user.Email ?? string.Empty, user.DisplayName, roles,
            user.TwoFactorEnabled, user.Signature, mailboxes));
    }

    [HttpPatch("me")]
    [Authorize]
    public async Task<ActionResult<MeResponse>> UpdateProfile([FromBody] UpdateProfileRequest request, CancellationToken ct)
    {
        var user = await _users.FindByIdAsync(_current.RequireUserId().ToString());
        if (user is null) return Unauthorized();

        if (request.DisplayName is not null) user.DisplayName = request.DisplayName.Trim();
        if (request.Signature is not null) user.Signature = request.Signature;
        await _users.UpdateAsync(user);

        return await Me(ct);
    }

    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
    {
        var user = await _users.FindByIdAsync(_current.RequireUserId().ToString());
        if (user is null) return Unauthorized();

        var result = await _users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
            return BadRequest(new ProblemDetails
            {
                Title = "Password not changed",
                Detail = string.Join(" ", result.Errors.Select(e => e.Description))
            });

        await _tokens.RevokeAllAsync(user.Id, ct);
        await _audit.WriteAsync("auth.password_changed", "user", user.Id.ToString(), ct: ct);
        return NoContent();
    }

    [HttpPost("2fa/setup")]
    [Authorize]
    public async Task<ActionResult<TwoFactorSetupResponse>> SetupTwoFactor()
    {
        var user = await _users.FindByIdAsync(_current.RequireUserId().ToString());
        if (user is null) return Unauthorized();

        var key = await _users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await _users.ResetAuthenticatorKeyAsync(user);
            key = await _users.GetAuthenticatorKeyAsync(user);
        }

        var issuer = Uri.EscapeDataString("OpenPost");
        var account = Uri.EscapeDataString(user.Email ?? user.UserName ?? "user");
        var uri = $"otpauth://totp/{issuer}:{account}?secret={key}&issuer={issuer}&algorithm=SHA1&digits=6&period=30";

        return Ok(new TwoFactorSetupResponse(key!, uri));
    }

    [HttpPost("2fa/enable")]
    [Authorize]
    public async Task<IActionResult> EnableTwoFactor([FromBody] TwoFactorVerifyRequest request)
    {
        var user = await _users.FindByIdAsync(_current.RequireUserId().ToString());
        if (user is null) return Unauthorized();

        var valid = await _users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, request.Code.Replace(" ", string.Empty));
        if (!valid)
            return BadRequest(new ProblemDetails { Title = "Invalid code", Detail = "The authenticator code did not match." });

        await _users.SetTwoFactorEnabledAsync(user, true);
        await _audit.WriteAsync("auth.2fa_enabled", "user", user.Id.ToString());
        return NoContent();
    }

    [HttpPost("2fa/disable")]
    [Authorize]
    public async Task<IActionResult> DisableTwoFactor([FromBody] TwoFactorVerifyRequest request)
    {
        var user = await _users.FindByIdAsync(_current.RequireUserId().ToString());
        if (user is null) return Unauthorized();

        var valid = await _users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, request.Code.Replace(" ", string.Empty));
        if (!valid) return BadRequest(new ProblemDetails { Title = "Invalid code" });

        await _users.SetTwoFactorEnabledAsync(user, false);
        await _users.ResetAuthenticatorKeyAsync(user);
        await _audit.WriteAsync("auth.2fa_disabled", "user", user.Id.ToString());
        return NoContent();
    }

    private async Task<List<MailboxDto>> _dbMailboxes(CancellationToken ct, Guid userId)
    {
        var rows = await _db.Mailboxes.Include(m => m.Domain)
            .Where(m => m.UserId == userId && m.IsActive)
            .OrderBy(m => m.Address)
            .ToListAsync(ct);

        return rows.Select(m => new MailboxDto(m.Id, m.Address, m.DisplayName, m.DomainId,
            m.Domain?.Name ?? string.Empty, m.QuotaBytes, m.UsedBytes, m.IsShared, m.IsSystem)).ToList();
    }
}
