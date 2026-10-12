using System.Text;
using SubTru.Infrastructure.Security.Cryptography;

namespace SubTru.Tests.Infrastructure.Security.Cryptography;

public sealed class KeyCryptoTests
{
    [Fact]
    public void CreateKeyId_StartsWithPrefix()
    {
        var keyId = KeyCrypto.CreateKeyId("subtru_wh_");

        Assert.StartsWith("subtru_wh_", keyId);
    }

    [Fact]
    public void CreateKeyId_HasCorrectLength()
    {
        const string prefix = "subtru_wh_";
        var keyId = KeyCrypto.CreateKeyId(prefix);

        // prefix + 16 hex chars (8 random bytes)
        Assert.Equal(prefix.Length + 16, keyId.Length);
    }

    [Fact]
    public void CreateKeyId_HexSuffixIsLowercase()
    {
        var keyId = KeyCrypto.CreateKeyId("subtru_wh_");
        var hexPart = keyId["subtru_wh_".Length..];

        Assert.Matches("^[0-9a-f]{16}$", hexPart);
    }

    [Fact]
    public void CreateKeyId_GeneratesUniqueValues()
    {
        var ids = Enumerable.Range(0, 100)
            .Select(_ => KeyCrypto.CreateKeyId("subtru_wh_"))
            .ToHashSet();

        Assert.Equal(100, ids.Count);
    }

    [Fact]
    public void CreateSecret_HasCorrectLength()
    {
        var secret = KeyCrypto.CreateSecret();

        // 24 random bytes = 48 hex chars
        Assert.Equal(48, secret.Length);
    }

    [Fact]
    public void CreateSecret_IsLowercaseHex()
    {
        var secret = KeyCrypto.CreateSecret();

        Assert.Matches("^[0-9a-f]{48}$", secret);
    }

    [Fact]
    public void ComposeFullKey_JoinsWithDot()
    {
        var fullKey = KeyCrypto.ComposeFullKey("subtru_wh_abc123", "secret456");

        Assert.Equal("subtru_wh_abc123.secret456", fullKey);
    }

    [Fact]
    public void CreateKeyId_NullPrefix_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            KeyCrypto.CreateKeyId(null!));
    }

    [Fact]
    public void CreateKeyId_EmptyPrefix_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            KeyCrypto.CreateKeyId(""));
    }

    [Fact]
    public void ComposeFullKey_NullKeyId_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            KeyCrypto.ComposeFullKey(null!, "secret456"));
    }

    [Fact]
    public void ComposeFullKey_EmptySecret_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            KeyCrypto.ComposeFullKey("subtru_wh_abc123", ""));
    }

    [Fact]
    public void ComposeFullKey_KeyIdContainsDot_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            KeyCrypto.ComposeFullKey("subtru_wh_abc.123", "secret456"));
    }

    [Fact]
    public void ComposeFullKey_SecretContainsDot_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            KeyCrypto.ComposeFullKey("subtru_wh_abc123", "secret.456"));
    }

    [Fact]
    public void Hash_IsDeterministic()
    {
        var pepper = Encoding.UTF8.GetBytes("test-pepper");
        const string key = "subtru_wh_abc123.secret456";

        var hash1 = KeyCrypto.Hash(key, pepper);
        var hash2 = KeyCrypto.Hash(key, pepper);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void Hash_DifferentKeys_ProduceDifferentHashes()
    {
        var pepper = Encoding.UTF8.GetBytes("test-pepper");

        var hash1 = KeyCrypto.Hash("subtru_wh_key1.secret1", pepper);
        var hash2 = KeyCrypto.Hash("subtru_wh_key2.secret2", pepper);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void Hash_DifferentPeppers_ProduceDifferentHashes()
    {
        const string key = "subtru_wh_abc123.secret456";
        var pepper1 = Encoding.UTF8.GetBytes("pepper-one");
        var pepper2 = Encoding.UTF8.GetBytes("pepper-two");

        var hash1 = KeyCrypto.Hash(key, pepper1);
        var hash2 = KeyCrypto.Hash(key, pepper2);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void Hash_Returns32Bytes()
    {
        var pepper = Encoding.UTF8.GetBytes("test-pepper");
        var hash = KeyCrypto.Hash("subtru_wh_abc123.secret456", pepper);

        // HMAC-SHA256 produces 32 bytes
        Assert.Equal(32, hash.Length);
    }

    [Fact]
    public void Hash_NullKey_ThrowsArgumentNullException()
    {
        var pepper = Encoding.UTF8.GetBytes("test-pepper");

        Assert.Throws<ArgumentNullException>(() =>
            KeyCrypto.Hash(null!, pepper));
    }

    [Fact]
    public void Hash_NullPepper_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            KeyCrypto.Hash("subtru_wh_abc123.secret456", null!));
    }
}
