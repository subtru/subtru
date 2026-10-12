using System.Security.Cryptography;
using SubTru.Infrastructure.Security.Cryptography;

namespace SubTru.Tests.Infrastructure.Security.Cryptography;

public sealed class AesGcmEncryptionTests
{
    private static byte[] CreateValidKey() => RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void EncryptDecrypt_RoundTrip_ReturnsOriginalPlaintext()
    {
        var key = CreateValidKey();
        const string plaintext = "whsec_test_signing_secret_123";

        var encrypted = AesGcmEncryption.Encrypt(plaintext, key);
        var decrypted = AesGcmEncryption.Decrypt(encrypted, key);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void EncryptDecrypt_EmptyString_RoundTrips()
    {
        var key = CreateValidKey();
        const string plaintext = "";

        var encrypted = AesGcmEncryption.Encrypt(plaintext, key);
        var decrypted = AesGcmEncryption.Decrypt(encrypted, key);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void EncryptDecrypt_UnicodeString_RoundTrips()
    {
        var key = CreateValidKey();
        const string plaintext = "wh_secret_🔑_tëst";

        var encrypted = AesGcmEncryption.Encrypt(plaintext, key);
        var decrypted = AesGcmEncryption.Decrypt(encrypted, key);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Encrypt_OutputStartsWithVersionByte()
    {
        var key = CreateValidKey();
        var encrypted = AesGcmEncryption.Encrypt("test", key);

        Assert.Equal(0x01, encrypted[0]);
    }

    [Fact]
    public void Encrypt_OutputHasCorrectMinimumLength()
    {
        var key = CreateValidKey();
        var encrypted = AesGcmEncryption.Encrypt("test", key);

        // version (1) + nonce (12) + ciphertext (>= plaintext length) + tag (16)
        Assert.True(encrypted.Length >= 1 + 12 + 4 + 16);
    }

    [Fact]
    public void Encrypt_SamePlaintext_ProducesDifferentCiphertext()
    {
        var key = CreateValidKey();
        const string plaintext = "whsec_deterministic_test";

        var encrypted1 = AesGcmEncryption.Encrypt(plaintext, key);
        var encrypted2 = AesGcmEncryption.Encrypt(plaintext, key);

        Assert.NotEqual(encrypted1, encrypted2);
    }

    [Fact]
    public void Decrypt_WrongKey_Throws()
    {
        var key1 = CreateValidKey();
        var key2 = CreateValidKey();

        var encrypted = AesGcmEncryption.Encrypt("secret", key1);

        Assert.ThrowsAny<CryptographicException>(() =>
            AesGcmEncryption.Decrypt(encrypted, key2));
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_Throws()
    {
        var key = CreateValidKey();
        var encrypted = AesGcmEncryption.Encrypt("secret", key);

        // Flip a byte in the ciphertext portion (after version + nonce)
        encrypted[14] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() =>
            AesGcmEncryption.Decrypt(encrypted, key));
    }

    [Fact]
    public void Decrypt_InvalidVersion_ThrowsNotSupported()
    {
        var key = CreateValidKey();
        var encrypted = AesGcmEncryption.Encrypt("secret", key);

        encrypted[0] = 0xFF;

        Assert.Throws<NotSupportedException>(() =>
            AesGcmEncryption.Decrypt(encrypted, key));
    }

    [Fact]
    public void Decrypt_TooShortInput_ThrowsArgumentException()
    {
        var key = CreateValidKey();
        var tooShort = new byte[10];

        Assert.Throws<ArgumentException>(() =>
            AesGcmEncryption.Decrypt(tooShort, key));
    }

    [Fact]
    public void Encrypt_NullPlaintext_ThrowsArgumentNullException()
    {
        var key = CreateValidKey();

        Assert.Throws<ArgumentNullException>(() =>
            AesGcmEncryption.Encrypt(null!, key));
    }

    [Fact]
    public void Encrypt_NullKey_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            AesGcmEncryption.Encrypt("test", null!));
    }

    [Fact]
    public void Decrypt_NullCombined_ThrowsArgumentNullException()
    {
        var key = CreateValidKey();

        Assert.Throws<ArgumentNullException>(() =>
            AesGcmEncryption.Decrypt(null!, key));
    }

    [Fact]
    public void Encrypt_WrongKeySize_ThrowsArgumentException()
    {
        var shortKey = new byte[16];

        Assert.Throws<ArgumentException>(() =>
            AesGcmEncryption.Encrypt("test", shortKey));
    }

    [Fact]
    public void Decrypt_WrongKeySize_ThrowsArgumentException()
    {
        var key = CreateValidKey();
        var encrypted = AesGcmEncryption.Encrypt("test", key);

        var shortKey = new byte[16];

        Assert.Throws<ArgumentException>(() =>
            AesGcmEncryption.Decrypt(encrypted, shortKey));
    }
}
