using Npgsql;
using NpgsqlTypes;
using SubTru.Infrastructure.Persistence.Postgres;

namespace SubTru.Infrastructure.Persistence.Postgres.WebhookEvents;

public sealed class PostgresProcessedEventRepository : IProcessedEventRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public PostgresProcessedEventRepository(NpgsqlConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<bool> IsProcessedAsync(
        Guid projectId,
        string provider,
        string environment,
        string eventId,
        CancellationToken ct = default)
    {
        const string sql = """
            select 1
            from processed_events
            where project_id = @project_id
              and provider = @provider
              and environment = @environment
              and event_id = @event_id
            limit 1;
        """;

        await using var conn = _factory.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter<Guid>("project_id", projectId));
        cmd.Parameters.Add(new NpgsqlParameter("provider", NpgsqlDbType.Text) { Value = provider });
        cmd.Parameters.Add(new NpgsqlParameter("environment", NpgsqlDbType.Text) { Value = environment });
        cmd.Parameters.Add(new NpgsqlParameter("event_id", NpgsqlDbType.Text) { Value = eventId });

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null;
    }
}
