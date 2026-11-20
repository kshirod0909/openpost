using Microsoft.Extensions.Options;
using OpenPost.Api.Security;

namespace OpenPost.Api.Mail;

public interface IMessageStorage
{
    /// <summary>Writes content to <paramref name="relativePath"/> and returns the path actually used.</summary>
    Task<string> WriteAsync(string relativePath, Stream content, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct = default);
    Task DeleteAsync(string relativePath, CancellationToken ct = default);
    Task<long> GetSizeAsync(string relativePath, CancellationToken ct = default);
    string ResolvePath(string relativePath);
}

/// <summary>
/// Filesystem storage for raw MIME, attachments and outbound payloads. Keeping blobs out of
/// PostgreSQL keeps the database small enough to back up quickly while remaining a single
/// volume to snapshot on the host.
/// </summary>
public sealed class LocalDiskStorage : IMessageStorage
{
    private readonly string _root;

    public LocalDiskStorage(IOptions<StorageOptions> options, ILogger<LocalDiskStorage> log)
    {
        _root = Path.GetFullPath(options.Value.RootPath);
        Directory.CreateDirectory(_root);
        log.LogInformation("Message storage root: {Root}", _root);
    }

    public async Task<string> WriteAsync(string relativePath, Stream content, CancellationToken ct = default)
    {
        var full = ResolvePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using var file = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true);
        await content.CopyToAsync(file, ct);
        return relativePath;
    }

    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct = default)
    {
        var full = ResolvePath(relativePath);
        if (!File.Exists(full)) throw new FileNotFoundException("Stored object not found.", relativePath);
        Stream stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string relativePath, CancellationToken ct = default)
    {
        var full = ResolvePath(relativePath);
        if (File.Exists(full)) File.Delete(full);
        return Task.CompletedTask;
    }

    public Task<long> GetSizeAsync(string relativePath, CancellationToken ct = default)
    {
        var info = new FileInfo(ResolvePath(relativePath));
        return Task.FromResult(info.Exists ? info.Length : 0L);
    }

    public string ResolvePath(string relativePath)
    {
        var combined = Path.GetFullPath(Path.Combine(_root, relativePath));
        var rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(rootWithSeparator, StringComparison.Ordinal) && combined != _root)
            throw new InvalidOperationException("Resolved path escapes the storage root.");
        return combined;
    }

    // ---- path helpers ----
    public static string RawPath(Guid mailboxId, Guid messageId, DateTimeOffset receivedAt) =>
        Path.Combine("raw", mailboxId.ToString("N"), receivedAt.ToString("yyyyMM"),
            messageId.ToString("N") + ".eml");

    public static string AttachmentPath(Guid mailboxId, Guid attachmentId, string fileName) =>
        Path.Combine("attachments", mailboxId.ToString("N"), attachmentId.ToString("N"),
            SanitiseFileName(fileName));

    public static string OutboundPath(Guid outboundId) =>
        Path.Combine("outbound", outboundId.ToString("N") + ".eml");

    public static string SanitiseFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (name.Length > 180) name = name[^180..];
        return string.IsNullOrWhiteSpace(name) ? "attachment" : name;
    }
}
