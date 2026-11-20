using System.Text.Json;
using MimeKit;
using OpenPost.Api.Contracts;
using OpenPost.Api.Data.Entities;

namespace OpenPost.Api.Mail;

/// <summary>Conversions between MimeKit messages and the stored representation.</summary>
public static class MimeMapper
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string ToJson(IEnumerable<InternetAddress> addresses)
    {
        var list = addresses.OfType<MailboxAddress>()
            .Select(a => new AddressDto(a.Address, string.IsNullOrWhiteSpace(a.Name) ? null : a.Name))
            .ToList();
        return JsonSerializer.Serialize(list);
    }

    public static string ToJson(IEnumerable<AddressDto> addresses) =>
        JsonSerializer.Serialize(addresses.ToList());

    public static IReadOnlyList<AddressDto> FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<AddressDto>();
        try { return JsonSerializer.Deserialize<List<AddressDto>>(json) ?? new List<AddressDto>(); }
        catch (JsonException) { return Array.Empty<AddressDto>(); }
    }

    public static string ToJson(IEnumerable<Guid> ids) => JsonSerializer.Serialize(ids.ToList());

    public static IReadOnlyList<Guid> GuidsFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<Guid>();
        try { return JsonSerializer.Deserialize<List<Guid>>(json) ?? new List<Guid>(); }
        catch (JsonException) { return Array.Empty<Guid>(); }
    }

    /// <summary>Copies the parseable parts of an inbound message onto a stored row.</summary>
    public static void Apply(StoredMessage target, MimeMessage mime, long sizeBytes)
    {
        target.Subject = Truncate(DecodeSubject(mime.Subject), 500);
        var from = mime.From.OfType<MailboxAddress>().FirstOrDefault();
        target.FromAddress = from?.Address?.ToLowerInvariant() ?? string.Empty;
        target.FromName = Truncate(from?.Name ?? string.Empty, 200);
        target.ToJson = ToJson(mime.To);
        target.CcJson = ToJson(mime.Cc);
        target.BccJson = ToJson(mime.Bcc);
        target.ReplyToJson = mime.ReplyTo.Count > 0 ? ToJson(mime.ReplyTo) : null;
        target.RfcMessageId = Truncate(mime.MessageId, 400);
        target.InReplyTo = Truncate(mime.InReplyTo, 400);
        target.References = Truncate(mime.References, 4000);
        target.ThreadId = ResolveThreadId(mime);
        target.SizeBytes = sizeBytes;

        target.BodyText = Truncate(mime.TextBody, 500_000);
        target.BodyHtml = Truncate(mime.HtmlBody, 1_000_000);
        target.Preview = BuildPreview(mime.TextBody ?? StripHtml(mime.HtmlBody));
        target.HasAttachments = mime.Attachments.Any();
        target.ReceivedAt = mime.Date == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : mime.Date;
    }

    private static string DecodeSubject(string? subject) =>
        string.IsNullOrWhiteSpace(subject) ? "(no subject)" : subject.Trim();

    private static string? ResolveThreadId(MimeMessage mime)
    {
        if (!string.IsNullOrWhiteSpace(mime.References))
        {
            var first = mime.References.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (first is not null) return first;
        }
        return !string.IsNullOrWhiteSpace(mime.InReplyTo) ? mime.InReplyTo : mime.MessageId;
    }

    public static string BuildPreview(string? body, int maxLength = 180)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        var text = System.Net.WebUtility.HtmlDecode(body)
            .Replace("\r", " ").Replace("\n", " ");
        while (text.Contains("  ")) text = text.Replace("  ", " ");
        text = text.Trim();
        return text.Length <= maxLength ? text : text[..maxLength] + "\u2026";
    }

    private static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
        return System.Net.WebUtility.HtmlDecode(text);
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];

    public static MessageSummaryDto ToSummary(StoredMessage m) => new(
        m.Id, m.FolderId, m.Folder?.Kind.ToString() ?? FolderKind.Custom.ToString(), m.Uid,
        m.Subject, m.FromName, m.FromAddress, FromJson(m.ToJson), m.Preview,
        m.IsRead, m.IsFlagged, m.IsDraft, m.HasAttachments, m.SizeBytes,
        Math.Round(m.SpamScore, 1), m.SpamVerdict.ToString(), m.ReceivedAt, m.ThreadId,
        GuidsFromJson(m.LabelsJson));

    public static MessageDetailDto ToDetail(StoredMessage m) => new(
        m.Id, m.FolderId, m.Folder?.Kind.ToString() ?? FolderKind.Custom.ToString(), m.Uid,
        m.Subject,
        new AddressDto(m.FromAddress, string.IsNullOrWhiteSpace(m.FromName) ? null : m.FromName),
        FromJson(m.ToJson), FromJson(m.CcJson), FromJson(m.BccJson), FromJson(m.ReplyToJson),
        m.BodyText, m.BodyHtml, m.RfcMessageId, m.InReplyTo, m.ThreadId,
        m.IsRead, m.IsFlagged, m.IsAnswered, m.IsDraft, m.SizeBytes,
        Math.Round(m.SpamScore, 1), m.SpamVerdict.ToString(),
        m.Attachments.Select(a => new AttachmentDto(a.Id, a.FileName, a.ContentType, a.SizeBytes, a.IsInline)).ToList(),
        GuidsFromJson(m.LabelsJson), m.ReceivedAt);
}
