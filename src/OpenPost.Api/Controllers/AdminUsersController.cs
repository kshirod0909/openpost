using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenPost.Api.Common;
using OpenPost.Api.Contracts;
using OpenPost.Api.Data;
using OpenPost.Api.Data.Entities;
using OpenPost.Api.Mail;
using OpenPost.Api.Security;
using OpenPost.Api.Services;

namespace OpenPost.Api.Controllers;

[ApiController]
[Authorize(Policy = Policies.RequireAdmin)]
[Route("api/v1/admin/users")]
public sealed class AdminUsersController : ControllerBase
{
    private readonly OpenPostDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly ICurrentUser _current;
    private readonly AuditWriter _audit;

    public AdminUsersController(OpenPostDbContext db, UserManager<AppUser> users, ICurrentUser current, AuditWriter audit)
    {
        _db = db;
        _users = users;
        _current = current;
        _audit = audit;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UserDto>>> List([FromQuery] string? q, CancellationToken ct)
    {
        var query = _db.Users.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = "%" + q.Trim() + "%";
            query = query.Where(u => EF.Functions.ILike(u.Email!, term) || EF.Functions.ILike(u.DisplayName, term));
        }

        var users = await query.OrderBy(u => u.Email).Take(500).ToListAsync(ct);
        var mailboxes = await _db.Mailboxes.Where(m => m.UserId != null)
            .Select(m => new { m.UserId, m.Address }).ToListAsync(ct);

        var result = new List<UserDto>();
        foreach (var user in users)
        {
            var roles = await _users.GetRolesAsync(user);
            result.Add(new UserDto(user.Id, user.Email ?? string.Empty, user.DisplayName, user.IsActive,
                user.TwoFactorEnabled, user.CreatedAt, user.LastLoginAt, roles,
                mailboxes.Where(m => m.UserId == user.Id).Select(m => m.Address).ToList()));
        }

        return Ok(result);
    }

    [HttpPost]
    [Authorize(Policy = Policies.RequireSystemAdmin)]
    public async Task<ActionResult<object>> Create([FromBody] CreateUserRequest request, CancellationToken ct)
    {
        var email = (request.Email ?? string.Empty).Trim().ToLowerInvariant();
        if (!Addresses.LooksValid(email)) throw new ValidationFailedException("A valid email address is required.");

        if (await _users.FindByEmailAsync(email) is not null)
            throw new ConflictException("A user with that email already exists.");

        var password = string.IsNullOrWhiteSpace(request.Password) ? PasswordPolicy.GenerateStrongPassword() : request.Password;
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? email : request.DisplayName.Trim(),
            IsActive = true
        };

        var created = await _users.CreateAsync(user, password);
        if (!created.Succeeded)
            throw new ValidationFailedException(string.Join(" ", created.Errors.Select(e => e.Description)));

        var roles = request.Roles is { Length: > 0 } ? request.Roles : new[] { Policies.User };
        foreach (var role in roles)
            if (await _users.IsInRoleAsync(user, role) == false)
                await _users.AddToRoleAsync(user, role);

        await _audit.WriteAsync("user.create", "user", user.Id.ToString(), new { email, roles }, ct);

        return Ok(new { user = new UserDto(user.Id, email, user.DisplayName, user.IsActive, false,
            user.CreatedAt, null, roles, Array.Empty<string>()), generatedPassword = string.IsNullOrWhiteSpace(request.Password) ? password : null });
    }

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<UserDto>> Update(Guid id, [FromBody] UpdateUserRequest request, CancellationToken ct)
    {
        var user = await _users.FindByIdAsync(id.ToString()) ?? throw new NotFoundException("User not found.");
        EnsureCanManage(user);

        if (request.DisplayName is not null) user.DisplayName = request.DisplayName.Trim();
        if (request.IsActive is { } active)
        {
            if (!active && user.Id == _current.Id) throw new ValidationFailedException("You cannot deactivate your own account.");
            user.IsActive = active;
        }

        await _users.UpdateAsync(user);

        if (request.Roles is { Length: > 0 })
        {
            if (!_current.IsSystemAdmin) throw new ForbiddenException("Only system administrators can change roles.");
            var currentRoles = await _users.GetRolesAsync(user);
            await _users.RemoveFromRolesAsync(user, currentRoles.Except(request.Roles));
            foreach (var role in request.Roles.Except(currentRoles))
                await _users.AddToRoleAsync(user, role);
        }

        await _audit.WriteAsync("user.update", "user", id.ToString(), ct: ct);

        var roles = await _users.GetRolesAsync(user);
        return Ok(new UserDto(user.Id, user.Email ?? string.Empty, user.DisplayName, user.IsActive,
            user.TwoFactorEnabled, user.CreatedAt, user.LastLoginAt, roles, Array.Empty<string>()));
    }

    [HttpPost("{id:guid}/reset-password")]
    public async Task<ActionResult<ResetPasswordResponse>> ResetPassword(Guid id, CancellationToken ct)
    {
        var user = await _users.FindByIdAsync(id.ToString()) ?? throw new NotFoundException("User not found.");
        EnsureCanManage(user);

        var password = PasswordPolicy.GenerateStrongPassword();
        var token = await _users.GeneratePasswordResetTokenAsync(user);
        var result = await _users.ResetPasswordAsync(user, token, password);
        if (!result.Succeeded)
            throw new ValidationFailedException(string.Join(" ", result.Errors.Select(e => e.Description)));

        await _audit.WriteAsync("user.password_reset", "user", id.ToString(), ct: ct);
        return Ok(new ResetPasswordResponse(password));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.RequireSystemAdmin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (id == _current.Id) throw new ValidationFailedException("You cannot delete your own account.");
        var user = await _users.FindByIdAsync(id.ToString()) ?? throw new NotFoundException("User not found.");

        var mailboxes = await _db.Mailboxes.Where(m => m.UserId == id).ToListAsync(ct);
        foreach (var mailbox in mailboxes) mailbox.UserId = null;

        await _users.DeleteAsync(user);
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("user.delete", "user", id.ToString(), new { user.Email }, ct);
        return NoContent();
    }

    private void EnsureCanManage(AppUser user)
    {
        if (_current.IsSystemAdmin) return;
        if (user.Id != _current.Id) throw new ForbiddenException("Domain administrators can only manage their own account.");
    }
}

[ApiController]
[Authorize(Policy = Policies.RequireAdmin)]
[Route("api/v1/admin/mailboxes")]
public sealed class AdminMailboxesController : ControllerBase
{
    private readonly OpenPostDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly IMailboxAccess _access;
    private readonly ICurrentUser _current;
    private readonly AuditWriter _audit;

    public AdminMailboxesController(OpenPostDbContext db, UserManager<AppUser> users, IMailboxAccess access, ICurrentUser current, AuditWriter audit)
    {
        _db = db;
        _users = users;
        _access = access;
        _current = current;
        _audit = audit;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AdminMailboxDto>>> List([FromQuery] Guid? domainId, [FromQuery] string? q, CancellationToken ct)
    {
        var query = _db.Mailboxes.Include(m => m.Domain).Include(m => m.User).AsQueryable();

        if (domainId is { } id)
        {
            if (!await _access.CanAdminDomainAsync(id, ct)) throw new ForbiddenException("You do not administer this domain.");
            query = query.Where(m => m.DomainId == id);
        }
        else if (!_current.IsSystemAdmin)
        {
            var userId = _current.RequireUserId();
            var owned = await _db.Domains.Where(d => d.OwnerUserId == userId).Select(d => d.Id).ToListAsync(ct);
            query = query.Where(m => owned.Contains(m.DomainId));
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = "%" + q.Trim() + "%";
            query = query.Where(m => EF.Functions.ILike(m.Address, term) || EF.Functions.ILike(m.DisplayName, term));
        }

        var mailboxes = await query.OrderBy(m => m.Address).Take(1000).ToListAsync(ct);
        return Ok(mailboxes.Select(ToDto).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<object>> Create([FromBody] CreateMailboxRequest request, CancellationToken ct)
    {
        var domain = await _db.Domains.FirstOrDefaultAsync(d => d.Id == request.DomainId, ct)
            ?? throw new NotFoundException("Domain not found.");

        if (!await _access.CanAdminDomainAsync(domain.Id, ct))
            throw new ForbiddenException("You do not administer this domain.");

        if (!Addresses.IsValidLocalPart(request.LocalPart))
            throw new ValidationFailedException("Invalid mailbox name. Use letters, digits, dots, dashes or underscores.");

        var address = $"{request.LocalPart.ToLowerInvariant()}@{domain.Name}";
        if (await _db.Mailboxes.AnyAsync(m => m.Address == address, ct) || await _db.Aliases.AnyAsync(a => a.Address == address, ct))
            throw new ConflictException("That address already exists.");

        var mailboxCount = await _db.Mailboxes.CountAsync(m => m.DomainId == domain.Id, ct);
        if (mailboxCount >= domain.MaxMailboxes)
            throw new ConflictException($"The domain has reached its limit of {domain.MaxMailboxes} mailboxes.");

        Guid? userId = request.UserId;
        string? generatedPassword = null;

        if (userId is null)
        {
            var existing = await _users.FindByEmailAsync(address);
            if (existing is not null)
            {
                userId = existing.Id;
            }
            else
            {
                var password = string.IsNullOrWhiteSpace(request.Password) ? PasswordPolicy.GenerateStrongPassword() : request.Password;
                var user = new AppUser
                {
                    Id = Guid.NewGuid(),
                    UserName = address,
                    Email = address,
                    EmailConfirmed = true,
                    DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? address : request.DisplayName.Trim(),
                    IsActive = true
                };
                var created = await _users.CreateAsync(user, password);
                if (!created.Succeeded)
                    throw new ValidationFailedException(string.Join(" ", created.Errors.Select(e => e.Description)));

                await _users.AddToRoleAsync(user, Policies.User);
                userId = user.Id;
                if (string.IsNullOrWhiteSpace(request.Password)) generatedPassword = password;
            }
        }

        var mailbox = new Mailbox
        {
            Id = Guid.NewGuid(),
            DomainId = domain.Id,
            LocalPart = request.LocalPart.ToLowerInvariant(),
            Address = address,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? request.LocalPart : request.DisplayName.Trim(),
            UserId = userId,
            QuotaBytes = request.QuotaBytes ?? domain.DefaultMailboxQuotaBytes,
            IsShared = request.IsShared
        };

        _db.Mailboxes.Add(mailbox);
        _db.Folders.AddRange(FolderFactory.CreateDefaults(mailbox.Id));
        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync("mailbox.create", "mailbox", mailbox.Id.ToString(), new { mailbox.Address }, ct);
        mailbox.Domain = domain;

        return Ok(new { mailbox = ToDto(mailbox), generatedPassword });
    }

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<AdminMailboxDto>> Update(Guid id, [FromBody] UpdateAdminMailboxRequest request, CancellationToken ct)
    {
        var mailbox = await _db.Mailboxes.Include(m => m.Domain).FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new NotFoundException("Mailbox not found.");

        if (!await _access.CanAdminDomainAsync(mailbox.DomainId, ct))
            throw new ForbiddenException("You do not administer this domain.");

        if (request.DisplayName is not null) mailbox.DisplayName = request.DisplayName.Trim();
        if (request.QuotaBytes is { } quota) mailbox.QuotaBytes = quota;
        if (request.IsActive is { } active)
        {
            if (!active && mailbox.IsSystem) throw new ValidationFailedException("System mailboxes cannot be deactivated.");
            mailbox.IsActive = active;
        }
        if (request.UserId is { } userId) mailbox.UserId = userId;

        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("mailbox.update", "mailbox", id.ToString(), ct: ct);
        return Ok(ToDto(mailbox));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var mailbox = await _db.Mailboxes.FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new NotFoundException("Mailbox not found.");

        if (!await _access.CanAdminDomainAsync(mailbox.DomainId, ct))
            throw new ForbiddenException("You do not administer this domain.");
        if (mailbox.IsSystem) throw new ValidationFailedException("System mailboxes cannot be deleted.");

        _db.Mailboxes.Remove(mailbox);
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("mailbox.delete", "mailbox", id.ToString(), new { mailbox.Address }, ct);
        return NoContent();
    }

    private static AdminMailboxDto ToDto(Mailbox m) => new(
        m.Id, m.Address, m.DisplayName, m.Domain?.Name ?? string.Empty, m.UserId, m.User?.Email,
        m.QuotaBytes, m.UsedBytes, m.IsActive, m.IsShared, m.CreatedAt);
}
