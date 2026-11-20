using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenPost.Api.Data.Entities;
using OpenPost.Api.Mail;

namespace OpenPost.Api.Data;

/// <summary>
/// Creates the schema and seeds the first system administrator, the system domain and
/// the standard folders for every new mailbox.
/// </summary>
public sealed class DatabaseInitializer
{
    private readonly OpenPostDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly RoleManager<AppRole> _roles;
    private readonly IConfiguration _config;
    private readonly ILogger<DatabaseInitializer> _log;

    public DatabaseInitializer(
        OpenPostDbContext db,
        UserManager<AppUser> users,
        RoleManager<AppRole> roles,
        IConfiguration config,
        ILogger<DatabaseInitializer> log)
    {
        _db = db;
        _users = users;
        _roles = roles;
        _config = config;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        // Prefer real migrations when the project has them; fall back to EnsureCreated for
        // a zero-friction first run of a fresh clone.
        if (_db.Database.GetMigrations().Any())
        {
            await _db.Database.MigrateAsync(ct);
        }
        else
        {
            _log.LogWarning("No EF Core migrations found - creating schema with EnsureCreated(). " +
                            "Generate migrations before running this in production.");
            await _db.Database.EnsureCreatedAsync(ct);
        }

        foreach (var role in new[] { Policies.SystemAdmin, Policies.DomainAdmin, Policies.User })
        {
            if (!await _roles.RoleExistsAsync(role))
                await _roles.CreateAsync(new AppRole(role));
        }

        await SeedAdminAsync(ct);
        await SeedSystemDomainAsync(ct);
    }

    private async Task SeedAdminAsync(CancellationToken ct)
    {
        var email = _config["Bootstrap:AdminEmail"] ?? _config["Bootstrap__AdminEmail"];
        var password = _config["Bootstrap:AdminPassword"] ?? _config["Bootstrap__AdminPassword"];
        if (string.IsNullOrWhiteSpace(email)) return;

        var existing = await _users.FindByEmailAsync(email);
        if (existing is not null) return;

        if (string.IsNullOrWhiteSpace(password))
        {
            password = PasswordPolicy.GenerateStrongPassword();
            _log.LogWarning("Bootstrap:AdminPassword not set. Generated password for {Email}: {Password} " +
                            "(change it immediately after first login).", email, password);
        }

        var admin = new AppUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = "System Administrator",
            IsActive = true
        };

        var result = await _users.CreateAsync(admin, password);
        if (!result.Succeeded)
        {
            _log.LogError("Could not create bootstrap admin: {Errors}",
                string.Join("; ", result.Errors.Select(e => e.Description)));
            return;
        }

        await _users.AddToRoleAsync(admin, Policies.SystemAdmin);
        _log.LogInformation("Bootstrap system administrator {Email} created.", email);
    }

    private async Task SeedSystemDomainAsync(CancellationToken ct)
    {
        var domainName = (_config["Bootstrap:Domain"] ?? "localhost").Trim().ToLowerInvariant();
        var domain = await _db.Domains.FirstOrDefaultAsync(d => d.Name == domainName, ct);
        if (domain is not null) return;

        domain = new MailDomain
        {
            Id = Guid.NewGuid(),
            Name = domainName,
            DisplayName = "Local system domain",
            IsActive = true
        };
        var keys = DkimKeyGenerator.Create("openpost");
        domain.DkimPrivateKeyPem = keys.PrivateKeyPem;
        domain.DkimPublicKeyBase64 = keys.PublicKeyBase64;
        _db.Domains.Add(domain);

        // postmaster@<domain> is required by RFC 5321 and is never deletable.
        var postmaster = new Mailbox
        {
            Id = Guid.NewGuid(),
            DomainId = domain.Id,
            LocalPart = "postmaster",
            Address = $"postmaster@{domainName}",
            DisplayName = "Postmaster",
            IsSystem = true,
            IsActive = true
        };
        _db.Mailboxes.Add(postmaster);
        _db.Folders.AddRange(FolderFactory.CreateDefaults(postmaster.Id));

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("System domain {Domain} with postmaster mailbox created.", domainName);
    }
}

/// <summary>Standard folder set created for every mailbox.</summary>
public static class FolderFactory
{
    public static IEnumerable<Folder> CreateDefaults(Guid mailboxId) => new List<Folder>
    {
        new() { Id = Guid.NewGuid(), MailboxId = mailboxId, Name = "Inbox",   Kind = FolderKind.Inbox,   SortOrder = 0 },
        new() { Id = Guid.NewGuid(), MailboxId = mailboxId, Name = "Sent",    Kind = FolderKind.Sent,    SortOrder = 1 },
        new() { Id = Guid.NewGuid(), MailboxId = mailboxId, Name = "Drafts",  Kind = FolderKind.Drafts,  SortOrder = 2 },
        new() { Id = Guid.NewGuid(), MailboxId = mailboxId, Name = "Archive", Kind = FolderKind.Archive, SortOrder = 3 },
        new() { Id = Guid.NewGuid(), MailboxId = mailboxId, Name = "Junk",    Kind = FolderKind.Junk,    SortOrder = 4 },
        new() { Id = Guid.NewGuid(), MailboxId = mailboxId, Name = "Trash",   Kind = FolderKind.Trash,   SortOrder = 5 }
    };
}
