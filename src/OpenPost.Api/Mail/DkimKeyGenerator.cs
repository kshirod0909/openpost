namespace OpenPost.Api.Mail;

/// <summary>
/// Generates and rotates DKIM key pairs (RSA 2048, relaxed/relaxed, rsa-sha256).
/// </summary>
public static class DkimKeyGenerator
{
    public sealed record DkimKeys(string PrivateKeyPem, string PublicKeyBase64);

    public static DkimKeys Create(string selector, int keySize = 2048)
    {
        using var rsa = RSA.Create(keySize);
        var privateKeyPem = rsa.ExportPkcs8PrivateKeyPem();
        var publicKeyBase64 = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        return new DkimKeys(privateKeyPem, publicKeyBase64);
    }

    /// <summary>Builds the DNS TXT value that must be published at &lt;selector&gt;._domainkey.&lt;domain&gt;.</summary>
    public static string BuildDnsRecord(string publicKeyBase64) =>
        $"v=DKIM1; k=rsa; p={publicKeyBase64}";
}
