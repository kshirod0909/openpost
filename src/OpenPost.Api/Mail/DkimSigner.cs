using Microsoft.EntityFrameworkCore;
using MimeKit;
using MimeKit.Cryptography;
using OpenPost.Api.Data;
using OpenPost.Api.Data.Entities;
using System.Security.Cryptography;

namespace OpenPost.Api.Mail;

public interface IDkimSigner
{
    /// <summary>Returns a DKIM-signed copy when the sending domain has a key, otherwise the message unchanged.</summary>
    Task<MimeMessage> SignAsync(MimeMessage message, string envelopeFrom, CancellationToken ct);
}

public sealed class DkimSigningService : IDkimSigner
{
    private static readonly HeaderId[] SignedHeaders =
    {
        HeaderId.From, HeaderId.To, HeaderId.Cc, HeaderId.Subject, HeaderId.Date,
        HeaderId.MessageId, HeaderId.MimeVersion, HeaderId.ReplyTo, HeaderId.InReplyTo, HeaderId.References
    };

    private readonly OpenPostDbContext _db;
    private readonly ILogger<DkimSigner> _log;

    public DkimSigningService(OpenPostDbContext db, ILogger<DkimSigningService> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<MimeMessage> SignAsync(MimeMessage message, string envelopeFrom, CancellationToken ct)
    {
        var domainName = Common.Addresses.DomainPart(envelopeFrom);
        var domain = await _db.Domains.FirstOrDefaultAsync(d => d.Name == domainName && d.IsActive, ct);

        if (domain?.DkimPrivateKeyPem is null)
        {
            _log.LogWarning("No DKIM key for {Domain}; sending unsigned", domainName);
            return message;
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(domain.DkimPrivateKeyPem);

            var signer = new MimeKit.Cryptography.DkimSigner(rsa, domain.Name, domain.DkimSelector, DkimSignatureAlgorithm.RsaSha256)
            {
                HeaderCanonicalization = DkimCanonicalizationAlgorithm.Relaxed,
                BodyCanonicalization = DkimCanonicalizationAlgorithm.Relaxed
            };

            return signer.Sign(message, SignedHeaders);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "DKIM signing failed for {Domain}; sending unsigned", domainName);
            return message;
        }
    }
}
