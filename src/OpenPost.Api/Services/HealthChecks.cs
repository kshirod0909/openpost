using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenPost.Api.Data;
using OpenPost.Api.Mail;

namespace OpenPost.Api.Services;

public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly OpenPostDbContext _db;

    public DatabaseHealthCheck(OpenPostDbContext db) => _db = db;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var canConnect = await _db.Database.CanConnectAsync(cancellationToken);
            return canConnect
                ? HealthCheckResult.Healthy("PostgreSQL reachable")
                : HealthCheckResult.Unhealthy("PostgreSQL not reachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL check failed", ex);
        }
    }
}

public sealed class StorageHealthCheck : IHealthCheck
{
    private readonly IMessageStorage _storage;

    public StorageHealthCheck(IMessageStorage storage) => _storage = storage;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var probe = Path.Combine("health", Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new MemoryStream("ok"u8.ToArray()))
            {
                await _storage.WriteAsync(probe, stream, cancellationToken);
            }
            await _storage.DeleteAsync(probe, cancellationToken);
            return HealthCheckResult.Healthy("Message storage writable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Message storage not writable", ex);
        }
    }
}
