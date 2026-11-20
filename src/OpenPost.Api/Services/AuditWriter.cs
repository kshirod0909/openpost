using System.Text.Json;
using OpenPost.Api.Data;
using OpenPost.Api.Data.Entities;
using OpenPost.Api.Security;

namespace OpenPost.Api.Services;

/// <summary>Append-only audit trail for security-relevant actions.</summary>
public sealed class AuditWriter
{
    private readonly OpenPostDbContext _db;
    private readonly ICurrentUser _current;
    private readonly ILogger<AuditWriter> _log;

    public AuditWriter(OpenPostDbContext db, ICurrentUser current, ILogger<AuditWriter> log)
    {
        _db = db;
        _current = current;
        _log = log;
    }

    public async Task WriteAsync(string action, string? targetType = null, string? targetId = null, object? detail = null, CancellationToken ct = default)
    {
        try
        {
            _db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                ActorUserId = _current.Id,
                ActorAddress = _current.Email,
                IpAddress = _current.IpAddress,
                Action = action,
                TargetType = targetType,
                TargetId = targetId,
                DetailJson = detail is null ? null : JsonSerializer.Serialize(detail)
            });
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Audit write failed for {Action}", action);
        }
    }
}
