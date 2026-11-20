using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenPost.Api.Common;
using OpenPost.Api.Data;
using OpenPost.Api.Data.Entities;

namespace OpenPost.Api.Security;

public interface ICurrentUser
{
    Guid? Id { get; }
    string? Email { get; }
    bool IsAuthenticated { get; }
    bool IsSystemAdmin { get; }
    bool IsAdmin { get; }
    string? IpAddress { get; }
    Guid RequireUserId();
}

public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public Guid? Id
    {
        get
        {
            var raw = Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(raw, out var id) ? id : null;
        }
    }

    public string? Email => Principal?.FindFirstValue(ClaimTypes.Email);
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
    public bool IsSystemAdmin => Principal?.IsInRole(Policies.SystemAdmin) == true;
    public bool IsAdmin => IsSystemAdmin || Principal?.IsInRole(Policies.DomainAdmin) == true;

    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public Guid RequireUserId() => Id ?? throw new ForbiddenException("Not authenticated.");
}

/// <summary>Single place that decides whether the caller may read/write a mailbox.</summary>
public interface IMailboxAccess
{
    Task<Mailbox> GetAsync(Guid mailboxId, bool write = false, CancellationToken ct = default);
    Task<Mailbox> GetByAddressAsync(string address, CancellationToken ct = default);
    Task<IReadOnlyList<Mailbox>> GetAccessibleAsync(CancellationToken ct = default);
    Task<bool> CanAdminDomainAsync(Guid domainId, CancellationToken ct = default);
}

public sealed class MailboxAccess : IMailboxAccess
{
    private readonly OpenPostDbContext _db;
    private readonly ICurrentUser _current;

    public MailboxAccess(OpenPostDbContext db, ICurrentUser current)
    {
        _db = db;
        _current = current;
    }

    public async Task<Mailbox> GetAsync(Guid mailboxId, bool write = false, CancellationToken ct = default)
    {
        var mailbox = await _db.Mailboxes.Include(m => m.Domain)
            .FirstOrDefaultAsync(m => m.Id == mailboxId, ct)
            ?? throw new NotFoundException("Mailbox not found.");

        var userId = _current.RequireUserId();

        if (mailbox.UserId == userId) return mailbox;
        if (_current.IsSystemAdmin) return mailbox;
        if (await CanAdminDomainAsync(mailbox.DomainId, ct)) return mailbox;

        throw new ForbiddenException("You do not have access to this mailbox.");
    }

    public async Task<Mailbox> GetByAddressAsync(string address, CancellationToken ct = default)
    {
        var normalised = Addresses.Normalise(address);
        return await _db.Mailboxes.Include(m => m.Domain)
            .FirstOrDefaultAsync(m => m.Address == normalised, ct)
            ?? throw new NotFoundException("Mailbox not found.");
    }

    public async Task<IReadOnlyList<Mailbox>> GetAccessibleAsync(CancellationToken ct = default)
    {
        var userId = _current.RequireUserId();
        var query = _db.Mailboxes.Include(m => m.Domain).Where(m => m.IsActive);

        if (!_current.IsSystemAdmin)
        {
            var ownedDomains = await _db.Domains
                .Where(d => d.OwnerUserId == userId)
                .Select(d => d.Id)
                .ToListAsync(ct);

            query = query.Where(m => m.UserId == userId || ownedDomains.Contains(m.DomainId));
        }

        return await query.OrderBy(m => m.Address).ToListAsync(ct);
    }

    public async Task<bool> CanAdminDomainAsync(Guid domainId, CancellationToken ct = default)
    {
        if (_current.IsSystemAdmin) return true;
        var userId = _current.Id;
        if (userId is null) return false;
        return await _db.Domains.AnyAsync(d => d.Id == domainId && d.OwnerUserId == userId, ct);
    }
}
