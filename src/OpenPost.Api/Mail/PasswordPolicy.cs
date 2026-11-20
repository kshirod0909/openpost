using System.Security.Cryptography;
using System.Text;

namespace OpenPost.Api.Mail;

public static class PasswordPolicy
{
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Digits = "23456789";
    private const string Symbols = "!@#$%^&*-_=+?";

    public static string GenerateStrongPassword(int length = 20)
    {
        const string all = Upper + Lower + Digits + Symbols;
        var bytes = RandomNumberGenerator.GetBytes(length * 2);
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++)
            sb.Append(all[bytes[i] % all.Length]);
        return sb.ToString();
    }
}
