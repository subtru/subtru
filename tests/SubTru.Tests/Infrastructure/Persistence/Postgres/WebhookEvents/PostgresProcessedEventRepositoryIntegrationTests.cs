using Npgsql;
using SubTru.Domain.Subscriptions;
using SubTru.Infrastructure.Persistence.Postgres;
using SubTru.Infrastructure.Persistence.Postgres.ProviderConfiguration;
using SubTru.Infrastructure.Persistence.Postgres.WebhookEvents;
using SubTru.Infrastructure.Persistence.Postgres.WebhookKeys;
using SubTru.Tests.Support.Postgres;

namespace SubTru.Tests.Infrastructure.Persistence.Postgres.WebhookEvents;

public sealed class PostgresProcessedEventRepositoryIntegrationTests
{
    [Trait("Category", "Integration")]
    [RequiresPostgresFact]
    public async Task IsProcessedAsync_ScopesByProjectAndProvider()
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
            throw new InvalidOperationException("Integration test DB is missing required schema (projects/processed_events).");
        }

        var repository = new PostgresProcessedEventRepository(factory);

        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        const string sharedEventId = "evt_shared_123";

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectA, ct);
        await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectB, ct);

        await PostgresIntegrationTestData.InsertProjectAsync(conn, projectA, "it-processed-events-a", ct);
        await PostgresIntegrationTestData.InsertProjectAsync(conn, projectB, "it-processed-events-b", ct);

        try
        {
            await InsertProcessedEventAsync(conn, projectA, "stripe", sharedEventId, ct);

            var processedInProjectAForStripe = await repository.IsProcessedAsync(projectA, "stripe", SubscriptionEnvironment.Production, sharedEventId, ct);
            var processedInProjectAForApple = await repository.IsProcessedAsync(projectA, "apple", SubscriptionEnvironment.Production, sharedEventId, ct);
            var processedInProjectBForStripe = await repository.IsProcessedAsync(projectB, "stripe", SubscriptionEnvironment.Production, sharedEventId, ct);
            var differentEventInA = await repository.IsProcessedAsync(projectA, "stripe", SubscriptionEnvironment.Production, "evt_other", ct);

            Assert.True(processedInProjectAForStripe);
            Assert.False(processedInProjectAForApple);
            Assert.False(processedInProjectBForStripe);
            Assert.False(differentEventInA);
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
                "SELECT to_regclass('public.projects') IS NOT NULL AND to_regclass('public.processed_events') IS NOT NULL;",
                conn);

            var result = await cmd.ExecuteScalarAsync(ct);
            return result is true;
        }
        catch (NpgsqlException)
        {
            return null;
        }
    }

    private static async Task InsertProcessedEventAsync(
        NpgsqlConnection conn,
        Guid projectId,
        string provider,
        string eventId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO processed_events (project_id, provider, event_id) VALUES (@project_id, @provider, @event_id);",
            conn);

        cmd.Parameters.AddWithValue("project_id", projectId);
        cmd.Parameters.AddWithValue("provider", provider);
        cmd.Parameters.AddWithValue("event_id", eventId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string? GetTestConnectionString()
        => Environment.GetEnvironmentVariable("SUBTRU_TEST_DB_CONNECTION");
}
