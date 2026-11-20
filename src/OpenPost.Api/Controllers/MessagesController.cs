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

namespace OpenPost.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/messages")]
public sealed class MessagesController : ControllerBase
{
    private readonly OpenPostDbContext _db;
    private readonly IMailboxAccess _access;
    private readonly IMessageStore _store;
    private readonly IMessageStorage _storage;

    public MessagesController(OpenPostDbContext db, IMailboxAccess access, IMessageStore store, IMessageStorage storage)
    {
        _db = db;
        _access = access;
        _store = store;
        _storage = storage;
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<MessageDetailDto>> Get(Guid id, CancellationToken ct)
    {
        var message = await LoadAsync(id, ct);

        // Opening a message marks it read; drafts stay as they are.
        if (!message.IsRead && !message.IsDraft)
        {
            message.IsRead = true;
            await _db.SaveChangesAsync(ct);
        }

        return Ok(MimeMapper.ToDetail(message));
    }

    [HttpGet("{id:guid}/raw")]
    public async Task<IActionResult> Raw(Guid id, CancellationToken ct)
    {
        var message = await LoadAsync(id, ct);
        Stream stream;
        try
        {
            stream = await _store.OpenRawAsync(message, ct);
        }
        catch (FileNotFoundException)
        {
            throw new NotFoundException("The original message file is no longer on disk.");
        }

        return File(stream, "message/rfc822", $"{message.Uid}-{SafeName(message.Subject)}.eml");
    }

    [HttpGet("{id:guid}/attachments/{attachmentId:guid}")]
    public async Task<IActionResult> Attachment(Guid id, Guid attachmentId, CancellationToken ct)
    {
        var message = await LoadAsync(id, ct);
        var attachment = message.Attachments.FirstOrDefault(a => a.Id == attachmentId)
            ?? throw new NotFoundException("Attachment not found.");

        var path = _storage.ResolvePath(attachment.StoragePath);
        if (!System.IO.File.Exists(path)) throw new NotFoundException("Attachment file missing from storage.");

        return File(System.IO.File.OpenRead(path), attachment.ContentType, attachment.FileName);
    }

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<MessageDetailDto>> Update(Guid id, [FromBody] UpdateMessageRequest request, CancellationToken ct)
    {
        var message = await LoadAsync(id, ct);

        if (request.IsRead is { } read) message.IsRead = read;
        if (request.IsFlagged is { } flagged) message.IsFlagged = flagged;

        if (request.FolderId is { } folderId)
        {
            var folder = await _db.Folders.FirstOrDefaultAsync(f => f.Id == folderId && f.MailboxId == message.MailboxId, ct)
                ?? throw new NotFoundException("Target folder not found.");
            message.FolderId = folder.Id;
            message.Folder = folder;
            message.IsDeleted = folder.Kind == FolderKind.Trash;
        }

        if (request.LabelIds is not null)
        {
            var valid = await _db.Labels.Where(l => l.MailboxId == message.MailboxId && request.LabelIds.Contains(l.Id))
                .Select(l => l.Id).ToListAsync(ct);
            message.LabelsJson = MimeMapper.ToJson(valid);
        }

        await _db.SaveChangesAsync(ct);
        return Ok(MimeMapper.ToDetail(message));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var message = await LoadAsync(id, ct);

        if (message.Folder?.Kind != FolderKind.Trash)
        {
            var trash = await _db.Folders.FirstOrDefaultAsync(f => f.MailboxId == message.MailboxId && f.Kind == FolderKind.Trash, ct)
                ?? throw new NotFoundException("Trash folder missing for this mailbox.");

            message.FolderId = trash.Id;
            message.IsDeleted = true;
            await _db.SaveChangesAsync(ct);
            return NoContent();
        }

        await _store.DeleteAsync(message, ct);
        return NoContent();
    }

    private async Task<StoredMessage> LoadAsync(Guid id, CancellationToken ct)
    {
        var message = await _db.Messages
            .Include(m => m.Folder)
            .Include(m => m.Attachments)
            .FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new NotFoundException("Message not found.");

        await _access.GetAsync(message.MailboxId, ct: ct);
        return message;
    }

    private static string SafeName(string subject)
    {
        var name = string.Join('_', subject.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        return name.Length > 60 ? name[..60] : name;
    }
}

[ApiController]
[Authorize]
[Route("api/v1/mailboxes/{mailboxId:guid}")]
public sealed class MailboxMessagesController : ControllerBase
{
    private readonly OpenPostDbContext _db;
    private readonly IMailboxAccess _access;
    private readonly IMailSubmissionService _submission;
    private readonly IMessageStore _store;
    private readonly IMessageStorage _storage;
    private readonly StorageOptions _storageOptions;

    public MailboxMessagesController(
        OpenPostDbContext db,
        IMailboxAccess access,
        IMailSubmissionService submission,
        IMessageStore store,
        IMessageStorage storage,
        IOptions<StorageOptions> storageOptions)
    {
        _db = db;
        _access = access;
        _submission = submission;
        _store = store;
        _storage = storage;
        _storageOptions = storageOptions.Value;
    }

    [HttpGet("messages")]
    public async Task<ActionResult<PagedResult<MessageSummaryDto>>> List(
        Guid mailboxId,
        [FromQuery] Guid? folderId,
        [FromQuery] string? folder,
        [FromQuery] string? q,
        [FromQuery] bool? unread,
        [FromQuery] bool? flagged,
        [FromQuery] bool includeDrafts = false,
        [FromQuery] int page = 1,
        [FromQuery] int size = 25,
        [FromQuery] string sort = "newest",
        CancellationToken ct = default)
    {
        await _access.GetAsync(mailboxId, ct: ct);
        (page, size) = Paging.Normalise(page, size);

        var query = _db.Messages.Include(m => m.Folder).Where(m => m.MailboxId == mailboxId);

        if (!includeDrafts) query = query.Where(m => !m.IsDraft);

        if (folderId is { } fid) query = query.Where(m => m.FolderId == fid);
        else if (!string.IsNullOrWhiteSpace(folder) && Enum.TryParse<FolderKind>(folder, true, out var kind))
        {
            var resolved = await _db.Folders
                .Where(f => f.MailboxId == mailboxId && f.Kind == kind)
                .Select(f => (Guid?)f.Id)
                .FirstOrDefaultAsync(ct);
            if (resolved is null) return Ok(new PagedResult<MessageSummaryDto>(Array.Empty<MessageSummaryDto>(), page, size, 0));
            query = query.Where(m => m.FolderId == resolved);
        }

        if (unread == true) query = query.Where(m => !m.IsRead);
        if (flagged == true) query = query.Where(m => m.IsFlagged);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = "%" + q.Trim() + "%";
            query = query.Where(m =>
                EF.Functions.ILike(m.Subject, term) ||
                EF.Functions.ILike(m.Preview, term) ||
                EF.Functions.ILike(m.FromAddress, term) ||
                EF.Functions.ILike(m.FromName, term) ||
                EF.Functions.ILike(m.BodyText ?? string.Empty, term));
        }

        var total = await query.LongCountAsync(ct);

        query = sort.Equals("oldest", StringComparison.OrdinalIgnoreCase)
            ? query.OrderBy(m => m.ReceivedAt)
            : query.OrderByDescending(m => m.ReceivedAt);

        var items = await query.Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return Ok(new PagedResult<MessageSummaryDto>(items.Select(MimeMapper.ToSummary).ToList(), page, size, total));
    }

    [HttpGet("threads/{threadId}")]
    public async Task<ActionResult<IReadOnlyList<MessageSummaryDto>>> Thread(Guid mailboxId, string threadId, CancellationToken ct)
    {
        await _access.GetAsync(mailboxId, ct: ct);
        var items = await _db.Messages.Include(m => m.Folder)
            .Where(m => m.MailboxId == mailboxId && m.ThreadId == threadId)
            .OrderBy(m => m.ReceivedAt)
            .ToListAsync(ct);
        return Ok(items.Select(MimeMapper.ToSummary).ToList());
    }

    [HttpPost("messages/bulk")]
    public async Task<ActionResult<object>> Bulk(Guid mailboxId, [FromBody] BulkActionRequest request, CancellationToken ct)
    {
        await _access.GetAsync(mailboxId, write: true, ct: ct);

        if (request.MessageIds.Length == 0) return Ok(new { affected = 0 });

        var messages = await _db.Messages.Include(m => m.Folder).Include(m => m.Attachments)
            .Where(m => m.MailboxId == mailboxId && request.MessageIds.Contains(m.Id))
            .ToListAsync(ct);

        var folders = await _db.Folders.Where(f => f.MailboxId == mailboxId).ToListAsync(ct);
        var permanentDeletes = new List<StoredMessage>();
        var affected = 0;

        foreach (var message in messages)
        {
            switch (request.Action.ToLowerInvariant())
            {
                case "markread": message.IsRead = true; break;
                case "markunread": message.IsRead = false; break;
                case "flag": message.IsFlagged = true; break;
                case "unflag": message.IsFlagged = false; break;
                case "move":
                {
                    var target = folders.FirstOrDefault(f => f.Id == request.TargetFolderId)
                        ?? throw new NotFoundException("Target folder not found.");
                    message.FolderId = target.Id;
                    message.Folder = target;
                    message.IsDeleted = target.Kind == FolderKind.Trash;
                    break;
                }
                case "delete":
                {
                    var trash = folders.FirstOrDefault(f => f.Kind == FolderKind.Trash)
                        ?? throw new NotFoundException("Trash folder missing.");
                    if (message.Folder?.Kind == FolderKind.Trash) permanentDeletes.Add(message);
                    else { message.FolderId = trash.Id; message.IsDeleted = true; }
                    break;
                }
                case "spam":
                {
                    var junk = folders.FirstOrDefault(f => f.Kind == FolderKind.Junk)
                        ?? throw new NotFoundException("Junk folder missing.");
                    message.FolderId = junk.Id;
                    message.SpamVerdict = SpamVerdict.Spam;
                    break;
                }
                case "notspam":
                {
                    var inbox = folders.FirstOrDefault(f => f.Kind == FolderKind.Inbox)
                        ?? throw new NotFoundException("Inbox folder missing.");
                    message.FolderId = inbox.Id;
                    message.SpamVerdict = SpamVerdict.Clean;
                    message.SpamScore = 0;
                    break;
                }
                case "archive":
                {
                    var archive = folders.FirstOrDefault(f => f.Kind == FolderKind.Archive)
                        ?? throw new NotFoundException("Archive folder missing.");
                    message.FolderId = archive.Id;
                    break;
                }
                default:
                    throw new ValidationFailedException($"Unknown action '{request.Action}'.");
            }
            affected++;
        }

        await _db.SaveChangesAsync(ct);

        foreach (var message in permanentDeletes)
        {
            await _store.DeleteAsync(message, ct);
            affected++;
        }

        return Ok(new { affected });
    }

    [HttpPost("send")]
    public async Task<ActionResult<SendMessageResponse>> Send(Guid mailboxId, [FromBody] SendMessageRequest request, CancellationToken ct)
    {
        var mailbox = await _access.GetAsync(mailboxId, write: true, ct);
        var response = await _submission.SendAsync(mailbox, request, ct);
        return Ok(response);
    }

    [HttpPost("drafts")]
    public async Task<ActionResult<MessageDetailDto>> SaveDraft(Guid mailboxId, [FromBody] DraftRequest request, CancellationToken ct)
    {
        var mailbox = await _access.GetAsync(mailboxId, write: true, ct);
        var draft = await _submission.SaveDraftAsync(mailbox, request, ct);
        return Ok(MimeMapper.ToDetail(draft));
    }

    [HttpPost("attachments")]
    [RequestSizeLimit(64 * 1024 * 1024)]
    public async Task<ActionResult<UploadResponse>> Upload(Guid mailboxId, [FromForm] IFormFile file, CancellationToken ct)
    {
        var mailbox = await _access.GetAsync(mailboxId, write: true, ct);

        if (file is null || file.Length == 0) throw new ValidationFailedException("No file was uploaded.");
        if (file.Length > _storageOptions.MaxAttachmentBytes)
            throw new ValidationFailedException($"Attachment exceeds the {_storageOptions.MaxAttachmentBytes / (1024 * 1024)} MB limit.");

        var attachmentId = Guid.NewGuid();
        var fileName = LocalDiskStorage.SanitiseFileName(file.FileName);
        var path = LocalDiskStorage.AttachmentPath(mailbox.Id, attachmentId, fileName);

        await using (var stream = file.OpenReadStream())
        {
            await _storage.WriteAsync(path, stream, ct);
        }

        var attachment = new Attachment
        {
            Id = attachmentId,
            MailboxId = mailbox.Id,
            MessageId = null,
            FileName = fileName,
            ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            SizeBytes = file.Length,
            StoragePath = path,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.Attachments.Add(attachment);
        await _db.SaveChangesAsync(ct);

        return Ok(new UploadResponse(attachment.Id, attachment.FileName, attachment.SizeBytes, attachment.ContentType));
    }
}
