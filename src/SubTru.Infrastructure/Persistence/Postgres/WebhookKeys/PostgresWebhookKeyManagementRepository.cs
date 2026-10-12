using System.Text;
using Npgsql;
using NpgsqlTypes;
using SubTru.Infrastructure.Security.Cryptography;
using SubTru.Infrastructure.Persistence.Postgres;

namespace SubTru.Infrastructure.Persistence.Postgres.WebhookKeys;

public sealed class PostgresWebhookKeyManagementRepository : IWebhookKeyManagementRepository
{
    private const string KeyPrefix = "subtru_wh_";

    private readonly NpgsqlConnectionFactory _factory;
    private readonly byte[] _pepper;
    private readonly byte[] _encryptionKey;

    public PostgresWebhookKeyManagementRepository(
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
    }

    public async Task<IReadOnlyList<WebhookKeyListItem>> ListByProjectAsync(
        Guid projectId,
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                id,
                key_id,
                provider,
                environment,
                signing_secret_enc IS NOT NULL AS has_signing_secret,
                is_active,
                created_at,
                updated_at
            FROM webhook_keys
            WHERE project_id = @project_id
            ORDER BY created_at DESC;
            """;

        await using var conn = _factory.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("project_id", projectId);

        var results = new List<WebhookKeyListItem>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new WebhookKeyListItem(
                Id: reader.GetGuid(0),
                KeyId: reader.GetString(1),
                Provider: reader.GetString(2),
                Environment: reader.GetString(3),
                HasSigningSecret: reader.GetBoolean(4),
                IsActive: reader.GetBoolean(5),
                CreatedAt: reader.GetFieldValue<DateTimeOffset>(6),
                UpdatedAt: reader.GetFieldValue<DateTimeOffset>(7)
            ));
        }

        return results;
    }

    public async Task<CreatedWebhookKey> CreateAsync(
        Guid projectId,
        string provider,
        string environment,
        string? signingSecret,
        CancellationToken ct = default)
    {
        const int maxAttempts = 5;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var keyId = KeyCrypto.CreateKeyId(KeyPrefix);
            var secret = KeyCrypto.CreateSecret();
            var fullKey = KeyCrypto.ComposeFullKey(keyId, secret);
            var keyHash = KeyCrypto.Hash(fullKey, _pepper);

            byte[]? encryptedSecret = signingSecret is not null
                ? AesGcmEncryption.Encrypt(signingSecret, _encryptionKey)
                : null;

            await using var conn = _factory.Create();
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            const string deactivateSql = """
                UPDATE webhook_keys
                SET is_active = false
                WHERE project_id = @project_id
                  AND provider = @provider
                  AND environment = @environment
                  AND is_active = true;
                """;

            await using (var deactivateCmd = new NpgsqlCommand(deactivateSql, conn, tx))
            {
                deactivateCmd.Parameters.AddWithValue("project_id", projectId);
                deactivateCmd.Parameters.AddWithValue("provider", provider);
                deactivateCmd.Parameters.AddWithValue("environment", environment);
                await deactivateCmd.ExecuteNonQueryAsync(ct);
            }

            const string insertSql = """
                INSERT INTO webhook_keys (
                    project_id,
                    key_id,
                    webhook_key_hash,
                    provider,
                    environment,
                    signing_secret_enc
                )
                VALUES (
                    @project_id,
                    @key_id,
                    @webhook_key_hash,
                    @provider,
                    @environment,
                    @signing_secret_enc
                )
                RETURNING id, key_id, created_at;
                """;

            await using var cmd = new NpgsqlCommand(insertSql, conn, tx);
            cmd.Parameters.AddWithValue("project_id", projectId);
            cmd.Parameters.AddWithValue("key_id", keyId);
            cmd.Parameters.Add("webhook_key_hash", NpgsqlDbType.Bytea).Value = keyHash;
            cmd.Parameters.AddWithValue("provider", provider);
            cmd.Parameters.AddWithValue("environment", environment);
            cmd.Parameters.Add("signing_secret_enc", NpgsqlDbType.Bytea).Value =
                (object?)encryptedSecret ?? DBNull.Value;

            try
            {
                CreatedWebhookKey result;
                await using (var reader = await cmd.ExecuteReaderAsync(ct))
                {
                    if (!await reader.ReadAsync(ct))
                    {
                        throw new InvalidOperationException("Webhook key insert did not return created row.");
                    }

                    result = new CreatedWebhookKey(
                        Id: reader.GetGuid(0),
                        KeyId: reader.GetString(1),
                        WebhookKey: fullKey,
                        Provider: provider,
                        Environment: environment,
                        CreatedAt: reader.GetFieldValue<DateTimeOffset>(2)
                    );
                }

                await tx.CommitAsync(ct);
                return result;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && attempt < maxAttempts)
            {
                // Extremely unlikely key collision; transaction auto-rolls back on dispose.
            }
        }

        throw new InvalidOperationException("Could not generate a unique webhook key. Please retry.");
    }

    public async Task<DeactivatedWebhookKey?> DeactivateAsync(
        Guid projectId,
        Guid webhookKeyId,
        CancellationToken ct = default)
    {
        const string sql = """
            UPDATE webhook_keys
            SET is_active = false
            WHERE id = @id
              AND project_id = @project_id
              AND is_active = true
            RETURNING id, is_active, updated_at;
            """;

        await using var conn = _factory.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", webhookKeyId);
        cmd.Parameters.AddWithValue("project_id", projectId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new DeactivatedWebhookKey(
            Id: reader.GetGuid(0),
            IsActive: reader.GetBoolean(1),
            UpdatedAt: reader.GetFieldValue<DateTimeOffset>(2)
        );
    }

    public async Task<bool> UpdateSigningSecretAsync(
        Guid projectId,
        Guid webhookKeyId,
        string signingSecret,
        CancellationToken ct = default)
    {
        var encryptedSecret = AesGcmEncryption.Encrypt(signingSecret, _encryptionKey);

        const string sql = """
            UPDATE webhook_keys
            SET signing_secret_enc = @signing_secret_enc
            WHERE id = @id
              AND project_id = @project_id
              AND is_active = true;
            """;

        await using var conn = _factory.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", webhookKeyId);
        cmd.Parameters.AddWithValue("project_id", projectId);
        cmd.Parameters.Add("signing_secret_enc", NpgsqlDbType.Bytea).Value = encryptedSecret;

        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected > 0;
    }
}
