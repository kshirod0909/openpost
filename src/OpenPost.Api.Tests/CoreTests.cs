using FluentAssertions;
using OpenPost.Api.Common;
using OpenPost.Api.Mail;
using Xunit;

namespace OpenPost.Api.Tests;

public sealed class AddressTests
{
    [Theory]
    [InlineData("Alice", true)]
    [InlineData("alice.smith", true)]
    [InlineData("alice+tag", true)]
    [InlineData("a_b-c", true)]
    [InlineData("", false)]
    [InlineData(".alice", false)]
    [InlineData("alice.", false)]
    [InlineData("alice..smith", false)]
    [InlineData("alice smith", false)]
    [InlineData("alice@domain", false)]
    public void ValidatesLocalParts(string localPart, bool expected)
    {
        Addresses.IsValidLocalPart(localPart).Should().Be(expected);
    }

    [Theory]
    [InlineData("alice@example.org", "alice", "example.org")]
    [InlineData("a.b+c@sub.example.org", "a.b+c", "sub.example.org")]
    [InlineData("nodomain", "nodomain", "")]
    public void SplitsAddresses(string address, string local, string domain)
    {
        Addresses.LocalPart(address).Should().Be(local);
        Addresses.DomainPart(address).Should().Be(domain);
    }

    [Fact]
    public void NormalisesCase()
    {
        Addresses.Normalise("  Alice@Example.ORG ").Should().Be("alice@example.org");
    }
}

public sealed class DkimKeyTests
{
    [Fact]
    public void GeneratesImportableKeyPair()
    {
        var keys = DkimKeyGenerator.Create("openpost");

        keys.PrivateKeyPem.Should().Contain("BEGIN PRIVATE KEY");
        keys.PublicKeyBase64.Should().NotBeNullOrWhiteSpace();

        using var rsa = System.Security.Cryptography.RSA.Create();
        rsa.ImportFromPem(keys.PrivateKeyPem);

        var expected = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        keys.PublicKeyBase64.Should().Be(expected);

        DkimKeyGenerator.BuildDnsRecord(keys.PublicKeyBase64)
            .Should().StartWith("v=DKIM1; k=rsa; p=");
    }

    [Fact]
    public void ProducesDifferentKeysEachTime()
    {
        DkimKeyGenerator.Create("a").PrivateKeyPem
            .Should().NotBe(DkimKeyGenerator.Create("a").PrivateKeyPem);
    }
}

public sealed class PasswordPolicyTests
{
    [Fact]
    public void GeneratesLongMixedPassword()
    {
        var password = PasswordPolicy.GenerateStrongPassword(24);

        password.Should().HaveLength(24);
        password.Any(char.IsUpper).Should().BeTrue();
        password.Any(char.IsLower).Should().BeTrue();
        password.Any(char.IsDigit).Should().BeTrue();
    }

    [Fact]
    public void GeneratesUniquePasswords()
    {
        PasswordPolicy.GenerateStrongPassword().Should().NotBe(PasswordPolicy.GenerateStrongPassword());
    }
}

public sealed class PagingTests
{
    [Theory]
    [InlineData(0, 0, 1, 25)]
    [InlineData(-5, -10, 1, 25)]
    [InlineData(3, 50, 3, 50)]
    [InlineData(1, 5000, 1, Paging.MaxPageSize)]
    public void NormalisesPaging(int page, int size, int expectedPage, int expectedSize)
    {
        var (p, s) = Paging.Normalise(page, size);
        p.Should().Be(expectedPage);
        s.Should().Be(expectedSize);
    }
}

public sealed class PreviewTests
{
    [Fact]
    public void CollapsesWhitespaceAndTruncates()
    {
        var preview = MimeMapper.BuildPreview("Hello\r\n\r\n  world   again", 12);
        preview.Should().StartWith("Hello world");
        preview.Should().EndWith("\u2026");
    }

    [Fact]
    public void HandlesEmptyBodies()
    {
        MimeMapper.BuildPreview(null).Should().BeEmpty();
    }
}
