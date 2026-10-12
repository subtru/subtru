using Npgsql;
using SubTru.Infrastructure.Persistence.Postgres;

namespace SubTru.Infrastructure.Persistence.Postgres.Subscriptions;

public sealed class PostgresSubscriptionReadRepository
    : ISubscriptionReadRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public PostgresSubscriptionReadRepository(NpgsqlConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<SubscriptionReadModel>> GetByUserAsync(
        Guid projectId,
        string environment,
        string externalUserId,
        CancellationToken ct)
    {
        const string sql = """
            select
              project_id,
              environment,
              external_user_id,
              provider,
              provider_subscription_id,
              product_id,
              price_id,
              status,
              provider_status,
              current_period_end,
              access_ends_at,
              cancel_at_period_end
            from subscriptions
            where project_id = @project_id
              and environment = @environment
              and external_user_id = @external_user_id
        """;

        await using var conn = _factory.Create();
        await conn.OpenAsync(ct);


        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("project_id", projectId);
        cmd.Parameters.AddWithValue("environment", environment);
        cmd.Parameters.AddWithValue("external_user_id", externalUserId);

        var results = new List<SubscriptionReadModel>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new SubscriptionReadModel(
                ProjectId: reader.GetGuid(0),
                Environment: reader.GetString(1),
                ExternalUserId: reader.GetString(2),
                Provider: reader.GetString(3),
                ProviderSubscriptionId: reader.GetString(4),
                ProductId: reader.GetString(5),
                PriceId: reader.IsDBNull(6) ? null : reader.GetString(6),
                Status: reader.GetString(7),
                ProviderStatus: reader.IsDBNull(8) ? null : reader.GetString(8),
                CurrentPeriodEnd: reader.GetFieldValue<DateTimeOffset>(9),
                AccessEndsAt: reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
                CancelAtPeriodEnd: reader.GetBoolean(11)
            ));
        }

        return results;
    }

    public async Task<string?> GetExternalUserIdByProviderSubscriptionIdAsync(
        Guid projectId,
        string environment,
        string provider,
        string providerSubscriptionId,
        CancellationToken ct)
    {
        const string sql = """
            select external_user_id
            from subscriptions
            where project_id = @project_id
              and environment = @environment
              and provider = @provider
              and provider_subscription_id = @provider_subscription_id
            limit 1
        """;

        await using var conn = _factory.Create();
        await conn.OpenAsync(ct);


        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("project_id", projectId);
        cmd.Parameters.AddWithValue("environment", environment);
        cmd.Parameters.AddWithValue("provider", provider);
        cmd.Parameters.AddWithValue("provider_subscription_id", providerSubscriptionId);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }
}
