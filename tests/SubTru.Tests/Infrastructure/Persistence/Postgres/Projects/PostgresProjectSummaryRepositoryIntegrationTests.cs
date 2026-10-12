using Npgsql;
using SubTru.Domain.Subscriptions;
using SubTru.Infrastructure.Persistence.Postgres;
using SubTru.Infrastructure.Persistence.Postgres.Projects;
using SubTru.Tests.Support.Postgres;

namespace SubTru.Tests.Infrastructure.Persistence.Postgres.Projects;

public sealed class PostgresProjectSummaryRepositoryIntegrationTests
{
    [Trait("Category", "Integration")]
    [RequiresPostgresFact]
    public async Task GetSummaryAsync_FiltersWebhookTimestampByProject()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        var connectionString = GetTestConnectionString()
            ?? throw new InvalidOperationException("SUBTRU_TEST_DB_CONNECTION must be set for this test.");

        var factory = new NpgsqlConnectionFactory(connectionString);

        var hasSchema = await TryHasRequiredSchemaAsync(factory, ct);
        if (!hasSchema.HasValue)
        {
            throw new InvalidOperationException("Postgres is not reachable. Start local integration Postgres to run this test.");
        }

        if (!hasSchema.Value)
        {
            throw new InvalidOperationException("Integration test DB is missing required schema (projects/subscriptions/processed_events).");
        }

        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();

        var projectAWebhook = new DateTimeOffset(2026, 2, 14, 21, 4, 2, TimeSpan.Zero);
        var projectBWebhook = new DateTimeOffset(2026, 2, 15, 9, 10, 11, TimeSpan.Zero);

        var repository = new PostgresProjectSummaryRepository(factory);

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectA, ct);
        await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectB, ct);

        await PostgresIntegrationTestData.InsertProjectAsync(conn, projectA, "it-summary-a", ct);
        await PostgresIntegrationTestData.InsertProjectAsync(conn, projectB, "it-summary-b", ct);

        try
        {
            await InsertSubscriptionAsync(conn, projectA, "user-a", "active", DateTimeOffset.UtcNow.AddDays(10), "sub-a-active", ct);
            await InsertSubscriptionAsync(conn, projectA, "user-a", "expired", DateTimeOffset.UtcNow.AddDays(-2), "sub-a-expired", ct);
            await InsertSubscriptionAsync(conn, projectA, "user-c", "trialing", DateTimeOffset.UtcNow.AddDays(-1), "sub-a-trial-lapsed", ct);
            await InsertSubscriptionAsync(conn, projectA, "user-grace", "billing_grace", DateTimeOffset.UtcNow.AddDays(-1), "sub-a-grace", ct, provider: "apple", providerStatus: "4", unknownAccessEnd: true);
            await InsertSubscriptionAsync(conn, projectA, "user-stripe-grace", "billing_grace", DateTimeOffset.UtcNow.AddDays(-1), "sub-a-stripe-grace", ct, providerStatus: "past_due", unknownAccessEnd: true);
            await InsertSubscriptionAsync(conn, projectA, "user-on-hold", "billing_suspended", DateTimeOffset.UtcNow.AddDays(5), "sub-a-google-suspended", ct, provider: "google", providerStatus: "SUBSCRIPTION_STATE_ON_HOLD");
            await InsertSubscriptionAsync(conn, projectB, "user-b", "active", DateTimeOffset.UtcNow.AddDays(10), "sub-b-active", ct);
            await InsertProcessedEventAsync(conn, projectA, "stripe", "evt_shared", projectAWebhook, ct);
            await InsertProcessedEventAsync(conn, projectA, "apple", "evt_apple", projectAWebhook.AddMinutes(5), ct);
            await InsertProcessedEventAsync(conn, projectB, "stripe", "evt_shared", projectBWebhook, ct);

            var summary = await repository.GetSummaryAsync(projectA, SubscriptionEnvironment.Production, ct);

            Assert.Equal(3, summary.UsersWithAccess);
            Assert.Equal(3, summary.ActiveSubscriptions);
            Assert.Equal(3, summary.ExpiredSubscriptions);
            Assert.Equal(projectAWebhook.AddMinutes(5), summary.LastWebhookReceivedAt);
        }
        finally
        {
            await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectA, ct);
            await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectB, ct);
        }
    }

    private static async Task<bool?> TryHasRequiredSchemaAsync(NpgsqlConnectionFactory factory, CancellationToken ct)
    {
        try
        {
            await using var conn = factory.Create();
            await conn.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand(
                "SELECT to_regclass('public.projects') IS NOT NULL AND to_regclass('public.subscriptions') IS NOT NULL AND to_regclass('public.processed_events') IS NOT NULL;",
                conn);

            var result = await cmd.ExecuteScalarAsync(ct);
            return result is true;
        }
        catch (NpgsqlException)
        {
            return null;
        }
    }

    private static async Task InsertSubscriptionAsync(
        NpgsqlConnection conn,
        Guid projectId,
        string externalUserId,
        string status,
        DateTimeOffset currentPeriodEnd,
        string providerSubscriptionId,
        CancellationToken ct,
        string provider = "stripe",
        string? providerStatus = null,
        bool unknownAccessEnd = false)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO subscriptions (
                project_id,
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
            )
            VALUES (
                @project_id,
                @external_user_id,
                @provider,
                @provider_subscription_id,
                'prod_test',
                'price_test',
                @status,
                @provider_status,
                @current_period_end,
                @access_ends_at,
                false
            );
            """,
            conn);

        cmd.Parameters.AddWithValue("project_id", projectId);
        cmd.Parameters.AddWithValue("external_user_id", externalUserId);
        cmd.Parameters.AddWithValue("provider", provider);
        cmd.Parameters.AddWithValue("provider_subscription_id", providerSubscriptionId);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("provider_status", (object?)providerStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("current_period_end", currentPeriodEnd);
        cmd.Parameters.AddWithValue("access_ends_at", unknownAccessEnd ? (object)DBNull.Value : currentPeriodEnd);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertProcessedEventAsync(
        NpgsqlConnection conn,
        Guid projectId,
        string provider,
        string eventId,
        DateTimeOffset processedAt,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO processed_events (project_id, provider, event_id, processed_at) VALUES (@project_id, @provider, @event_id, @processed_at);",
            conn);

        cmd.Parameters.AddWithValue("project_id", projectId);
        cmd.Parameters.AddWithValue("provider", provider);
        cmd.Parameters.AddWithValue("event_id", eventId);
        cmd.Parameters.AddWithValue("processed_at", processedAt);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string? GetTestConnectionString()
        => Environment.GetEnvironmentVariable("SUBTRU_TEST_DB_CONNECTION");
}
