using Npgsql;
using SubTru.Domain.Subscriptions;
using SubTru.Infrastructure.Persistence.Postgres;
using SubTru.Infrastructure.Persistence.Postgres.ProviderConfiguration;
using SubTru.Infrastructure.Persistence.Postgres.WebhookEvents;
using SubTru.Infrastructure.Persistence.Postgres.WebhookKeys;
using SubTru.Tests.Support.Postgres;

namespace SubTru.Tests.Infrastructure.Persistence.Postgres.WebhookEvents;

public sealed class PostgresWebhookEventLogRepositoryIntegrationTests
{
    [Trait("Category", "Integration")]
    [RequiresPostgresFact]
    public async Task ListRecentAsync_ProjectScoped_OrderedAndCursorPaged()
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
            throw new InvalidOperationException("Integration test DB is missing required schema (projects/webhook_events).");
        }

        var repository = new PostgresWebhookEventLogRepository(factory);

        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectA, ct);
        await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectB, ct);

        await PostgresIntegrationTestData.InsertProjectAsync(conn, projectA, "it-webhook-events-a", ct);
        await PostgresIntegrationTestData.InsertProjectAsync(conn, projectB, "it-webhook-events-b", ct);

        try
        {
            await repository.UpsertAsync(new WebhookEventLogWriteModel(
                ProjectId: projectA,
                Provider: "stripe",
                Environment: SubscriptionEnvironment.Production,
                EventId: "evt_001",
                EventType: "customer.subscription.updated",
                ExternalUserId: "user-a",
                Status: WebhookEventStatus.Success,
                ErrorCode: null,
                ErrorMessage: null,
                ProviderCreatedAt: new DateTimeOffset(2026, 2, 17, 10, 0, 0, TimeSpan.Zero),
                ReceivedAt: new DateTimeOffset(2026, 2, 17, 10, 0, 5, TimeSpan.Zero),
                LastAttemptedAt: new DateTimeOffset(2026, 2, 17, 10, 0, 7, TimeSpan.Zero),
                ProcessedAt: new DateTimeOffset(2026, 2, 17, 10, 0, 7, TimeSpan.Zero),
                ProviderSubscriptionId: null,
                EventChange: "cancellation_scheduled"
            ), ct);

            var originalSuccessProcessedAt = await GetProcessedAtAsync(conn, projectA, "stripe", "evt_001", ct);
            Assert.Equal(new DateTimeOffset(2026, 2, 17, 10, 0, 7, TimeSpan.Zero), originalSuccessProcessedAt);

            await repository.UpsertAsync(new WebhookEventLogWriteModel(
                ProjectId: projectA,
                Provider: "google",
                Environment: SubscriptionEnvironment.Production,
                EventId: "evt_002",
                EventType: "google.subscription.recovered",
                ExternalUserId: null,
                Status: WebhookEventStatus.Failed,
                ErrorCode: "google_unbound_purchase",
                ErrorMessage: "Google purchase could not be safely bound to a SubTru user.",
                ProviderCreatedAt: new DateTimeOffset(2026, 2, 17, 10, 1, 0, TimeSpan.Zero),
                ReceivedAt: new DateTimeOffset(2026, 2, 17, 10, 1, 5, TimeSpan.Zero),
                LastAttemptedAt: new DateTimeOffset(2026, 2, 17, 10, 1, 7, TimeSpan.Zero),
                ProcessedAt: null,
                ProviderSubscriptionId: "token-unbound"
            ), ct);

            var originalFailedReceivedAt = await GetReceivedAtAsync(conn, projectA, "google", "evt_002", ct);
            Assert.Equal(new DateTimeOffset(2026, 2, 17, 10, 1, 5, TimeSpan.Zero), originalFailedReceivedAt);

            await repository.UpsertAsync(new WebhookEventLogWriteModel(
                ProjectId: projectA,
                Provider: "stripe",
                Environment: SubscriptionEnvironment.Production,
                EventId: "evt_003",
                EventType: "customer.subscription.deleted",
                ExternalUserId: "user-a",
                Status: WebhookEventStatus.Success,
                ErrorCode: null,
                ErrorMessage: null,
                ProviderCreatedAt: new DateTimeOffset(2026, 2, 17, 10, 2, 0, TimeSpan.Zero),
                ReceivedAt: new DateTimeOffset(2026, 2, 17, 10, 2, 5, TimeSpan.Zero),
                LastAttemptedAt: new DateTimeOffset(2026, 2, 17, 10, 2, 7, TimeSpan.Zero),
                ProcessedAt: new DateTimeOffset(2026, 2, 17, 10, 2, 7, TimeSpan.Zero),
                ProviderSubscriptionId: null
            ), ct);

            await repository.UpsertAsync(new WebhookEventLogWriteModel(
                ProjectId: projectB,
                Provider: "stripe",
                Environment: SubscriptionEnvironment.Production,
                EventId: "evt_other_project",
                EventType: "customer.subscription.created",
                ExternalUserId: "user-b",
                Status: WebhookEventStatus.Success,
                ErrorCode: null,
                ErrorMessage: null,
                ProviderCreatedAt: new DateTimeOffset(2026, 2, 17, 11, 0, 0, TimeSpan.Zero),
                ReceivedAt: new DateTimeOffset(2026, 2, 17, 11, 0, 5, TimeSpan.Zero),
                LastAttemptedAt: new DateTimeOffset(2026, 2, 17, 11, 0, 7, TimeSpan.Zero),
                ProcessedAt: new DateTimeOffset(2026, 2, 17, 11, 0, 7, TimeSpan.Zero),
                ProviderSubscriptionId: null
            ), ct);

            var firstPage = await repository.ListRecentAsync(projectA, SubscriptionEnvironment.Production, limit: 2, cursor: null, ct: ct);

            Assert.Equal(2, firstPage.Items.Count);
            Assert.Equal("evt_003", firstPage.Items[0].EventId);
            Assert.Equal("evt_002", firstPage.Items[1].EventId);
            Assert.Equal("token-unbound", firstPage.Items[1].ProviderSubscriptionId);
            Assert.NotNull(firstPage.NextCursor);

            var secondPage = await repository.ListRecentAsync(projectA, SubscriptionEnvironment.Production, limit: 2, cursor: firstPage.NextCursor, ct: ct);

            Assert.Single(secondPage.Items);
            Assert.Equal("evt_001", secondPage.Items[0].EventId);
            Assert.Equal("cancellation_scheduled", secondPage.Items[0].EventChange);
            Assert.Null(secondPage.NextCursor);

            var attemptCount = await GetAttemptCountAsync(conn, projectA, "google", "evt_002", ct);
            Assert.Equal(1, attemptCount);

            await repository.UpsertAsync(new WebhookEventLogWriteModel(
                ProjectId: projectA,
                Provider: "google",
                Environment: SubscriptionEnvironment.Production,
                EventId: "evt_002",
                EventType: "google.subscription.recovered",
                ExternalUserId: "user-a",
                Status: WebhookEventStatus.Success,
                ErrorCode: null,
                ErrorMessage: null,
                ProviderCreatedAt: null,
                ReceivedAt: new DateTimeOffset(2026, 2, 17, 10, 1, 6, TimeSpan.Zero),
                LastAttemptedAt: new DateTimeOffset(2026, 2, 17, 10, 3, 0, TimeSpan.Zero),
                ProcessedAt: new DateTimeOffset(2026, 2, 17, 10, 3, 0, TimeSpan.Zero),
                ProviderSubscriptionId: null
            ), ct);

            attemptCount = await GetAttemptCountAsync(conn, projectA, "google", "evt_002", ct);
            Assert.Equal(2, attemptCount);

            var receivedAtAfterRecovery = await GetReceivedAtAsync(conn, projectA, "google", "evt_002", ct);
            Assert.Equal(originalFailedReceivedAt, receivedAtAfterRecovery);

            var recoveredPage = await repository.ListRecentAsync(projectA, SubscriptionEnvironment.Production, limit: 3, cursor: null, ct: ct);
            var recovered = recoveredPage.Items.Single(x => x.EventId == "evt_002");
            Assert.Equal("token-unbound", recovered.ProviderSubscriptionId);

            await repository.UpsertAsync(new WebhookEventLogWriteModel(
                ProjectId: projectA,
                Provider: "stripe",
                Environment: SubscriptionEnvironment.Production,
                EventId: "evt_001",
                EventType: "customer.subscription.updated",
                ExternalUserId: "user-a",
                Status: WebhookEventStatus.Duplicate,
                ErrorCode: null,
                ErrorMessage: null,
                ProviderCreatedAt: null,
                ReceivedAt: new DateTimeOffset(2026, 2, 17, 10, 0, 10, TimeSpan.Zero),
                LastAttemptedAt: new DateTimeOffset(2026, 2, 17, 10, 4, 0, TimeSpan.Zero),
                ProcessedAt: new DateTimeOffset(2026, 2, 17, 10, 4, 0, TimeSpan.Zero),
                ProviderSubscriptionId: null
            ), ct);

            var statusAfterDuplicate = await GetStatusAsync(conn, projectA, "stripe", "evt_001", ct);
            Assert.Equal(WebhookEventStatus.Success, statusAfterDuplicate);

            var processedAtAfterDuplicate = await GetProcessedAtAsync(conn, projectA, "stripe", "evt_001", ct);
            Assert.Equal(originalSuccessProcessedAt, processedAtAfterDuplicate);

            var attemptCountAfterDuplicate = await GetAttemptCountAsync(conn, projectA, "stripe", "evt_001", ct);
            Assert.Equal(2, attemptCountAfterDuplicate);
        }
        finally
        {
            await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectA, ct);
            await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectB, ct);
        }
    }

    [Trait("Category", "Integration")]
    [RequiresPostgresFact]
    public async Task ListRecentAsync_FiltersByProviderAndStatus()
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
            throw new InvalidOperationException("Integration test DB is missing required schema (projects/webhook_events).");
        }

        var repository = new PostgresWebhookEventLogRepository(factory);
        var project = Guid.NewGuid();

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        await PostgresIntegrationTestData.DeleteProjectAsync(conn, project, ct);
        await PostgresIntegrationTestData.InsertProjectAsync(conn, project, "it-webhook-filter", ct);

        async Task Seed(string eventId, string provider, string status, int minute, string? errorCode = null, string? eventType = null)
        {
            await repository.UpsertAsync(new WebhookEventLogWriteModel(
                ProjectId: project,
                Provider: provider,
                Environment: SubscriptionEnvironment.Production,
                EventId: eventId,
                EventType: eventType
                    ?? (provider == "google" && status == WebhookEventStatus.Ignored
                        ? "google.test_notification"
                        : $"{provider}.event"),
                ExternalUserId: null,
                Status: status,
                ErrorCode: errorCode,
                ErrorMessage: null,
                ProviderCreatedAt: null,
                ReceivedAt: new DateTimeOffset(2026, 2, 17, 10, minute, 0, TimeSpan.Zero),
                LastAttemptedAt: new DateTimeOffset(2026, 2, 17, 10, minute, 1, TimeSpan.Zero),
                ProcessedAt: status == WebhookEventStatus.Success
                    ? new DateTimeOffset(2026, 2, 17, 10, minute, 1, TimeSpan.Zero)
                    : null,
                ProviderSubscriptionId: null
            ), ct);
        }

        try
        {
            await Seed("evt_s1", "stripe", WebhookEventStatus.Success, 1);
            await Seed("evt_s2", "stripe", WebhookEventStatus.Failed, 2, errorCode: "stripe_api_version_mismatch");
            await Seed("evt_g1", "google", WebhookEventStatus.Failed, 3, errorCode: "google_unbound_purchase");
            await Seed("evt_g2", "google", WebhookEventStatus.Ignored, 4);
            await Seed("evt_a1", "apple", WebhookEventStatus.Ignored, 5, eventType: "TEST");
            await Seed("evt_g3", "google", WebhookEventStatus.Ignored, 6,
                eventType: "google.pending_refund_review");
            // Ordinary ignored events stay invisible; explicitly visible notifications are rescued.
            await Seed("evt_a2", "apple", WebhookEventStatus.Ignored, 7, eventType: "DID_CHANGE_RENEWAL_PREF");

            var unfiltered = await repository.ListRecentAsync(
                project, SubscriptionEnvironment.Production, limit: 50, cursor: null, ct: ct);
            Assert.Contains(unfiltered.Items, x => x.EventId == "evt_a1");
            Assert.Contains(unfiltered.Items, x => x.EventId == "evt_g3");
            Assert.DoesNotContain(unfiltered.Items, x => x.EventId == "evt_a2");

            var stripeOnly = await repository.ListRecentAsync(
                project, SubscriptionEnvironment.Production, limit: 50, cursor: null, provider: "stripe", ct: ct);
            Assert.Equal(2, stripeOnly.Items.Count);
            Assert.All(stripeOnly.Items, x => Assert.Equal("stripe", x.Provider));

            var failedOnly = await repository.ListRecentAsync(
                project, SubscriptionEnvironment.Production, limit: 50, cursor: null,
                statusFilter: WebhookEventStatusFilter.Failed, ct: ct);
            Assert.Equal(2, failedOnly.Items.Count);
            Assert.All(failedOnly.Items, x => Assert.Equal("failed", x.Status));

            var succeededOnly = await repository.ListRecentAsync(
                project, SubscriptionEnvironment.Production, limit: 50, cursor: null,
                statusFilter: WebhookEventStatusFilter.Succeeded, ct: ct);
            Assert.DoesNotContain(succeededOnly.Items, x => x.Status == "failed");
            // Visible ignored notifications count as succeeded.
            Assert.Contains(succeededOnly.Items, x => x.EventId == "evt_g2");
            Assert.Contains(succeededOnly.Items, x => x.EventId == "evt_a1");
            Assert.Contains(succeededOnly.Items, x => x.EventId == "evt_g3");
            Assert.Contains(succeededOnly.Items, x => x.EventId == "evt_s1");

            var googleFailed = await repository.ListRecentAsync(
                project, SubscriptionEnvironment.Production, limit: 50, cursor: null,
                provider: "google", statusFilter: WebhookEventStatusFilter.Failed, ct: ct);
            Assert.Single(googleFailed.Items);
            Assert.Equal("evt_g1", googleFailed.Items[0].EventId);
        }
        finally
        {
            await PostgresIntegrationTestData.DeleteProjectAsync(conn, project, ct);
        }
    }

    private static async Task<bool?> TryHasRequiredSchemaAsync(NpgsqlConnectionFactory factory, CancellationToken ct)
    {
        try
        {
            await using var conn = factory.Create();
            await conn.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand(
                "SELECT to_regclass('public.projects') IS NOT NULL AND to_regclass('public.webhook_events') IS NOT NULL;",
                conn);

            var result = await cmd.ExecuteScalarAsync(ct);
            return result is true;
        }
        catch (NpgsqlException)
        {
            return null;
        }
    }

    private static async Task<int> GetAttemptCountAsync(
        NpgsqlConnection conn,
        Guid projectId,
        string provider,
        string eventId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT attempt_count FROM webhook_events WHERE project_id = @project_id AND provider = @provider AND event_id = @event_id;",
            conn);

        cmd.Parameters.AddWithValue("project_id", projectId);
        cmd.Parameters.AddWithValue("provider", provider);
        cmd.Parameters.AddWithValue("event_id", eventId);

        var result = await cmd.ExecuteScalarAsync(ct);

        return result switch
        {
            int count => count,
            _ => throw new InvalidOperationException("Failed to read attempt_count for webhook event.")
        };
    }

    private static async Task<string> GetStatusAsync(
        NpgsqlConnection conn,
        Guid projectId,
        string provider,
        string eventId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT status FROM webhook_events WHERE project_id = @project_id AND provider = @provider AND event_id = @event_id;",
            conn);

        cmd.Parameters.AddWithValue("project_id", projectId);
        cmd.Parameters.AddWithValue("provider", provider);
        cmd.Parameters.AddWithValue("event_id", eventId);

        var result = await cmd.ExecuteScalarAsync(ct);

        return result switch
        {
            string status => status,
            _ => throw new InvalidOperationException("Failed to read status for webhook event.")
        };
    }

    private static async Task<DateTimeOffset?> GetProcessedAtAsync(
        NpgsqlConnection conn,
        Guid projectId,
        string provider,
        string eventId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT processed_at FROM webhook_events WHERE project_id = @project_id AND provider = @provider AND event_id = @event_id;",
            conn);

        cmd.Parameters.AddWithValue("project_id", projectId);
        cmd.Parameters.AddWithValue("provider", provider);
        cmd.Parameters.AddWithValue("event_id", eventId);

        var result = await cmd.ExecuteScalarAsync(ct);

        return result switch
        {
            DBNull => null,
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            DateTimeOffset dto => dto,
            _ => throw new InvalidOperationException("Failed to read processed_at for webhook event.")
        };
    }

    private static async Task<DateTimeOffset> GetReceivedAtAsync(
        NpgsqlConnection conn,
        Guid projectId,
        string provider,
        string eventId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT received_at FROM webhook_events WHERE project_id = @project_id AND provider = @provider AND event_id = @event_id;",
            conn);

        cmd.Parameters.AddWithValue("project_id", projectId);
        cmd.Parameters.AddWithValue("provider", provider);
        cmd.Parameters.AddWithValue("event_id", eventId);

        var result = await cmd.ExecuteScalarAsync(ct);

        return result switch
        {
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            DateTimeOffset dto => dto,
            _ => throw new InvalidOperationException("Failed to read received_at for webhook event.")
        };
    }

    private static string? GetTestConnectionString()
        => Environment.GetEnvironmentVariable("SUBTRU_TEST_DB_CONNECTION");
}
