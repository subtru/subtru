using System.Security.Cryptography;
using System.Text;

namespace SubTru.Infrastructure.Security.Cryptography;

/// <summary>
/// AES-256-GCM authenticated encryption.
///
/// Output format:
///   [1 byte version=0x01] || [12 byte nonce] || [ciphertext] || [16 byte tag]
/// </summary>
public static class AesGcmEncryption
{
    private const byte CurrentVersion = 0x01;
    private const int VersionSize = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int MinCombinedLength = VersionSize + NonceSize + TagSize;

    /// <summary>
    /// Encrypts plaintext using AES-256-GCM.
    /// Returns: version || nonce || ciphertext || tag.
    /// </summary>
    public static byte[] Encrypt(string plaintext, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ValidateKey(key);

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var ciphertext = new byte[plaintextBytes.Length];
            var tag = new byte[TagSize];

            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

            var result = new byte[VersionSize + NonceSize + ciphertext.Length + TagSize];
            result[0] = CurrentVersion;
            Buffer.BlockCopy(nonce, 0, result, VersionSize, NonceSize);
            Buffer.BlockCopy(ciphertext, 0, result, VersionSize + NonceSize, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, result, VersionSize + NonceSize + ciphertext.Length, TagSize);

            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    /// <summary>
    /// Decrypts data produced by <see cref="Encrypt"/>.
    /// Input format: version || nonce || ciphertext || tag.
    /// </summary>
    public static string Decrypt(byte[] combined, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(combined);
        ValidateKey(key);

        if (combined.Length < MinCombinedLength)
            throw new ArgumentException(
                $"Ciphertext too short. Minimum length is {MinCombinedLength} bytes.", nameof(combined));

        if (combined[0] != CurrentVersion)
            throw new NotSupportedException($"Unsupported ciphertext version: 0x{combined[0]:X2}.");

        ReadOnlySpan<byte> span = combined;
        var nonce = span.Slice(VersionSize, NonceSize);
        var tag = span[^TagSize..];
        var ciphertext = span[(VersionSize + NonceSize)..^TagSize];

        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void ValidateKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeySize)
            throw new ArgumentException(
                $"Key must be {KeySize} bytes (256 bits). Actual: {key.Length} bytes.", nameof(key));
    }
}
