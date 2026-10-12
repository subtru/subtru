using Npgsql;
using NpgsqlTypes;
using SubTru.Infrastructure.Persistence.Postgres;

namespace SubTru.Infrastructure.Persistence.Postgres.WebhookEvents;

public sealed class PostgresWebhookEventLogRepository : IWebhookEventLogRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public PostgresWebhookEventLogRepository(NpgsqlConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task UpsertAsync(
        WebhookEventLogWriteModel writeModel,
        CancellationToken ct = default)
    {
        const string sql = """
            insert into webhook_events (
                project_id,
                provider,
                environment,
                event_id,
                event_type,
                event_change,
                external_user_id,
                provider_subscription_id,
                status,
                error_code,
                error_message,
                provider_created_at,
                received_at,
                processed_at,
                last_attempted_at
            )
            values (
                @project_id,
                @provider,
                @environment,
                @event_id,
                @event_type,
                @event_change,
                @external_user_id,
                @provider_subscription_id,
                @status,
                @error_code,
                @error_message,
                @provider_created_at,
                @received_at,
                @processed_at,
                @last_attempted_at
            )
            on conflict (project_id, provider, environment, event_id)
            do update set
                event_type = coalesce(excluded.event_type, webhook_events.event_type),
                event_change = coalesce(excluded.event_change, webhook_events.event_change),
                external_user_id = coalesce(excluded.external_user_id, webhook_events.external_user_id),
                status = case
                    when webhook_events.status = 'success' then webhook_events.status
                    else excluded.status
                end,
                error_code = case
                    when webhook_events.status = 'success' then webhook_events.error_code
                    else excluded.error_code
                end,
                error_message = case
                    when webhook_events.status = 'success' then webhook_events.error_message
                    else excluded.error_message
                end,
                provider_subscription_id = coalesce(excluded.provider_subscription_id, webhook_events.provider_subscription_id),
                provider_created_at = coalesce(webhook_events.provider_created_at, excluded.provider_created_at),
                received_at = least(webhook_events.received_at, excluded.received_at),
                processed_at = case
                    when webhook_events.status = 'success' then webhook_events.processed_at
                    else coalesce(excluded.processed_at, webhook_events.processed_at)
                end,
                last_attempted_at = excluded.last_attempted_at,
                attempt_count = webhook_events.attempt_count + 1;
        """;

        await using var conn = _factory.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter<Guid>("project_id", writeModel.ProjectId));
        cmd.Parameters.Add(new NpgsqlParameter("provider", NpgsqlDbType.Text) { Value = writeModel.Provider });
        cmd.Parameters.Add(new NpgsqlParameter("environment", NpgsqlDbType.Text) { Value = writeModel.Environment });
        cmd.Parameters.Add(new NpgsqlParameter("event_id", NpgsqlDbType.Text) { Value = writeModel.EventId });
        cmd.Parameters.Add(new NpgsqlParameter("event_type", NpgsqlDbType.Text) { Value = (object?)writeModel.EventType ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("event_change", NpgsqlDbType.Text) { Value = (object?)writeModel.EventChange ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("external_user_id", NpgsqlDbType.Text) { Value = (object?)writeModel.ExternalUserId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("provider_subscription_id", NpgsqlDbType.Text) { Value = (object?)writeModel.ProviderSubscriptionId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = writeModel.Status });
        cmd.Parameters.Add(new NpgsqlParameter("error_code", NpgsqlDbType.Text) { Value = (object?)writeModel.ErrorCode ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("error_message", NpgsqlDbType.Text) { Value = (object?)writeModel.ErrorMessage ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("provider_created_at", NpgsqlDbType.TimestampTz) { Value = (object?)writeModel.ProviderCreatedAt ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("received_at", NpgsqlDbType.TimestampTz) { Value = writeModel.ReceivedAt });
        cmd.Parameters.Add(new NpgsqlParameter("processed_at", NpgsqlDbType.TimestampTz) { Value = (object?)writeModel.ProcessedAt ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("last_attempted_at", NpgsqlDbType.TimestampTz) { Value = writeModel.LastAttemptedAt });

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<WebhookEventListPageReadModel> ListRecentAsync(
        Guid projectId,
        string environment,
        int limit,
        WebhookEventCursorReadModel? cursor,
        string? provider = null,
        WebhookEventStatusFilter? statusFilter = null,
        CancellationToken ct = default)
    {
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be at least 1.");
        }

        const string sql = """
            select
                event_id,
                provider,
                environment,
                event_type,
                event_change,
                external_user_id,
                status,
                received_at,
                processed_at,
                error_code,
                provider_subscription_id
            from webhook_events
            where project_id = @project_id
                and environment = @environment
                and (
                    status in ('success', 'failed')
                    or (status = 'ignored' and event_type in (
                        'google.test_notification',
                        'google.pending_refund_review',
                        'TEST'
                    ))
                )
                and (@provider is null or provider = @provider)
                and (
                    @status_filter is null
                    or (@status_filter = 'failed' and status = 'failed')
                    or (@status_filter = 'succeeded' and status <> 'failed')
                )
                and (
                    @cursor_received_at is null
                    or (received_at, event_id) < (@cursor_received_at, @cursor_event_id)
                )
            order by received_at desc, event_id desc
            limit @limit_plus_one;
        """;

        await using var conn = _factory.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter<Guid>("project_id", projectId));
        cmd.Parameters.Add(new NpgsqlParameter("environment", NpgsqlDbType.Text) { Value = environment });

        cmd.Parameters.Add(new NpgsqlParameter("provider", NpgsqlDbType.Text)
        {
            Value = (object?)provider ?? DBNull.Value
        });

        var statusFilterValue = statusFilter switch
        {
            WebhookEventStatusFilter.Failed => "failed",
            WebhookEventStatusFilter.Succeeded => "succeeded",
            _ => null
        };
        cmd.Parameters.Add(new NpgsqlParameter("status_filter", NpgsqlDbType.Text)
        {
            Value = (object?)statusFilterValue ?? DBNull.Value
        });

        var cursorReceivedAt = new NpgsqlParameter("cursor_received_at", NpgsqlDbType.TimestampTz)
        {
            Value = (object?)cursor?.ReceivedAt ?? DBNull.Value
        };
        cmd.Parameters.Add(cursorReceivedAt);

        var cursorEventId = new NpgsqlParameter("cursor_event_id", NpgsqlDbType.Text)
        {
            Value = (object?)cursor?.EventId ?? DBNull.Value
        };
        cmd.Parameters.Add(cursorEventId);

        cmd.Parameters.Add(new NpgsqlParameter<int>("limit_plus_one", limit + 1));

        var rows = new List<WebhookEventListItemReadModel>(limit + 1);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new WebhookEventListItemReadModel(
                EventId: reader.GetString(0),
                Provider: reader.GetString(1),
                Environment: reader.GetString(2),
                EventType: reader.IsDBNull(3) ? null : reader.GetString(3),
                EventChange: reader.IsDBNull(4) ? null : reader.GetString(4),
                ExternalUserId: reader.IsDBNull(5) ? null : reader.GetString(5),
                Status: reader.GetString(6),
                ReceivedAt: reader.GetFieldValue<DateTimeOffset>(7),
                ProcessedAt: reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
                ErrorCode: reader.IsDBNull(9) ? null : reader.GetString(9),
                ProviderSubscriptionId: reader.IsDBNull(10) ? null : reader.GetString(10)
            ));
        }

        WebhookEventCursorReadModel? nextCursor = null;

        if (rows.Count > limit)
        {
            var next = rows[limit - 1];
            nextCursor = new WebhookEventCursorReadModel(next.ReceivedAt, next.EventId);
            rows = rows.Take(limit).ToList();
        }

        return new WebhookEventListPageReadModel(rows, nextCursor);
    }
}
