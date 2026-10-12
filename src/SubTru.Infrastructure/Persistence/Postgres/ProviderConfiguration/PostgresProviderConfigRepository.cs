using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SubTru.Infrastructure.Security.Cryptography;
using SubTru.Infrastructure.Persistence.Postgres;
using SubTru.Infrastructure.Providers.Google;

namespace SubTru.Infrastructure.Persistence.Postgres.ProviderConfiguration;

public sealed class PostgresProviderConfigRepository : IProviderConfigRepository
{
    private readonly NpgsqlConnectionFactory _factory;
    private readonly byte[] _encryptionKey;

    public PostgresProviderConfigRepository(
        NpgsqlConnectionFactory factory,
        string encryptionKey)
    {
        _factory = factory;
        if (string.IsNullOrWhiteSpace(encryptionKey))
            throw new ArgumentException("Encryption key cannot be null or empty.", nameof(encryptionKey));

        _encryptionKey = Convert.FromHexString(encryptionKey);
        if (_encryptionKey.Length != 32)
            throw new ArgumentException("Encryption key must be 32 bytes (256 bits).", nameof(encryptionKey));
    }

    public async Task<AppleProviderConfigReadModel?> GetAppleProviderConfigAsync(
        Guid projectId,
        CancellationToken ct = default)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(ct);
        return await GetAppleProviderConfigAsync(conn, transaction: null, projectId, ct);
    }

    public async Task<AppleProviderConfigReadModel> UpsertAppleProviderConfigAsync(
        Guid projectId,
        AppleProviderConfigWriteModel writeModel,
        CancellationToken ct = default)
    {
        var document = new AppleProviderConfigDocument(
            BundleId: writeModel.BundleId,
            AppAppleId: writeModel.AppAppleId
        );

        var json = JsonSerializer.Serialize(document);

        const string sql = """
            INSERT INTO provider_configs (
                project_id,
                provider,
                config_json
            )
            VALUES (
                @project_id,
                'apple',
                @config_json
            )
            ON CONFLICT (project_id, provider)
            DO UPDATE SET
                config_json = excluded.config_json,
                updated_at = now()
            RETURNING created_at, updated_at;
            """;

        await using var conn = _factory.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("project_id", projectId);
        cmd.Parameters.Add("config_json", NpgsqlDbType.Jsonb).Value = json;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException("Apple provider config upsert did not return timestamps.");
        }

        return new AppleProviderConfigReadModel(
            BundleId: writeModel.BundleId,
            AppAppleId: writeModel.AppAppleId,
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(0),
            UpdatedAt: reader.GetFieldValue<DateTimeOffset>(1)
        );
    }

    public async Task<GoogleProviderConfigReadModel?> GetGoogleProviderConfigAsync(
        Guid projectId,
        CancellationToken ct = default)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(ct);
        return await GetGoogleProviderConfigAsync(conn, transaction: null, projectId, ct);
    }

    public async Task<GoogleProviderCredentialReadModel?> GetGoogleProviderCredentialAsync(
        Guid projectId,
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                credential_enc,
                client_email,
                private_key_id,
                created_at,
                last_validated_at
            FROM provider_credentials
            WHERE project_id = @project_id
              AND provider = 'google'
              AND is_active = true
            LIMIT 1;
            """;

        await using var conn = _factory.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("project_id", projectId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var encryptedCredential = (byte[])reader.GetValue(0);
        var serviceAccountJson = AesGcmEncryption.Decrypt(encryptedCredential, _encryptionKey);

        return new GoogleProviderCredentialReadModel(
            ServiceAccountJson: serviceAccountJson,
            ClientEmail: reader.GetString(1),
            PrivateKeyId: reader.IsDBNull(2) ? null : reader.GetString(2),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(3),
            LastValidatedAt: reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4)
        );
    }

    public async Task<GoogleProviderConfigReadModel> UpsertGoogleProviderConfigAsync(
        Guid projectId,
        GoogleProviderConfigWriteModel writeModel,
        ValidatedGoogleServiceAccount? validatedServiceAccount,
        CancellationToken ct = default)
    {
        var document = new GoogleProviderConfigDocument(
            PackageName: writeModel.PackageName,
            PubSubAuthServiceAccountEmail: writeModel.PubSubAuthServiceAccountEmail
        );

        var json = JsonSerializer.Serialize(document);

        const string upsertConfigSql = """
            INSERT INTO provider_configs (
                project_id,
                provider,
                config_json
            )
            VALUES (
                @project_id,
                'google',
                @config_json
            )
            ON CONFLICT (project_id, provider)
            DO UPDATE SET
                config_json = excluded.config_json,
                updated_at = now();
            """;

        const string deactivateActiveCredentialSql = """
            UPDATE provider_credentials
            SET is_active = false,
                rotated_at = now()
            WHERE project_id = @project_id
              AND provider = 'google'
              AND is_active = true;
            """;

        const string insertCredentialSql = """
            INSERT INTO provider_credentials (
                project_id,
                provider,
                credential_type,
                credential_enc,
                client_email,
                private_key_id,
                last_validated_at
            )
            VALUES (
                @project_id,
                'google',
                'google_service_account_json',
                @credential_enc,
                @client_email,
                @private_key_id,
                @last_validated_at
            );
            """;

        await using var conn = _factory.Create();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var cmd = new NpgsqlCommand(upsertConfigSql, conn, tx))
        {
            cmd.Parameters.AddWithValue("project_id", projectId);
            cmd.Parameters.Add("config_json", NpgsqlDbType.Jsonb).Value = json;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!string.IsNullOrWhiteSpace(writeModel.ServiceAccountJson))
        {
            ArgumentNullException.ThrowIfNull(validatedServiceAccount);

            var encryptedCredential = AesGcmEncryption.Encrypt(writeModel.ServiceAccountJson, _encryptionKey);

            await using (var deactivateCmd = new NpgsqlCommand(deactivateActiveCredentialSql, conn, tx))
            {
                deactivateCmd.Parameters.AddWithValue("project_id", projectId);
                await deactivateCmd.ExecuteNonQueryAsync(ct);
            }

            await using var credentialCmd = new NpgsqlCommand(insertCredentialSql, conn, tx);
            credentialCmd.Parameters.AddWithValue("project_id", projectId);
            credentialCmd.Parameters.Add("credential_enc", NpgsqlDbType.Bytea).Value = encryptedCredential;
            credentialCmd.Parameters.AddWithValue("client_email", validatedServiceAccount.ClientEmail);
            credentialCmd.Parameters.AddWithValue("private_key_id", (object?)validatedServiceAccount.PrivateKeyId ?? DBNull.Value);
            credentialCmd.Parameters.AddWithValue("last_validated_at", (object?)validatedServiceAccount.ValidatedAt ?? DBNull.Value);
            await credentialCmd.ExecuteNonQueryAsync(ct);
        }

        var saved = await GetGoogleProviderConfigAsync(conn, tx, projectId, ct)
            ?? throw new InvalidOperationException("Google provider config upsert did not return a row.");

        await tx.CommitAsync(ct);
        return saved;
    }

    private static async Task<AppleProviderConfigReadModel?> GetAppleProviderConfigAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? transaction,
        Guid projectId,
        CancellationToken ct)
    {
        const string sql = """
            SELECT config_json::text, created_at, updated_at
            FROM provider_configs
            WHERE project_id = @project_id
              AND provider = 'apple'
            LIMIT 1;
            """;

        await using var cmd = new NpgsqlCommand(sql, conn, transaction);
        cmd.Parameters.AddWithValue("project_id", projectId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var json = reader.GetString(0);
        var document = DeserializeAppleDocument(json);

        return new AppleProviderConfigReadModel(
            BundleId: document.BundleId,
            AppAppleId: document.AppAppleId,
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(1),
            UpdatedAt: reader.GetFieldValue<DateTimeOffset>(2)
        );
    }

    private static async Task<GoogleProviderConfigReadModel?> GetGoogleProviderConfigAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? transaction,
        Guid projectId,
        CancellationToken ct)
    {
        const string sql = """
            SELECT
                c.config_json::text,
                c.created_at,
                c.updated_at,
                cred.client_email,
                cred.private_key_id,
                cred.created_at,
                cred.last_validated_at
            FROM provider_configs c
            LEFT JOIN provider_credentials cred
              ON cred.project_id = c.project_id
             AND cred.provider = 'google'
             AND cred.is_active = true
            WHERE c.project_id = @project_id
              AND c.provider = 'google'
            LIMIT 1;
            """;

        await using var cmd = new NpgsqlCommand(sql, conn, transaction);
        cmd.Parameters.AddWithValue("project_id", projectId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var json = reader.GetString(0);
        var document = DeserializeGoogleDocument(json);

        return new GoogleProviderConfigReadModel(
            PackageName: document.PackageName,
            PubSubAuthServiceAccountEmail: document.PubSubAuthServiceAccountEmail,
            HasServiceAccountCredentials: !reader.IsDBNull(3),
            ServiceAccountClientEmail: reader.IsDBNull(3) ? null : reader.GetString(3),
            ServiceAccountKeyId: reader.IsDBNull(4) ? null : reader.GetString(4),
            ServiceAccountCreatedAt: reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
            ServiceAccountLastValidatedAt: reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(1),
            UpdatedAt: reader.GetFieldValue<DateTimeOffset>(2)
        );
    }

    private static AppleProviderConfigDocument DeserializeAppleDocument(string json)
    {
        var document = JsonSerializer.Deserialize<AppleProviderConfigDocument>(json);
        if (document is null
            || string.IsNullOrWhiteSpace(document.BundleId)
            || document.AppAppleId <= 0)
        {
            throw new InvalidOperationException("Stored Apple provider config is invalid.");
        }

        return document;
    }

    private static GoogleProviderConfigDocument DeserializeGoogleDocument(string json)
    {
        var document = JsonSerializer.Deserialize<GoogleProviderConfigDocument>(json);
        if (document is null
            || string.IsNullOrWhiteSpace(document.PackageName)
            || string.IsNullOrWhiteSpace(document.PubSubAuthServiceAccountEmail))
        {
            throw new InvalidOperationException("Stored Google provider config is invalid.");
        }

        return document;
    }

    private sealed record AppleProviderConfigDocument(
        string BundleId,
        long AppAppleId
    );

    private sealed record GoogleProviderConfigDocument(
        string PackageName,
        string PubSubAuthServiceAccountEmail
    );
}
