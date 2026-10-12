using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using SubTru.Infrastructure.Security.Cryptography;
using SubTru.Infrastructure.Persistence.Postgres;

namespace SubTru.Infrastructure.Persistence.Postgres.Authentication;

/// <summary>
/// Postgres-backed API key validation and project resolution.
/// Uses public key_id for indexed lookups, then constant-time hash comparison.
/// Follows pattern: subtru_live_{key_id}.{secret} or subtru_test_{key_id}.{secret}
/// </summary>
public sealed class PostgresProjectAuthService : IProjectAuthService
{
    private static readonly byte[] DummyMessage = Encoding.UTF8.GetBytes("dummy");

    private readonly NpgsqlConnectionFactory _factory;
    private readonly byte[] _pepper;
    private readonly byte[] _dummyHash;

    public PostgresProjectAuthService(NpgsqlConnectionFactory factory, string pepper)
    {
        if (string.IsNullOrWhiteSpace(pepper))
            throw new ArgumentException("API key pepper cannot be null or empty", nameof(pepper));

        _factory = factory;
        _pepper = Encoding.UTF8.GetBytes(pepper);
        _dummyHash = HMACSHA256.HashData(_pepper, DummyMessage);
    }

    public async Task<ValidatedApiKey?> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        var dotIndex = apiKey.IndexOf('.');
        if (dotIndex <= 0 || dotIndex == apiKey.Length - 1)
            return null;

        var keyId = apiKey[..dotIndex];
        var providedHash = KeyCrypto.Hash(apiKey, _pepper);

        const string selectSql = """
            SELECT id, project_id, environment, api_key_hash
            FROM api_keys
            WHERE key_id = @keyId
              AND revoked_at IS NULL
              AND (expires_at IS NULL OR expires_at > now())
            LIMIT 1;
            """;

        const string touchSql = """
            UPDATE api_keys
            SET last_used_at = CURRENT_TIMESTAMP
            WHERE id = @id;
            """;

        await using var conn = _factory.Create();
        await conn.OpenAsync(ct);

        Guid? rowId = null;
        Guid? projectId = null;
        string? environment = null;
        byte[] storedHash = _dummyHash;

        await using (var cmd = new NpgsqlCommand(selectSql, conn))
        {
            cmd.Parameters.Add("keyId", NpgsqlDbType.Text).Value = keyId;

            await using var reader = await cmd.ExecuteReaderAsync(ct);

            if (await reader.ReadAsync(ct))
            {
                rowId = reader.GetGuid(0);
                projectId = reader.GetGuid(1);
                environment = reader.GetString(2);
                storedHash = (byte[])reader.GetValue(3);
            }
        }

        var isMatch = CryptographicOperations.FixedTimeEquals(providedHash, storedHash);
        if (!isMatch || projectId is null || rowId is null || environment is null)
        {
            return null;
        }

        await using (var touchCmd = new NpgsqlCommand(touchSql, conn))
        {
            touchCmd.Parameters.Add("id", NpgsqlDbType.Uuid).Value = rowId.Value;
            await touchCmd.ExecuteNonQueryAsync(ct);
        }

        return new ValidatedApiKey(projectId.Value, environment);
    }

}
