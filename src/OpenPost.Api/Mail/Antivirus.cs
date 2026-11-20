using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using MimeKit;
using OpenPost.Api.Security;

namespace OpenPost.Api.Mail;

public sealed record ScanResult(bool Clean, string? Signature);

public interface IAntivirusScanner
{
    Task<ScanResult> ScanAsync(MimeMessage message, CancellationToken ct);
}

/// <summary>No-op scanner used when antivirus is disabled.</summary>
public sealed class NullAntivirusScanner : IAntivirusScanner
{
    public Task<ScanResult> ScanAsync(MimeMessage message, CancellationToken ct) => Task.FromResult(new ScanResult(true, null));
}

/// <summary>ClamAV INSTREAM client. Enabled with Antivirus:Enabled=true.</summary>
public sealed class ClamAvScanner : IAntivirusScanner
{
    private readonly AntivirusOptions _options;
    private readonly ILogger<ClamAvScanner> _log;

    public ClamAvScanner(IOptions<AntivirusOptions> options, ILogger<ClamAvScanner> log)
    {
        _options = options.Value;
        _log = log;
    }

    public async Task<ScanResult> ScanAsync(MimeMessage message, CancellationToken ct)
    {
        if (!_options.Enabled) return new ScanResult(true, null);

        using var buffer = new MemoryStream();
        await message.WriteToAsync(buffer, ct);
        var payload = buffer.ToArray();

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_options.Host, _options.Port, ct);
            await using var stream = client.GetStream();

            await stream.WriteAsync("zINSTREAM\0"u8.ToArray(), ct);

            var lengthPrefix = new byte[4];
            for (var offset = 0; offset < payload.Length; offset += 8192)
            {
                var chunk = Math.Min(8192, payload.Length - offset);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, chunk);
                await stream.WriteAsync(lengthPrefix, ct);
                await stream.WriteAsync(payload.AsMemory(offset, chunk), ct);
            }
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, 0);
            await stream.WriteAsync(lengthPrefix, ct);
            await stream.FlushAsync(ct);

            var response = new StringBuilder();
            var buffer2 = new byte[1024];
            int read;
            while ((read = await stream.ReadAsync(buffer2, ct)) > 0)
            {
                response.Append(Encoding.ASCII.GetString(buffer2, 0, read));
                if (response.ToString().Contains('\0')) break;
            }

            var text = response.ToString().Trim('\0', '\r', '\n');
            if (text.Contains("FOUND", StringComparison.Ordinal))
            {
                var signature = text.Split(':').ElementAtOrDefault(1)?.Trim();
                _log.LogWarning("ClamAV detected {Signature}", signature);
                return new ScanResult(false, signature);
            }

            return new ScanResult(true, null);
        }
        catch (Exception ex)
        {
            // Fail open: a dead scanner must not silently stop mail delivery, but it must be visible.
            _log.LogError(ex, "ClamAV scan failed; message accepted unscanned");
            return new ScanResult(true, null);
        }
    }
}
