using Npgsql;
using NpgsqlTypes;
using SubTru.Domain.Subscriptions;
using SubTru.Infrastructure.Persistence.Postgres;

namespace SubTru.Infrastructure.Persistence.Postgres.Subscriptions;

public sealed class PostgresSubscriptionWriteRepository : ISubscriptionWriteRepository
    , IGoogleSubscriptionWriteRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public PostgresSubscriptionWriteRepository(NpgsqlConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<SubscriptionWriteOutcome> UpsertWithEventAsync(
        NormalizedSubscription sub,
        string eventId,
        CancellationToken ct)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(ct);

        await using var transaction = await conn.BeginTransactionAsync(ct);

        try
        {
            var claimedEvent = await TryMarkEventProcessedAsync(
                conn,
                transaction,
                sub.ProjectId,
                sub.Provider,
                sub.Environment,
                eventId,
                ct);

            if (!claimedEvent)
            {
                await transaction.RollbackAsync(ct);
                return SubscriptionWriteOutcome.Duplicate;
            }

            var writeOutcome = await UpsertSubscriptionCoreAsync(conn, transaction, sub, ct);

            await transaction.CommitAsync(ct);
            return writeOutcome;
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<SubscriptionWriteOutcome> UpsertGoogleWithEventAsync(
        NormalizedSubscription sub,
        string eventId,
        string? linkedPurchaseToken,
        CancellationToken ct)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(ct);

        await using var transaction = await conn.BeginTransactionAsync(ct);

        try
        {
            var claimedEvent = await TryMarkEventProcessedAsync(
                conn,
                transaction,
                sub.ProjectId,
                sub.Provider,
                sub.Environment,
                eventId,
                ct);

            if (!claimedEvent)
            {
                await transaction.RollbackAsync(ct);
                return SubscriptionWriteOutcome.Duplicate;
            }

            var writeOutcome = await UpsertSubscriptionCoreAsync(conn, transaction, sub, ct);

            if (writeOutcome == SubscriptionWriteOutcome.Applied
                && !string.IsNullOrWhiteSpace(linkedPurchaseToken)
                && !string.Equals(linkedPurchaseToken, sub.ProviderSubscriptionId, StringComparison.Ordinal))
            {
                await SupersedeLinkedGoogleSubscriptionAsync(
                    conn,
                    transaction,
                    sub.ProjectId,
                    sub.Environment,
                    linkedPurchaseToken,
                    sub.ProviderEventCreatedAt,
                    ct);
            }

            await transaction.CommitAsync(ct);
            return writeOutcome;
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task UpsertGoogleManualAsync(
        NormalizedSubscription sub,
        string? linkedPurchaseToken,
        CancellationToken ct)
    {
        // Treat subscription data fetched during a manual refresh as current now.
        var authoritativeSub = sub with
        {
            ProviderEventCreatedAt = sub.ProviderEventCreatedAt ?? DateTimeOffset.UtcNow
        };

        await using var conn = _factory.Create();
        await conn.OpenAsync(ct);

        await using var transaction = await conn.BeginTransactionAsync(ct);

        try
        {
            var writeOutcome = await UpsertSubscriptionCoreAsync(
                conn,
                transaction,
                authoritativeSub,
                ct);

            if (writeOutcome == SubscriptionWriteOutcome.Applied
                && !string.IsNullOrWhiteSpace(linkedPurchaseToken)
                && !string.Equals(linkedPurchaseToken, authoritativeSub.ProviderSubscriptionId, StringComparison.Ordinal))
            {
                await SupersedeLinkedGoogleSubscriptionAsync(
                    conn,
                    transaction,
                    authoritativeSub.ProjectId,
                    authoritativeSub.Environment,
                    linkedPurchaseToken,
                    authoritativeSub.ProviderEventCreatedAt,
                    ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    private static async Task<SubscriptionWriteOutcome> UpsertSubscriptionCoreAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? transaction,
        NormalizedSubscription sub,
        CancellationToken ct)
    {
        const string sql = """
            insert into subscriptions (
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
                cancel_at_period_end,
                provider_event_created_at
            )
            values (
                @project_id,
                @environment,
                @external_user_id,
                @provider,
                @provider_subscription_id,
                @product_id,
                @price_id,
                @status,
                @provider_status,
                @current_period_end,
                @access_ends_at,
                @cancel_at_period_end,
                @provider_event_created_at
            )
            on conflict (project_id, provider, environment, provider_subscription_id)
            do update set
                external_user_id = excluded.external_user_id,
                product_id = excluded.product_id,
                price_id = excluded.price_id,
                status = excluded.status,
                provider_status = excluded.provider_status,
                current_period_end = excluded.current_period_end,
                access_ends_at = excluded.access_ends_at,
                cancel_at_period_end = excluded.cancel_at_period_end,
                provider_event_created_at = coalesce(
                    excluded.provider_event_created_at,
                    subscriptions.provider_event_created_at),
                updated_at = now()
            where subscriptions.provider_event_created_at is null
               or (
                   excluded.provider_event_created_at is not null
                   and excluded.provider_event_created_at >= subscriptions.provider_event_created_at
               );
        """;

        await using var cmd = new NpgsqlCommand(sql, conn, transaction);

        cmd.Parameters.AddWithValue("project_id", sub.ProjectId);
        cmd.Parameters.AddWithValue("environment", sub.Environment);
        cmd.Parameters.AddWithValue("external_user_id", sub.ExternalUserId);
        cmd.Parameters.AddWithValue("provider", sub.Provider);
        cmd.Parameters.AddWithValue("provider_subscription_id", sub.ProviderSubscriptionId);

        cmd.Parameters.AddWithValue("product_id", (object?)sub.ProductId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("price_id", (object?)sub.PriceId ?? DBNull.Value);

        cmd.Parameters.AddWithValue("status", sub.Status);
        cmd.Parameters.AddWithValue("provider_status", (object?)sub.ProviderStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("current_period_end", sub.CurrentPeriodEnd);
        cmd.Parameters.Add("access_ends_at", NpgsqlDbType.TimestampTz).Value =
            (object?)sub.AccessEndsAt ?? DBNull.Value;
        cmd.Parameters.AddWithValue("cancel_at_period_end", sub.CancelAtPeriodEnd);
        cmd.Parameters.Add("provider_event_created_at", NpgsqlDbType.TimestampTz).Value =
            (object?)sub.ProviderEventCreatedAt ?? DBNull.Value;

        return await cmd.ExecuteNonQueryAsync(ct) > 0
            ? SubscriptionWriteOutcome.Applied
            : SubscriptionWriteOutcome.Stale;
    }

    private static async Task<bool> TryMarkEventProcessedAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction transaction,
        Guid projectId,
        string provider,
        string environment,
        string eventId,
        CancellationToken ct)
    {
        const string sql = """
            INSERT INTO processed_events (project_id, provider, environment, event_id)
            VALUES (@project_id, @provider, @environment, @event_id)
            ON CONFLICT (project_id, provider, environment, event_id) DO NOTHING
            RETURNING 1;
            """;

        await using var cmd = new NpgsqlCommand(sql, conn, transaction);
        cmd.Parameters.AddWithValue("project_id", projectId);
        cmd.Parameters.AddWithValue("provider", provider);
        cmd.Parameters.AddWithValue("environment", environment);
        cmd.Parameters.AddWithValue("event_id", eventId);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null;
    }

    private static async Task SupersedeLinkedGoogleSubscriptionAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction transaction,
        Guid projectId,
        string environment,
        string linkedPurchaseToken,
        DateTimeOffset? providerEventCreatedAt,
        CancellationToken ct)
    {
        const string sql = """
            update subscriptions
            set
                status = 'expired',
                current_period_end = least(current_period_end, now()),
                access_ends_at = least(
                    coalesce(access_ends_at, current_period_end),
                    now()
                ),
                cancel_at_period_end = true,
                provider_event_created_at = coalesce(
                    @provider_event_created_at,
                    provider_event_created_at),
                updated_at = now()
            where project_id = @project_id
              and provider = 'google'
              and environment = @environment
              and provider_subscription_id = @provider_subscription_id
              and (
                  provider_event_created_at is null
                  or (
                      @provider_event_created_at is not null
                      and @provider_event_created_at >= provider_event_created_at
                  )
              );
            """;

        await using var cmd = new NpgsqlCommand(sql, conn, transaction);
        cmd.Parameters.AddWithValue("project_id", projectId);
        cmd.Parameters.AddWithValue("environment", environment);
        cmd.Parameters.AddWithValue("provider_subscription_id", linkedPurchaseToken);
        cmd.Parameters.Add("provider_event_created_at", NpgsqlDbType.TimestampTz).Value =
            (object?)providerEventCreatedAt ?? DBNull.Value;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
