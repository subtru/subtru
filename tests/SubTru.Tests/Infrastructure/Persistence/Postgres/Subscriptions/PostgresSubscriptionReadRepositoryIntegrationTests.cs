using Npgsql;
using SubTru.Domain.Subscriptions;
using SubTru.Infrastructure.Persistence.Postgres;
using SubTru.Infrastructure.Persistence.Postgres.Subscriptions;
using SubTru.Tests.Support.Postgres;

namespace SubTru.Tests.Infrastructure.Persistence.Postgres.Subscriptions;

public sealed class PostgresSubscriptionReadRepositoryIntegrationTests
{
    [Trait("Category", "Integration")]
    [RequiresPostgresFact]
    public async Task GetByUserAsync_ReturnsProviderStatusWhenPresent()
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
            throw new InvalidOperationException("Integration test DB is missing required schema (projects/subscriptions).");
        }

        var repository = new PostgresSubscriptionReadRepository(factory);
        var projectId = Guid.NewGuid();
        const string externalUserId = "user-123";

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectId, ct);
        await PostgresIntegrationTestData.InsertProjectAsync(conn, projectId, "it-subscription-read-provider-status", ct);

        try
        {
            await InsertSubscriptionAsync(
                conn,
                projectId,
                externalUserId,
                provider: "google",
                providerSubscriptionId: "purchase-token-123",
                status: "active",
                providerStatus: "SUBSCRIPTION_STATE_ACTIVE",
                ct);

            var subscriptions = await repository.GetByUserAsync(projectId, SubscriptionEnvironment.Production, externalUserId, ct);

            var subscription = Assert.Single(subscriptions);
            Assert.Equal("google", subscription.Provider);
            Assert.Equal("active", subscription.Status);
            Assert.Equal("SUBSCRIPTION_STATE_ACTIVE", subscription.ProviderStatus);
            Assert.Equal(subscription.CurrentPeriodEnd, subscription.AccessEndsAt);
            Assert.False(subscription.CancelAtPeriodEnd);
        }
        finally
        {
            await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectId, ct);
        }
    }

    private static async Task<bool?> TryHasRequiredSchemaAsync(NpgsqlConnectionFactory factory, CancellationToken ct)
    {
        try
        {
            await using var conn = factory.Create();
            await conn.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand(
                "SELECT to_regclass('public.projects') IS NOT NULL AND to_regclass('public.subscriptions') IS NOT NULL;",
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
        string provider,
        string providerSubscriptionId,
        string status,
        string? providerStatus,
        CancellationToken ct)
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
                @current_period_end,
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
        cmd.Parameters.AddWithValue("current_period_end", DateTimeOffset.UtcNow.AddDays(30));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string? GetTestConnectionString()
        => Environment.GetEnvironmentVariable("SUBTRU_TEST_DB_CONNECTION");
}
