using System.Text;
using Npgsql;
using NpgsqlTypes;
using SubTru.Domain.Subscriptions;
using SubTru.Infrastructure.Security.Cryptography;
using SubTru.Infrastructure.Persistence.Postgres;

namespace SubTru.Infrastructure.Persistence.Postgres.ApiKeys;

public sealed class PostgresApiKeyManagementRepository : IApiKeyManagementRepository
{
    private const string LiveKeyPrefix = "subtru_live_";
    private const string TestKeyPrefix = "subtru_test_";

    private readonly NpgsqlConnectionFactory _factory;
    private readonly byte[] _pepper;

    public PostgresApiKeyManagementRepository(NpgsqlConnectionFactory factory, string pepper)
    {
        if (string.IsNullOrWhiteSpace(pepper))
        {
            throw new ArgumentException("API key pepper cannot be null or empty", nameof(pepper));
        }

        _factory = factory;
        _pepper = Encoding.UTF8.GetBytes(pepper);
    }

    public async Task<IReadOnlyList<ApiKeyListItem>> ListByProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                id,
                name,
                key_id,
                environment,
                created_at,
                last_used_at,
                expires_at,
                revoked_at
            FROM api_keys
            WHERE project_id = @project_id
            ORDER BY created_at DESC;
            """;

        await using var conn = _factory.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("project_id", projectId);

        var results = new List<ApiKeyListItem>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            DateTimeOffset? revokedAt = reader.IsDBNull(7)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(7);

            results.Add(new ApiKeyListItem(
                Id: reader.GetGuid(0),
                Name: reader.GetString(1),
                KeyId: reader.GetString(2),
                Environment: reader.GetString(3),
                CreatedAt: reader.GetFieldValue<DateTimeOffset>(4),
                LastUsedAt: reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                ExpiresAt: reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                IsRevoked: revokedAt.HasValue,
                RevokedAt: revokedAt
            ));
        }

        return results;
    }

    public async Task<CreatedApiKey> CreateAsync(
        Guid projectId,
        string name,
        string environment,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default)
    {
        const int maxAttempts = 5;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var normalizedEnvironment = SubscriptionEnvironment.NormalizeOrDefault(environment);
            var keyPrefix = normalizedEnvironment == SubscriptionEnvironment.Test
                ? TestKeyPrefix
                : LiveKeyPrefix;
            var keyId = KeyCrypto.CreateKeyId(keyPrefix);
            var secret = KeyCrypto.CreateSecret();
            var fullApiKey = KeyCrypto.ComposeFullKey(keyId, secret);
            var apiKeyHash = KeyCrypto.Hash(fullApiKey, _pepper);

            const string sql = """
                INSERT INTO api_keys (
                    project_id,
                    key_id,
                    api_key_hash,
                    name,
                    environment,
                    expires_at
                )
                VALUES (
                    @project_id,
                    @key_id,
                    @api_key_hash,
                    @name,
                    @environment,
                    @expires_at
                )
                RETURNING id, key_id, created_at;
                """;

            await using var conn = _factory.Create();
            await conn.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("project_id", projectId);
            cmd.Parameters.AddWithValue("key_id", keyId);
            cmd.Parameters.Add("api_key_hash", NpgsqlDbType.Bytea).Value = apiKeyHash;
            cmd.Parameters.AddWithValue("name", name);
            cmd.Parameters.AddWithValue("environment", normalizedEnvironment);
            cmd.Parameters.AddWithValue("expires_at", (object?)expiresAt ?? DBNull.Value);

            try
            {
                await using var reader = await cmd.ExecuteReaderAsync(ct);

                if (!await reader.ReadAsync(ct))
                {
                    throw new InvalidOperationException("API key insert did not return created row.");
                }

                return new CreatedApiKey(
                    Id: reader.GetGuid(0),
                    Name: name,
                    KeyId: reader.GetString(1),
                    ApiKey: fullApiKey,
                    Environment: normalizedEnvironment,
                    CreatedAt: reader.GetFieldValue<DateTimeOffset>(2)
                );
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && attempt < maxAttempts)
            {
                // Extremely unlikely key collision; retry with a fresh key.
            }
        }

        throw new InvalidOperationException("Could not generate a unique API key. Please retry.");
    }

    public async Task<RevokedApiKey?> RevokeAsync(Guid projectId, Guid apiKeyId, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE api_keys
            SET revoked_at = COALESCE(revoked_at, CURRENT_TIMESTAMP)
            WHERE id = @id
              AND project_id = @project_id
            RETURNING id, revoked_at;
            """;

        await using var conn = _factory.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", apiKeyId);
        cmd.Parameters.AddWithValue("project_id", projectId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        DateTimeOffset? revokedAt = reader.IsDBNull(1)
            ? null
            : reader.GetFieldValue<DateTimeOffset>(1);

        return new RevokedApiKey(
            Id: reader.GetGuid(0),
            IsRevoked: revokedAt.HasValue,
            RevokedAt: revokedAt
        );
    }
}
