using MimeKit;
using OpenPost.Api.Contracts;
using OpenPost.Api.Data.Entities;

namespace OpenPost.Api.Mail;

/// <summary>Builds RFC 5322 messages from compose requests.</summary>
public sealed class MimeFactory
{
    private readonly IMessageStorage _storage;

    public MimeFactory(IMessageStorage storage) => _storage = storage;

    public async Task<MimeMessage> BuildAsync(Mailbox sender, SendMessageRequest request, IReadOnlyList<Attachment> attachments, CancellationToken ct)
    {
        var domain = Common.Addresses.DomainPart(sender.Address);
        var message = new MimeMessage
        {
            Subject = request.Subject ?? string.Empty,
            Date = DateTimeOffset.UtcNow,
            MessageId = MimeUtils.GenerateMessageId(domain)
        };

        message.From.Add(new MailboxAddress(sender.DisplayName, sender.Address));

        foreach (var to in request.To) message.To.Add(Parse(to));
        foreach (var cc in request.Cc ?? Array.Empty<string>()) message.Cc.Add(Parse(cc));
        foreach (var bcc in request.Bcc ?? Array.Empty<string>()) message.Bcc.Add(Parse(bcc));

        if (!string.IsNullOrWhiteSpace(request.InReplyTo))
        {
            message.InReplyTo = request.InReplyTo;
            message.References = string.IsNullOrWhiteSpace(request.References)
                ? request.InReplyTo
                : request.References + " " + request.InReplyTo;
        }

        message.Headers.Add("X-Mailer", "OpenPost");

        var builder = new BodyBuilder
        {
            TextBody = request.TextBody ?? HtmlToText(request.HtmlBody),
            HtmlBody = request.HtmlBody
        };

        foreach (var attachment in attachments)
        {
            var path = _storage.ResolvePath(attachment.StoragePath);
            if (!File.Exists(path)) continue;

            var contentType = ContentType.Parse(string.IsNullOrWhiteSpace(attachment.ContentType)
                ? "application/octet-stream"
                : attachment.ContentType);

            await using var stream = File.OpenRead(path);
            builder.Attachments.Add(attachment.FileName, stream, contentType);
        }

        message.Body = builder.ToMessageBody();
        return message;
    }

    public static MailboxAddress Parse(string address)
    {
        var trimmed = address.Trim();
        if (MailboxAddress.TryParse(trimmed, out var parsed)) return parsed;
        return new MailboxAddress(string.Empty, trimmed);
    }

    public static string HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var text = System.Text.RegularExpressions.Regex.Replace(html, "<br\\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "</p>", "\n\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", string.Empty);
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }
}
