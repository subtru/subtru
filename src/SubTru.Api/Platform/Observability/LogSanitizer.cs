using System.Security.Cryptography;
using System.Text;

namespace SubTru.Api.Platform.Observability;

public static class LogSanitizer
{
    private const int FingerprintByteLength = 6;

    public static string Fingerprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "missing";
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return Convert.ToHexString(bytes.AsSpan(0, FingerprintByteLength)).ToLowerInvariant();
    }
}
