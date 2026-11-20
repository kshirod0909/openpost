using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenPost.Api.Common;
using OpenPost.Api.Contracts;
using OpenPost.Api.Data;
using OpenPost.Api.Data.Entities;
using OpenPost.Api.Mail.Outbound;
using OpenPost.Api.Security;

namespace OpenPost.Api.Controllers;

[ApiController]
[Authorize(Policy = Policies.RequireAdmin)]
[Route("api/v1/admin")]
public sealed class AdminOperationsController : ControllerBase
{
    private readonly OpenPostDbContext _db;
    private readonly OutboundDeliveryWorker _worker;

    public AdminOperationsController(OpenPostDbContext db, OutboundDeliveryWorker worker)
    {
        _db = db;
        _worker = worker;
    }

    [HttpGet("queue")]
    public async Task<ActionResult<PagedResult<QueueItemDto>>> Queue(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int size = 50,
        CancellationToken ct = default)
    {
        (page, size) = Paging.Normalise(page, size);
        var query = _db.Outbound.AsQueryable();

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<OutboundStatus>(status, true, out var parsed))
            query = query.Where(o => o.Status == parsed);

        var total = await query.LongCountAsync(ct);
        var rows = await query.OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);

        return Ok(new PagedResult<QueueItemDto>(rows.Select(ToDto).ToList(), page, size, total));
    }

    [HttpPost("queue/{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct)
    {
        var row = await _db.Outbound.FirstOrDefaultAsync(o => o.Id == id, ct)
            ?? throw new NotFoundException("Queue item not found.");

        if (row.Status == OutboundStatus.Sent)
            throw new ConflictException("This message has already been delivered.");

        row.Status = OutboundStatus.Queued;
        row.Attempts = 0;
        row.NextAttemptAt = DateTimeOffset.UtcNow;
        row.LockedUntil = null;
        row.LastError = null;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("queue/{id:guid}")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var row = await _db.Outbound.FirstOrDefaultAsync(o => o.Id == id, ct)
            ?? throw new NotFoundException("Queue item not found.");

        if (row.Status == OutboundStatus.Sent)
            throw new ConflictException("Delivered messages cannot be cancelled.");

        row.Status = OutboundStatus.Cancelled;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Runs one delivery pass immediately instead of waiting for the poll interval.</summary>
    [HttpPost("queue/process")]
    public async Task<ActionResult<object>> Process(CancellationToken ct)
    {
        var processed = await _worker.RunOnceAsync(ct);
        return Ok(new { processed });
    }

    [HttpGet("stats")]
    public async Task<ActionResult<StatsDto>> Stats(CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-1);

        var domains = await _db.Domains.CountAsync(ct);
        var mailboxes = await _db.Mailboxes.CountAsync(ct);
        var users = await _db.Users.CountAsync(ct);
        var messages = await _db.Messages.LongCountAsync(ct);
        var last24 = await _db.Messages.LongCountAsync(m => m.ReceivedAt >= since, ct);
        var sent24 = await _db.Outbound.LongCountAsync(o => o.SentAt >= since, ct);
        var pending = await _db.Outbound.CountAsync(o => o.Status == OutboundStatus.Queued || o.Status == OutboundStatus.Deferred || o.Status == OutboundStatus.InProgress, ct);
        var failed = await _db.Outbound.CountAsync(o => o.Status == OutboundStatus.Failed, ct);
        var storage = await _db.Mailboxes.SumAsync(m => m.UsedBytes, ct);

        var volume = await _db.Database.SqlQueryRaw<DailyVolumeDto>("""
            SELECT d::date AS "Date",
                   (SELECT count(*) FROM "Messages" m
                     WHERE m."ReceivedAt"::date = d::date AND m."IsDraft" = false) AS "Received",
                   (SELECT count(*) FROM "Outbound" o
                     WHERE o."SentAt"::date = d::date) AS "Sent",
                   (SELECT count(*) FROM "Messages" m
                     WHERE m."ReceivedAt"::date = d::date AND m."SpamVerdict" = 'Spam') AS "Spam"
            FROM generate_series(now() - interval '6 days', now(), interval '1 day') AS d
            ORDER BY 1
            """).ToListAsync(ct);

        return Ok(new StatsDto(domains, mailboxes, users, messages, last24, sent24, last24,
            pending, failed, storage, volume));
    }

    [HttpGet("audit")]
    public async Task<ActionResult<PagedResult<AuditLogDto>>> Audit(
        [FromQuery] string? action,
        [FromQuery] int page = 1,
        [FromQuery] int size = 50,
        CancellationToken ct = default)
    {
        (page, size) = Paging.Normalise(page, size);
        var query = _db.AuditLogs.AsQueryable();

        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(a => a.Action == action);

        var total = await query.LongCountAsync(ct);
        var rows = await query.OrderByDescending(a => a.CreatedAt)
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);

        return Ok(new PagedResult<AuditLogDto>(rows.Select(a => new AuditLogDto(
            a.Id, a.Action, a.ActorAddress, a.IpAddress, a.TargetType, a.TargetId, a.DetailJson, a.CreatedAt)).ToList(),
            page, size, total));
    }

    private static QueueItemDto ToDto(OutboundMessage o) => new(
        o.Id, o.EnvelopeFrom, o.EnvelopeTo, o.Subject, o.Status.ToString(), o.Attempts,
        o.CreatedAt, o.NextAttemptAt, o.SentAt, o.LastError, o.SizeBytes);
}
