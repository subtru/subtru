using Npgsql;
using SubTru.Infrastructure.Persistence.Postgres;

namespace SubTru.Infrastructure.Persistence.Postgres.Projects;

public sealed class PostgresProjectSummaryRepository : IProjectSummaryRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public PostgresProjectSummaryRepository(NpgsqlConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<ProjectSummaryReadModel> GetSummaryAsync(
        Guid projectId,
        string environment,
        CancellationToken ct)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(ct);

        var (usersWithAccess, activeSubscriptions, expiredSubscriptions) =
            await GetSubscriptionSummaryAsync(conn, projectId, environment, ct);

        var lastWebhookReceivedAt = await GetLastWebhookReceivedAtAsync(conn, projectId, environment, ct);

        return new ProjectSummaryReadModel(
            UsersWithAccess: usersWithAccess,
            ActiveSubscriptions: activeSubscriptions,
            ExpiredSubscriptions: expiredSubscriptions,
            LastWebhookReceivedAt: lastWebhookReceivedAt
        );
    }

    private static async Task<(int UsersWithAccess, int ActiveSubscriptions, int ExpiredSubscriptions)> GetSubscriptionSummaryAsync(
        NpgsqlConnection conn,
        Guid projectId,
        string environment,
        CancellationToken ct)
    {
        const string sql = """
            select
                coalesce(count(distinct external_user_id) filter (
                    where (
                        lower(status) in ('active', 'trialing', 'canceled')
                        and access_ends_at > now()
                    ) or (
                        lower(status) = 'billing_grace'
                        and (access_ends_at is null or access_ends_at > now())
                    )
                ), 0) as users_with_access,
                coalesce(count(*) filter (
                    where (
                        lower(status) in ('active', 'trialing', 'canceled')
                        and access_ends_at > now()
                    ) or (
                        lower(status) = 'billing_grace'
                        and (access_ends_at is null or access_ends_at > now())
                    )
                ), 0) as active_subscriptions,
                coalesce(count(*) filter (
                    where not (
                        (
                            lower(status) in ('active', 'trialing', 'canceled')
                            and access_ends_at > now()
                        ) or (
                            lower(status) = 'billing_grace'
                            and (access_ends_at is null or access_ends_at > now())
                        )
                    )
                ), 0) as expired_subscriptions
            from subscriptions
            where project_id = @project_id
              and environment = @environment;
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("project_id", projectId);
        cmd.Parameters.AddWithValue("environment", environment);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        return (
            UsersWithAccess: reader.GetInt32(0),
            ActiveSubscriptions: reader.GetInt32(1),
            ExpiredSubscriptions: reader.GetInt32(2)
        );
    }

    private static async Task<DateTimeOffset?> GetLastWebhookReceivedAtAsync(
        NpgsqlConnection conn,
        Guid projectId,
        string environment,
        CancellationToken ct)
    {
        const string sql = """
            select max(processed_at)
            from processed_events
            where project_id = @project_id
              and environment = @environment;
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("project_id", projectId);
        cmd.Parameters.AddWithValue("environment", environment);
        var result = await cmd.ExecuteScalarAsync(ct);

        return result switch
        {
            null => null,
            DBNull => null,
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException("Unexpected type for processed_events.processed_at.")
        };
    }
}
