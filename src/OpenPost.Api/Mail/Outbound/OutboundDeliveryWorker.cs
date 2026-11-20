using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;
using OpenPost.Api.Common;
using OpenPost.Api.Data;
using OpenPost.Api.Data.Entities;
using OpenPost.Api.Security;

namespace OpenPost.Api.Mail.Outbound;

/// <summary>
/// Durable outbound delivery worker. Rows are claimed with SELECT ... FOR UPDATE SKIP LOCKED so
/// several API replicas can share one queue without double-sending.
/// </summary>
public sealed class OutboundDeliveryWorker : BackgroundService
{
    private static readonly int[] BackoffMinutes = { 1, 5, 15, 30, 60, 180, 360, 720 };

    private readonly IServiceScopeFactory _scopes;
    private readonly DeliveryOptions _options;
    private readonly ILogger<OutboundDeliveryWorker> _log;

    public OutboundDeliveryWorker(IServiceScopeFactory scopes, IOptions<DeliveryOptions> options, ILogger<OutboundDeliveryWorker> log)
    {
        _scopes = scopes;
        _options = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Outbound delivery worker started (max {Parallel} parallel, {Attempts} attempts)",
            _options.MaxParallelDeliveries, _options.MaxAttempts);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Outbound worker iteration failed");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        List<Guid> claimed;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpenPostDbContext>();
            claimed = await ClaimAsync(db, _options.MaxParallelDeliveries, ct);
        }

        if (claimed.Count == 0) return 0;

        var gate = new SemaphoreSlim(_options.MaxParallelDeliveries);
        var tasks = claimed.Select(async id =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var scope = _scopes.CreateScope();
                await DeliverAsync(scope.ServiceProvider, id, ct);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        return claimed.Count;
    }

    private static async Task<List<Guid>> ClaimAsync(OpenPostDbContext db, int batchSize, CancellationToken ct)
    {
        // SKIP LOCKED lets several API replicas share one queue without double-sending.
        return await RawSql.QueryAsync<Guid>(db, """
            UPDATE "Outbound" SET "Status" = 'InProgress', "LockedUntil" = now() + interval '5 minutes'
            WHERE "Id" IN (
                SELECT "Id" FROM "Outbound"
                WHERE "Status" IN ('Queued', 'Deferred')
                  AND "NextAttemptAt" <= now()
                  AND ("LockedUntil" IS NULL OR "LockedUntil" < now())
                ORDER BY "NextAttemptAt"
                LIMIT @batch
                FOR UPDATE SKIP LOCKED)
            RETURNING "Id"
            """, ct, ("@batch", batchSize));
    }

    private async Task DeliverAsync(IServiceProvider services, Guid outboundId, CancellationToken ct)
    {
        var db = services.GetRequiredService<OpenPostDbContext>();
        var storage = services.GetRequiredService<IMessageStorage>();
        var mx = services.GetRequiredService<IMxResolver>();

        var row = await db.Outbound.FirstOrDefaultAsync(o => o.Id == outboundId, ct);
        if (row is null) return;

        MimeMessage message;
        await using (var stream = await storage.OpenReadAsync(row.MimePath, ct))
        {
            message = await MimeMessage.LoadAsync(stream, ct);
        }

        var recipients = row.EnvelopeTo.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        var errors = new List<string>();
        var delivered = new List<string>();

        foreach (var group in recipients.GroupBy(Addresses.DomainPart))
        {
            var hosts = await mx.ResolveAsync(group.Key, ct);
            if (hosts.Count == 0)
            {
                errors.Add($"{group.Key}: no MX record");
                continue;
            }

            var success = false;
            foreach (var host in hosts)
            {
                try
                {
                    await SendToHostAsync(host, row.EnvelopeFrom, group.ToList(), message, ct);
                    delivered.AddRange(group);
                    success = true;
                    break;
                }
                catch (Exception ex)
                {
                    errors.Add($"{host}: {ex.Message}");
                    _log.LogWarning(ex, "Delivery of {Id} to {Host} failed", outboundId, host);
                }
            }

            if (!success && !errors.Any(e => e.StartsWith(group.Key + ": no MX")))
                errors.Add($"{group.Key}: all MX hosts refused");
        }

        row.Attempts++;
        row.LockedUntil = null;

        if (errors.Count == 0)
        {
            row.Status = OutboundStatus.Sent;
            row.SentAt = DateTimeOffset.UtcNow;
            row.LastError = null;
            _log.LogInformation("Outbound {Id} delivered to {Count} recipients", outboundId, delivered.Count);
        }
        else if (row.Attempts >= _options.MaxAttempts)
        {
            row.Status = OutboundStatus.Failed;
            row.LastError = string.Join(" | ", errors).Truncate(2000);
            _log.LogError("Outbound {Id} permanently failed after {Attempts} attempts: {Error}", outboundId, row.Attempts, row.LastError);
            if (_options.SendBounces)
                await TryCreateBounceAsync(services, row, errors, ct);
        }
        else
        {
            var delay = BackoffMinutes[Math.Min(row.Attempts - 1, BackoffMinutes.Length - 1)];
            row.Status = OutboundStatus.Deferred;
            row.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(delay);
            row.LastError = string.Join(" | ", errors).Truncate(2000);
            _log.LogWarning("Outbound {Id} deferred for {Minutes}m (attempt {Attempts})", outboundId, delay, row.Attempts);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task SendToHostAsync(string host, string envelopeFrom, IReadOnlyList<string> recipients, MimeMessage message, CancellationToken ct)
    {
        using var client = new SmtpClient { Timeout = _options.SmtpTimeoutSeconds * 1000 };
        var socketOptions = _options.RequireTls ? SecureSocketOptions.StartTls : SecureSocketOptions.StartTlsWhenAvailable;

        await client.ConnectAsync(host, 25, socketOptions, ct);
        try
        {
            await client.SendAsync(FormatOptions.Default, message, envelopeFrom, recipients, ct);
        }
        finally
        {
            if (client.IsConnected) await client.DisconnectAsync(true, ct);
        }
    }

    /// <summary>Drops an RFC 3464 style delivery status notification into the local sender's inbox.</summary>
    private async Task TryCreateBounceAsync(IServiceProvider services, OutboundMessage row, IReadOnlyList<string> errors, CancellationToken ct)
    {
        var db = services.GetRequiredService<OpenPostDbContext>();
        var store = services.GetRequiredService<IMessageStore>();

        var sender = await db.Mailboxes.FirstOrDefaultAsync(m => m.Address == row.EnvelopeFrom, ct);
        if (sender is null) return;

        var domain = Addresses.DomainPart(sender.Address);
        var bounce = new MimeMessage
        {
            Subject = $"Delivery failed: {row.Subject}".Truncate(300),
            Date = DateTimeOffset.UtcNow,
            MessageId = MimeUtils.GenerateMessageId(domain)
        };
        bounce.From.Add(new MailboxAddress("Mail Delivery System", $"postmaster@{domain}"));
        bounce.To.Add(new MailboxAddress(sender.DisplayName, sender.Address));

        var builder = new BodyBuilder
        {
            TextBody = $"""
                This is the mail system at {domain}.

                Your message could not be delivered to one or more recipients
                after {row.Attempts} attempts.

                Subject: {row.Subject}
                Recipients: {row.EnvelopeTo}

                Last error:
                {row.LastError}
                """
        };
        bounce.Body = builder.ToMessageBody();

        try
        {
            await store.StoreAsync(sender.Id, FolderKind.Inbox,
                bounce, new StoreOptions { IsRead = false, EnforceQuota = false }, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not store bounce for {Id}", row.Id);
        }
    }
}

internal static class StringExtensions
{
    public static string Truncate(this string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}
