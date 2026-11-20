using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
[Route("api/v1/admin/domains")]
public sealed class AdminDomainsController : ControllerBase
{
    private readonly OpenPostDbContext _db;
    private readonly ICurrentUser _current;
    private readonly IMailboxAccess _access;
    private readonly AuditWriter _audit;
    private readonly MailServerOptions _mailOptions;

    public AdminDomainsController(
        OpenPostDbContext db,
        ICurrentUser current,
        IMailboxAccess access,
        AuditWriter audit,
        IOptions<MailServerOptions> mailOptions)
    {
        _db = db;
        _current = current;
        _access = access;
        _audit = audit;
        _mailOptions = mailOptions.Value;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DomainDto>>> List(CancellationToken ct)
    {
        var query = _db.Domains.AsQueryable();
        if (!_current.IsSystemAdmin)
        {
            var userId = _current.RequireUserId();
            query = query.Where(d => d.OwnerUserId == userId);
        }

        var domains = await query.OrderBy(d => d.Name).ToListAsync(ct);
        var counts = await _db.Mailboxes.GroupBy(m => m.DomainId)
            .Select(g => new { DomainId = g.Key, Count = g.Count() }).ToListAsync(ct);
        var aliasCounts = await _db.Aliases.GroupBy(a => a.DomainId)
            .Select(g => new { DomainId = g.Key, Count = g.Count() }).ToListAsync(ct);

        return Ok(domains.Select(d => new DomainDto(
            d.Id, d.Name, d.DisplayName, d.IsActive,
            counts.FirstOrDefault(c => c.DomainId == d.Id)?.Count ?? 0,
            aliasCounts.FirstOrDefault(c => c.DomainId == d.Id)?.Count ?? 0,
            d.DkimSelector, d.DmarcPolicy, d.CatchAllMailboxId, d.CreatedAt)).ToList());
    }

    [HttpPost]
    [Authorize(Policy = Policies.RequireSystemAdmin)]
    public async Task<ActionResult<DomainDto>> Create([FromBody] CreateDomainRequest request, CancellationToken ct)
    {
        var name = (request.Name ?? string.Empty).Trim().ToLowerInvariant();
        if (!IsValidDomain(name)) throw new ValidationFailedException("Enter a valid domain name, e.g. example.org.");

        if (await _db.Domains.AnyAsync(d => d.Name == name, ct))
            throw new ConflictException("That domain is already hosted on this instance.");

        var keys = DkimKeyGenerator.Create("openpost");
        var domain = new MailDomain
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? name : request.DisplayName.Trim(),
            OwnerUserId = _current.IsSystemAdmin ? null : _current.Id,
            DefaultMailboxQuotaBytes = request.DefaultMailboxQuotaBytes ?? 2L * 1024 * 1024 * 1024,
            MaxMailboxes = request.MaxMailboxes ?? 100,
            DkimSelector = "openpost",
            DkimPrivateKeyPem = keys.PrivateKeyPem,
            DkimPublicKeyBase64 = keys.PublicKeyBase64
        };

        _db.Domains.Add(domain);
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("domain.create", "domain", domain.Id.ToString(), new { domain.Name }, ct);

        return Ok(new DomainDto(domain.Id, domain.Name, domain.DisplayName, domain.IsActive, 0, 0,
            domain.DkimSelector, domain.DmarcPolicy, null, domain.CreatedAt));
    }

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<DomainDto>> Update(Guid id, [FromBody] UpdateDomainRequest request, CancellationToken ct)
    {
        var domain = await LoadAdminDomainAsync(id, ct);

        if (request.DisplayName is not null) domain.DisplayName = request.DisplayName.Trim();
        if (request.IsActive is { } active) domain.IsActive = active;
        if (request.DmarcPolicy is not null)
        {
            var policy = request.DmarcPolicy.ToLowerInvariant();
            if (policy is not ("none" or "quarantine" or "reject"))
                throw new ValidationFailedException("DMARC policy must be none, quarantine or reject.");
            domain.DmarcPolicy = policy;
        }
        if (request.CatchAllMailboxId is { } catchAll)
        {
            var exists = await _db.Mailboxes.AnyAsync(m => m.Id == catchAll && m.DomainId == id, ct);
            if (!exists) throw new ValidationFailedException("Catch-all mailbox must belong to this domain.");
            domain.CatchAllMailboxId = catchAll;
        }
        if (request.DefaultMailboxQuotaBytes is { } quota) domain.DefaultMailboxQuotaBytes = quota;
        if (request.MaxMailboxes is { } max) domain.MaxMailboxes = max;

        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("domain.update", "domain", domain.Id.ToString(), ct: ct);

        var mailboxCount = await _db.Mailboxes.CountAsync(m => m.DomainId == id, ct);
        var aliasCount = await _db.Aliases.CountAsync(a => a.DomainId == id, ct);

        return Ok(new DomainDto(domain.Id, domain.Name, domain.DisplayName, domain.IsActive, mailboxCount, aliasCount,
            domain.DkimSelector, domain.DmarcPolicy, domain.CatchAllMailboxId, domain.CreatedAt));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.RequireSystemAdmin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var domain = await LoadAdminDomainAsync(id, ct);

        if (await _db.Mailboxes.AnyAsync(m => m.DomainId == id && m.IsSystem, ct))
            throw new ConflictException("Remove the system mailboxes (postmaster) before deleting the domain.");

        if (await _db.Mailboxes.AnyAsync(m => m.DomainId == id, ct))
            throw new ConflictException("Delete or move the mailboxes on this domain first.");

        _db.Domains.Remove(domain);
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("domain.delete", "domain", id.ToString(), new { domain.Name }, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/dkim/rotate")]
    public async Task<ActionResult<DnsRecordsResponse>> RotateDkim(Guid id, CancellationToken ct)
    {
        var domain = await LoadAdminDomainAsync(id, ct);
        var selector = $"openpost{DateTimeOffset.UtcNow:yyyyMM}";
        var keys = DkimKeyGenerator.Create(selector);

        domain.DkimSelector = selector;
        domain.DkimPrivateKeyPem = keys.PrivateKeyPem;
        domain.DkimPublicKeyBase64 = keys.PublicKeyBase64;
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("domain.dkim_rotate", "domain", domain.Id.ToString(), new { selector }, ct);

        return Ok(BuildDnsRecords(domain));
    }

    [HttpGet("{id:guid}/dns-records")]
    public async Task<ActionResult<DnsRecordsResponse>> DnsRecords(Guid id, CancellationToken ct)
    {
        var domain = await LoadAdminDomainAsync(id, ct);
        return Ok(BuildDnsRecords(domain));
    }

    // ---------- Aliases ----------

    [HttpGet("{id:guid}/aliases")]
    public async Task<ActionResult<IReadOnlyList<AliasDto>>> ListAliases(Guid id, CancellationToken ct)
    {
        await LoadAdminDomainAsync(id, ct);
        var aliases = await _db.Aliases.Where(a => a.DomainId == id).OrderBy(a => a.Address).ToListAsync(ct);
        return Ok(aliases.Select(a => new AliasDto(a.Id, a.Address,
            MimeMapper.FromJson(a.TargetsJson).Select(t => t.Address).ToList(), a.IsActive)).ToList());
    }

    [HttpPost("{id:guid}/aliases")]
    public async Task<ActionResult<AliasDto>> CreateAlias(Guid id, [FromBody] CreateAliasRequest request, CancellationToken ct)
    {
        var domain = await LoadAdminDomainAsync(id, ct);

        if (!Addresses.IsValidLocalPart(request.LocalPart))
            throw new ValidationFailedException("Invalid alias local part.");

        var address = $"{request.LocalPart.ToLowerInvariant()}@{domain.Name}";
        if (await _db.Aliases.AnyAsync(a => a.Address == address, ct) || await _db.Mailboxes.AnyAsync(m => m.Address == address, ct))
            throw new ConflictException("That address already exists on this domain.");

        if (request.Targets is null || request.Targets.Length == 0)
            throw new ValidationFailedException("An alias needs at least one target address.");

        var alias = new MailAlias
        {
            Id = Guid.NewGuid(),
            DomainId = id,
            LocalPart = request.LocalPart.ToLowerInvariant(),
            Address = address,
            TargetsJson = MimeMapper.ToJson(request.Targets.Select(t => new AddressDto(t.Trim().ToLowerInvariant(), null)))
        };

        _db.Aliases.Add(alias);
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("alias.create", "alias", alias.Id.ToString(), new { alias.Address }, ct);

        return Ok(new AliasDto(alias.Id, alias.Address, request.Targets, alias.IsActive));
    }

    [HttpDelete("{id:guid}/aliases/{aliasId:guid}")]
    public async Task<IActionResult> DeleteAlias(Guid id, Guid aliasId, CancellationToken ct)
    {
        await LoadAdminDomainAsync(id, ct);
        var alias = await _db.Aliases.FirstOrDefaultAsync(a => a.Id == aliasId && a.DomainId == id, ct)
            ?? throw new NotFoundException("Alias not found.");
        _db.Aliases.Remove(alias);
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("alias.delete", "alias", aliasId.ToString(), ct: ct);
        return NoContent();
    }

    private async Task<MailDomain> LoadAdminDomainAsync(Guid id, CancellationToken ct)
    {
        var domain = await _db.Domains.FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? throw new NotFoundException("Domain not found.");

        if (!await _access.CanAdminDomainAsync(id, ct))
            throw new ForbiddenException("You do not administer this domain.");

        return domain;
    }

    private DnsRecordsResponse BuildDnsRecords(MailDomain domain)
    {
        var host = _mailOptions.HostName;
        var records = new List<DnsRecordDto>
        {
            new("MX", domain.Name, host, 10, "Routes inbound mail to this server."),
            new("TXT", domain.Name, $"v=spf1 mx a:{host} -all", null, "SPF: only this server may send mail for the domain."),
            new("TXT", $"{domain.DkimSelector}._domainkey.{domain.Name}",
                DkimKeyGenerator.BuildDnsRecord(domain.DkimPublicKeyBase64 ?? string.Empty), null, "DKIM public key used to verify signatures."),
            new("TXT", $"_dmarc.{domain.Name}",
                $"v=DMARC1; p={domain.DmarcPolicy}; rua=mailto:postmaster@{domain.Name}; adkim=r; aspf=r; pct=100",
                null, "DMARC policy and aggregate report address."),
            new("CNAME", $"autoconfig.{domain.Name}", host, null, "Optional: client autoconfiguration."),
            new("PTR", host, host, null, "Reverse DNS for the server's public IP must match this hostname.")
        };

        return new DnsRecordsResponse(domain.Name, records);
    }

    private static bool IsValidDomain(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 253 || !name.Contains('.')) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(name,
            @"^(?=.{4,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$");
    }
}
