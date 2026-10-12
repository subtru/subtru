using Npgsql;
using NpgsqlTypes;
using SubTru.Infrastructure.Persistence.Postgres;

namespace SubTru.Infrastructure.Persistence.Postgres.Google;

public sealed class PostgresGooglePurchaseRemediationAuditRepository
    : IGooglePurchaseRemediationAuditRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public PostgresGooglePurchaseRemediationAuditRepository(NpgsqlConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task InsertAsync(
        GooglePurchaseRemediationAuditWriteModel writeModel,
        CancellationToken ct = default)
    {
        const string sql = """
            insert into google_purchase_remediations (
                project_id,
                environment,
                requested_purchase_token,
                effective_purchase_token,
                linked_purchase_token,
                external_user_id,
                actor,
                status,
                provider_status,
                current_period_end,
                acknowledged_at
            )
            values (
                @project_id,
                @environment,
                @requested_purchase_token,
                @effective_purchase_token,
                @linked_purchase_token,
                @external_user_id,
                @actor,
                @status,
                @provider_status,
                @current_period_end,
                @acknowledged_at
            );
            """;

        await using var conn = _factory.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter<Guid>("project_id", writeModel.ProjectId));
        cmd.Parameters.Add(new NpgsqlParameter("environment", NpgsqlDbType.Text) { Value = writeModel.Environment });
        cmd.Parameters.Add(new NpgsqlParameter("requested_purchase_token", NpgsqlDbType.Text) { Value = writeModel.RequestedPurchaseToken });
        cmd.Parameters.Add(new NpgsqlParameter("effective_purchase_token", NpgsqlDbType.Text) { Value = writeModel.EffectivePurchaseToken });
        cmd.Parameters.Add(new NpgsqlParameter("linked_purchase_token", NpgsqlDbType.Text) { Value = (object?)writeModel.LinkedPurchaseToken ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("external_user_id", NpgsqlDbType.Text) { Value = writeModel.ExternalUserId });
        cmd.Parameters.Add(new NpgsqlParameter("actor", NpgsqlDbType.Text) { Value = writeModel.Actor });
        cmd.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = writeModel.Status });
        cmd.Parameters.Add(new NpgsqlParameter("provider_status", NpgsqlDbType.Text) { Value = (object?)writeModel.ProviderStatus ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("current_period_end", NpgsqlDbType.TimestampTz) { Value = writeModel.CurrentPeriodEnd });
        cmd.Parameters.Add(new NpgsqlParameter("acknowledged_at", NpgsqlDbType.TimestampTz) { Value = writeModel.AcknowledgedAt });

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
