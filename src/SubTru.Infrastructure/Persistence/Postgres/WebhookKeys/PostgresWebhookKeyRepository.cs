using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using SubTru.Infrastructure.Security.Cryptography;
using SubTru.Infrastructure.Persistence.Postgres;

namespace SubTru.Infrastructure.Persistence.Postgres.WebhookKeys;

/// <summary>
/// Postgres-backed repository for webhook key lookups during ingestion.
/// Uses public key_id for indexed lookups, then constant-time hash comparison.
/// Follows pattern: subtru_wh_{key_id}.{secret}
/// </summary>
public sealed class PostgresWebhookKeyRepository : IWebhookKeyRepository
{
    private static readonly byte[] DummyMessage = Encoding.UTF8.GetBytes("dummy");

    private readonly NpgsqlConnectionFactory _factory;
    private readonly byte[] _pepper;
    private readonly byte[] _encryptionKey;
    private readonly byte[] _dummyHash;

    public PostgresWebhookKeyRepository(
        NpgsqlConnectionFactory factory,
        string pepper,
        string encryptionKey)
    {
        if (string.IsNullOrWhiteSpace(pepper))
            throw new ArgumentException("Pepper cannot be null or empty", nameof(pepper));
        if (string.IsNullOrWhiteSpace(encryptionKey))
            throw new ArgumentException("Encryption key cannot be null or empty", nameof(encryptionKey));

        _factory = factory;
        _pepper = Encoding.UTF8.GetBytes(pepper);
        _encryptionKey = Convert.FromHexString(encryptionKey);

        if (_encryptionKey.Length != 32)
            throw new ArgumentException("Encryption key must be 32 bytes (256 bits).", nameof(encryptionKey));

        _dummyHash = HMACSHA256.HashData(_pepper, DummyMessage);
    }

    public async Task<WebhookKeyInfo?> GetByKeyAsync(
        string webhookKey,
        string provider,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(webhookKey))
            return null;

        if (string.IsNullOrWhiteSpace(provider))
            return null;

        // Parse key_id from full key (everything before the dot)
        var dotIndex = webhookKey.IndexOf('.');
        if (dotIndex <= 0 || dotIndex == webhookKey.Length - 1)
            return null;

        var keyId = webhookKey[..dotIndex];
        var providedHash = KeyCrypto.Hash(webhookKey, _pepper);

        const string sql = """
            SELECT project_id, environment, webhook_key_hash, signing_secret_enc
            FROM webhook_keys
            WHERE key_id = @key_id
              AND provider = @provider
              AND is_active = true
            LIMIT 1;
            """;

        await using var conn = _factory.Create();
        await conn.OpenAsync(ct);

        Guid? projectId = null;
        string? environment = null;
        byte[] storedHash = _dummyHash;
        byte[]? encryptedSecret = null;

        await using (var cmd = new NpgsqlCommand(sql, conn))
        {
            cmd.Parameters.Add("key_id", NpgsqlDbType.Text).Value = keyId;
            cmd.Parameters.Add("provider", NpgsqlDbType.Text).Value = provider;

            await using var reader = await cmd.ExecuteReaderAsync(ct);

            if (await reader.ReadAsync(ct))
            {
                projectId = reader.GetGuid(0);
                environment = reader.GetString(1);
                storedHash = (byte[])reader.GetValue(2);
                encryptedSecret = reader.IsDBNull(3) ? null : (byte[])reader.GetValue(3);
            }
        }

        // Constant-time comparison to prevent timing attacks
        var isMatch = CryptographicOperations.FixedTimeEquals(providedHash, storedHash);
        if (!isMatch || projectId is null || environment is null)
        {
            return null;
        }

        string? signingSecret = encryptedSecret is not null
            ? AesGcmEncryption.Decrypt(encryptedSecret, _encryptionKey)
            : null;

        return new WebhookKeyInfo(projectId.Value, environment, signingSecret);
    }
}
