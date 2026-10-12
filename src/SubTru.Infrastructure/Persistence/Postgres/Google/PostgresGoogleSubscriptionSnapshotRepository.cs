using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using SubTru.Infrastructure.Security.Cryptography;
using SubTru.Infrastructure.Persistence.Postgres;

namespace SubTru.Infrastructure.Persistence.Postgres.Google;

public sealed class PostgresGoogleSubscriptionSnapshotRepository
    : IGoogleSubscriptionSnapshotRepository
{
    private readonly NpgsqlConnectionFactory _factory;
    private readonly byte[] _encryptionKey;

    public PostgresGoogleSubscriptionSnapshotRepository(
        NpgsqlConnectionFactory factory,
        string snapshotEncryptionKey)
    {
        _factory = factory;
        if (string.IsNullOrWhiteSpace(snapshotEncryptionKey))
            throw new ArgumentException(
                "Encryption key cannot be null or empty.",
                nameof(snapshotEncryptionKey));

        _encryptionKey = Convert.FromHexString(snapshotEncryptionKey);

        if (_encryptionKey.Length != 32)
        {
            CryptographicOperations.ZeroMemory(_encryptionKey);
            throw new ArgumentException(
                "Encryption key must be 32 bytes (256 bits).",
                nameof(snapshotEncryptionKey));
        }
    }

    public async Task SaveAsync(
        GoogleSubscriptionSnapshotWriteModel snapshot,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.Environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.EventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.PurchaseToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.RawResponseJson);
        if (snapshot.FetchSequence < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(snapshot),
                "Fetch sequence must be at least 1.");
        }

        var encryptedPayload = AesGcmEncryption.Encrypt(
            snapshot.RawResponseJson,
            _encryptionKey);
        var purchaseTokenFingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.PurchaseToken)))
            .ToLowerInvariant();

        const string sql = """
            insert into google_subscription_snapshots (
                project_id,
                environment,
                event_id,
                fetch_sequence,
                purchase_token_fingerprint,
                fetched_at,
                subscription_state,
                product_ids,
                response_enc
            )
            values (
                @project_id,
                @environment,
                @event_id,
                @fetch_sequence,
                @purchase_token_fingerprint,
                @fetched_at,
                @subscription_state,
                @product_ids,
                @response_enc
            );
            """;

        await using var conn = _factory.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter<Guid>("project_id", snapshot.ProjectId));
        cmd.Parameters.Add(new NpgsqlParameter("environment", NpgsqlDbType.Text)
        {
            Value = snapshot.Environment
        });
        cmd.Parameters.Add(new NpgsqlParameter("event_id", NpgsqlDbType.Text)
        {
            Value = snapshot.EventId
        });
        cmd.Parameters.Add(new NpgsqlParameter<int>("fetch_sequence", snapshot.FetchSequence));
        cmd.Parameters.Add(new NpgsqlParameter("purchase_token_fingerprint", NpgsqlDbType.Text)
        {
            Value = purchaseTokenFingerprint
        });
        cmd.Parameters.Add(new NpgsqlParameter("fetched_at", NpgsqlDbType.TimestampTz)
        {
            Value = snapshot.FetchedAt
        });
        cmd.Parameters.Add(new NpgsqlParameter("subscription_state", NpgsqlDbType.Text)
        {
            Value = (object?)snapshot.SubscriptionState ?? DBNull.Value
        });
        cmd.Parameters.Add(new NpgsqlParameter("product_ids", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = snapshot.ProductIds.ToArray()
        });
        cmd.Parameters.Add(new NpgsqlParameter("response_enc", NpgsqlDbType.Bytea)
        {
            Value = encryptedPayload
        });

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
