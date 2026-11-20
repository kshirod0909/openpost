using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenPost.Api.Common;
using OpenPost.Api.Contracts;
using OpenPost.Api.Data;
using OpenPost.Api.Data.Entities;
using OpenPost.Api.Security;

namespace OpenPost.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/mailboxes")]
public sealed class MailboxesController : ControllerBase
{
    private readonly OpenPostDbContext _db;
    private readonly IMailboxAccess _access;
    private readonly ICurrentUser _current;

    public MailboxesController(OpenPostDbContext db, IMailboxAccess access, ICurrentUser current)
    {
        _db = db;
        _access = access;
        _current = current;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<MailboxDto>>> List(CancellationToken ct)
    {
        var mailboxes = await _access.GetAccessibleAsync(ct);
        return Ok(mailboxes.Select(m => new MailboxDto(
            m.Id, m.Address, m.DisplayName, m.DomainId, m.Domain?.Name ?? string.Empty,
            m.QuotaBytes, m.UsedBytes, m.IsShared, m.IsSystem)).ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<MailboxDto>> Get(Guid id, CancellationToken ct)
    {
        var mailbox = await _access.GetAsync(id, ct: ct);
        return Ok(new MailboxDto(mailbox.Id, mailbox.Address, mailbox.DisplayName, mailbox.DomainId,
            mailbox.Domain?.Name ?? string.Empty, mailbox.QuotaBytes, mailbox.UsedBytes, mailbox.IsShared, mailbox.IsSystem));
    }

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<MailboxDto>> Update(Guid id, [FromBody] UpdateMailboxRequest request, CancellationToken ct)
    {
        var mailbox = await _access.GetAsync(id, write: true, ct: ct);

        var isOwner = mailbox.UserId == _current.Id;
        var isAdmin = _current.IsAdmin || await _access.CanAdminDomainAsync(mailbox.DomainId, ct);

        if (request.DisplayName is not null)
        {
            if (!isOwner && !isAdmin) throw new ForbiddenException("Only the mailbox owner can rename it.");
            mailbox.DisplayName = request.DisplayName.Trim();
        }

        if (request.QuotaBytes is { } quota)
        {
            if (!isAdmin) throw new ForbiddenException("Only an administrator can change the quota.");
            if (quota < 50L * 1024 * 1024) throw new ValidationFailedException("Quota must be at least 50 MB.");
            mailbox.QuotaBytes = quota;
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new MailboxDto(mailbox.Id, mailbox.Address, mailbox.DisplayName, mailbox.DomainId,
            mailbox.Domain?.Name ?? string.Empty, mailbox.QuotaBytes, mailbox.UsedBytes, mailbox.IsShared, mailbox.IsSystem));
    }
}

[ApiController]
[Authorize]
[Route("api/v1/mailboxes/{mailboxId:guid}/folders")]
public sealed class FoldersController : ControllerBase
{
    private readonly OpenPostDbContext _db;
    private readonly IMailboxAccess _access;

    public FoldersController(OpenPostDbContext db, IMailboxAccess access)
    {
        _db = db;
        _access = access;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<FolderDto>>> List(Guid mailboxId, CancellationToken ct)
    {
        await _access.GetAsync(mailboxId, ct: ct);

        var folders = await _db.Folders.Where(f => f.MailboxId == mailboxId).ToListAsync(ct);
        var counts = await _db.Messages
            .Where(m => m.MailboxId == mailboxId && !m.IsDraft)
            .GroupBy(m => m.FolderId)
            .Select(g => new { FolderId = g.Key, Total = g.Count(), Unread = g.Count(m => !m.IsRead) })
            .ToListAsync(ct);

        var result = folders
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Name)
            .Select(f =>
            {
                var stat = counts.FirstOrDefault(c => c.FolderId == f.Id);
                return new FolderDto(f.Id, f.Name, f.Kind.ToString(), f.SortOrder, stat?.Unread ?? 0, stat?.Total ?? 0);
            })
            .ToList();

        return Ok(result);
    }

    [HttpPost]
    public async Task<ActionResult<FolderDto>> Create(Guid mailboxId, [FromBody] CreateFolderRequest request, CancellationToken ct)
    {
        await _access.GetAsync(mailboxId, write: true, ct: ct);

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 80) throw new ValidationFailedException("Folder name must be 1-80 characters.");

        if (await _db.Folders.AnyAsync(f => f.MailboxId == mailboxId && f.Name == name, ct))
            throw new ConflictException("A folder with that name already exists.");

        var folder = new Folder
        {
            Id = Guid.NewGuid(),
            MailboxId = mailboxId,
            Name = name,
            Kind = FolderKind.Custom,
            SortOrder = 100 + await _db.Folders.CountAsync(f => f.MailboxId == mailboxId, ct)
        };

        _db.Folders.Add(folder);
        await _db.SaveChangesAsync(ct);
        return Ok(new FolderDto(folder.Id, folder.Name, folder.Kind.ToString(), folder.SortOrder, 0, 0));
    }

    [HttpPatch("{folderId:guid}")]
    public async Task<IActionResult> Rename(Guid mailboxId, Guid folderId, [FromBody] UpdateFolderRequest request, CancellationToken ct)
    {
        await _access.GetAsync(mailboxId, write: true, ct: ct);

        var folder = await _db.Folders.FirstOrDefaultAsync(f => f.Id == folderId && f.MailboxId == mailboxId, ct)
            ?? throw new NotFoundException("Folder not found.");

        if (folder.Kind != FolderKind.Custom)
            throw new ValidationFailedException("System folders cannot be renamed.");

        folder.Name = request.Name.Trim();
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{folderId:guid}")]
    public async Task<IActionResult> Delete(Guid mailboxId, Guid folderId, CancellationToken ct)
    {
        await _access.GetAsync(mailboxId, write: true, ct: ct);

        var folder = await _db.Folders.FirstOrDefaultAsync(f => f.Id == folderId && f.MailboxId == mailboxId, ct)
            ?? throw new NotFoundException("Folder not found.");

        if (folder.Kind != FolderKind.Custom)
            throw new ValidationFailedException("System folders cannot be deleted.");

        if (await _db.Messages.AnyAsync(m => m.FolderId == folderId, ct))
            throw new ConflictException("Move or delete the messages in this folder first.");

        _db.Folders.Remove(folder);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}

[ApiController]
[Authorize]
[Route("api/v1/mailboxes/{mailboxId:guid}/labels")]
public sealed class LabelsController : ControllerBase
{
    private readonly OpenPostDbContext _db;
    private readonly IMailboxAccess _access;

    public LabelsController(OpenPostDbContext db, IMailboxAccess access)
    {
        _db = db;
        _access = access;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<LabelDto>>> List(Guid mailboxId, CancellationToken ct)
    {
        await _access.GetAsync(mailboxId, ct: ct);
        return Ok(await _db.Labels.Where(l => l.MailboxId == mailboxId)
            .OrderBy(l => l.Name)
            .Select(l => new LabelDto(l.Id, l.Name, l.Color))
            .ToListAsync(ct));
    }

    [HttpPost]
    public async Task<ActionResult<LabelDto>> Create(Guid mailboxId, [FromBody] CreateLabelRequest request, CancellationToken ct)
    {
        await _access.GetAsync(mailboxId, write: true, ct: ct);

        var label = new Label
        {
            Id = Guid.NewGuid(),
            MailboxId = mailboxId,
            Name = request.Name.Trim(),
            Color = string.IsNullOrWhiteSpace(request.Color) ? "#6366f1" : request.Color
        };

        _db.Labels.Add(label);
        await _db.SaveChangesAsync(ct);
        return Ok(new LabelDto(label.Id, label.Name, label.Color));
    }

    [HttpDelete("{labelId:guid}")]
    public async Task<IActionResult> Delete(Guid mailboxId, Guid labelId, CancellationToken ct)
    {
        await _access.GetAsync(mailboxId, write: true, ct: ct);
        var label = await _db.Labels.FirstOrDefaultAsync(l => l.Id == labelId && l.MailboxId == mailboxId, ct)
            ?? throw new NotFoundException("Label not found.");
        _db.Labels.Remove(label);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
