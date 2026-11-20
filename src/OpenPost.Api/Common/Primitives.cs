namespace OpenPost.Api.Common;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasNext => (long)Page * PageSize < TotalCount;
}

public sealed class NotFoundException(string message) : Exception(message);
public sealed class ForbiddenException(string message) : Exception(message);
public sealed class ConflictException(string message) : Exception(message);
public sealed class ValidationFailedException(string message) : Exception(message);

public static class Paging
{
    public const int MaxPageSize = 200;

    public static (int Page, int Size) Normalise(int page, int size)
    {
        if (page < 1) page = 1;
        if (size < 1) size = 25;
        if (size > MaxPageSize) size = MaxPageSize;
        return (page, size);
    }
}

public static class Addresses
{
    /// <summary>Lower-cased, trimmed address used for storage and lookups.</summary>
    public static string Normalise(string address) => address.Trim().ToLowerInvariant();

    public static string LocalPart(string address)
    {
        var at = address.LastIndexOf('@');
        return at <= 0 ? address : address[..at];
    }

    public static string DomainPart(string address)
    {
        var at = address.LastIndexOf('@');
        return at < 0 || at == address.Length - 1 ? string.Empty : address[(at + 1)..];
    }

    public static bool LooksValid(string address) =>
        address.Contains('@') && !address.StartsWith('@') && !address.EndsWith('@') && address.Length <= 320;

    /// <summary>Validates the local part against a conservative RFC 5322 dot-atom subset.</summary>
    public static bool IsValidLocalPart(string localPart)
    {
        if (string.IsNullOrWhiteSpace(localPart) || localPart.Length > 64) return false;
        const string allowed = "abcdefghijklmnopqrstuvwxyz0123456789._-+";
        var lower = localPart.ToLowerInvariant();
        if (lower.StartsWith('.') || lower.EndsWith('.') || lower.Contains("..")) return false;
        return lower.All(c => allowed.Contains(c));
    }
}
