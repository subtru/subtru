using System.Security.Cryptography;
using System.Text;

namespace SubTru.Infrastructure.Security.Cryptography;

internal static class KeyCrypto
{
    public static string CreateKeyId(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            throw new ArgumentException("Prefix cannot be null or empty.", nameof(prefix));

        return $"{prefix}{Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()}";
    }

    public static string CreateSecret()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    public static string ComposeFullKey(string keyId, string secret)
    {
        if (string.IsNullOrWhiteSpace(keyId))
            throw new ArgumentException("Key ID cannot be null or empty.", nameof(keyId));
        if (string.IsNullOrWhiteSpace(secret))
            throw new ArgumentException("Secret cannot be null or empty.", nameof(secret));
        if (keyId.Contains('.'))
            throw new ArgumentException("Key ID cannot contain '.'.", nameof(keyId));
        if (secret.Contains('.'))
            throw new ArgumentException("Secret cannot contain '.'.", nameof(secret));

        return $"{keyId}.{secret}";
    }

    public static byte[] Hash(string key, byte[] pepper)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(pepper);

        return HMACSHA256.HashData(pepper, Encoding.UTF8.GetBytes(key));
    }
}
