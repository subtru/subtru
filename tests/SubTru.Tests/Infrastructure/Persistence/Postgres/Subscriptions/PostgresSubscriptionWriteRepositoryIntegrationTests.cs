using Npgsql;
using SubTru.Domain.Subscriptions;
using SubTru.Infrastructure.Persistence.Postgres;
using SubTru.Infrastructure.Persistence.Postgres.Subscriptions;
using SubTru.Tests.Support.Postgres;

namespace SubTru.Tests.Infrastructure.Persistence.Postgres.Subscriptions;

public sealed class PostgresSubscriptionWriteRepositoryIntegrationTests
{
    [Trait("Category", "Integration")]
    [RequiresPostgresFact]
    public async Task UpsertWithEventAsync_AllowsNullPriceIdForAppleSubscriptions()
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

        var repository = new PostgresSubscriptionWriteRepository(factory);
        var projectId = Guid.NewGuid();
        const string eventId = "evt_write_repo_null_price_123";

        var subscription = new NormalizedSubscription(
            ProjectId: projectId,
            Environment: SubscriptionEnvironment.Production,
            ExternalUserId: "user-123",
            Provider: "apple",
            ProviderSubscriptionId: "sub-apple-123",
            ProductId: "prod-monthly",
            PriceId: null,
            Status: "active",
            ProviderStatus: null,
            CurrentPeriodEnd: DateTimeOffset.UtcNow.AddDays(30),
            AccessEndsAt: DateTimeOffset.UtcNow.AddDays(30),
            CancelAtPeriodEnd: false);

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectId, ct);
        await PostgresIntegrationTestData.InsertProjectAsync(conn, projectId, "it-subscription-null-price", ct);

        try
        {
            var processed = await repository.UpsertWithEventAsync(subscription, eventId, ct);
            var subscriptionStatus = await GetSubscriptionStatusAsync(conn, projectId, subscription.Provider, subscription.ProviderSubscriptionId, ct);
            var providerStatus = await GetProviderStatusAsync(conn, projectId, subscription.Provider, subscription.ProviderSubscriptionId, ct);

            Assert.Equal(SubscriptionWriteOutcome.Applied, processed);
            Assert.Equal("active", subscriptionStatus);
            Assert.Null(providerStatus);
        }
        finally
        {
            await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectId, ct);
        }
    }

    [Trait("Category", "Integration")]
    [RequiresPostgresFact]
    public async Task UpsertWithEventAsync_ReturnsFalseForDuplicateEventAndDoesNotCreateSecondSubscriptionWrite()
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

        var repository = new PostgresSubscriptionWriteRepository(factory);
        var projectId = Guid.NewGuid();
        const string eventId = "evt_write_repo_duplicate_123";

        var subscription = new NormalizedSubscription(
            ProjectId: projectId,
            Environment: SubscriptionEnvironment.Production,
            ExternalUserId: "user-123",
            Provider: "apple",
            ProviderSubscriptionId: "sub-123",
            ProductId: "prod-monthly",
            PriceId: "price-monthly",
            Status: "active",
            ProviderStatus: null,
            CurrentPeriodEnd: DateTimeOffset.UtcNow.AddDays(30),
            AccessEndsAt: DateTimeOffset.UtcNow.AddDays(30),
            CancelAtPeriodEnd: false);

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectId, ct);
        await PostgresIntegrationTestData.InsertProjectAsync(conn, projectId, "it-subscription-write", ct);

        try
        {
            var firstProcessed = await repository.UpsertWithEventAsync(subscription, eventId, ct);
            var secondProcessed = await repository.UpsertWithEventAsync(subscription with { Status = "canceled" }, eventId, ct);
            var subscriptionStatus = await GetSubscriptionStatusAsync(conn, projectId, subscription.Provider, subscription.ProviderSubscriptionId, ct);
            var processedEventCount = await CountProcessedEventsAsync(conn, projectId, subscription.Provider, eventId, ct);

            Assert.Equal(SubscriptionWriteOutcome.Applied, firstProcessed);
            Assert.Equal(SubscriptionWriteOutcome.Duplicate, secondProcessed);
            Assert.Equal("active", subscriptionStatus);
            Assert.Equal(1, processedEventCount);
        }
        finally
        {
            await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectId, ct);
        }
    }

    [Trait("Category", "Integration")]
    [RequiresPostgresFact]
    public async Task UpsertWithEventAsync_PersistsProviderStatusWhenSupplied()
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

        var repository = new PostgresSubscriptionWriteRepository(factory);
        var projectId = Guid.NewGuid();
        var periodEnd = DateTimeOffset.UtcNow.AddDays(30);
        var subscription = new NormalizedSubscription(
            ProjectId: projectId,
            Environment: SubscriptionEnvironment.Production,
            ExternalUserId: "user-123",
            Provider: "google",
            ProviderSubscriptionId: "purchase-token-provider-status",
            ProductId: "monthly_premium",
            PriceId: "monthly-base-plan",
            Status: "active",
            ProviderStatus: "SUBSCRIPTION_STATE_ACTIVE",
            CurrentPeriodEnd: periodEnd,
            AccessEndsAt: periodEnd,
            CancelAtPeriodEnd: false);

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);
        await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectId, ct);
        await PostgresIntegrationTestData.InsertProjectAsync(conn, projectId, "it-subscription-provider-status", ct);

        try
        {
            var processed = await repository.UpsertWithEventAsync(
                subscription,
                "evt_write_repo_provider_status",
                ct);
            var providerStatus = await GetProviderStatusAsync(
                conn,
                projectId,
                subscription.Provider,
                subscription.ProviderSubscriptionId,
                ct);

            Assert.Equal(SubscriptionWriteOutcome.Applied, processed);
            Assert.Equal("SUBSCRIPTION_STATE_ACTIVE", providerStatus);
        }
        finally
        {
            await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectId, ct);
        }
    }

    [Trait("Category", "Integration")]
    [RequiresPostgresFact]
    public async Task UpsertWithEventAsync_PersistsKnownAndUnknownAccessEnds()
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

        var repository = new PostgresSubscriptionWriteRepository(factory);
        var projectId = Guid.NewGuid();
        var knownAccessEnd = DateTimeOffset.FromUnixTimeSeconds(
            DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds());

        var unknownEndSubscription = new NormalizedSubscription(
            ProjectId: projectId,
            Environment: SubscriptionEnvironment.Production,
            ExternalUserId: "user-123",
            Provider: "apple",
            ProviderSubscriptionId: "original-transaction-123",
            ProductId: "monthly_premium",
            PriceId: null,
            Status: "billing_grace",
            ProviderStatus: "4",
            CurrentPeriodEnd: DateTimeOffset.UtcNow.AddDays(30),
            AccessEndsAt: null,
            CancelAtPeriodEnd: false);
        var knownEndSubscription = unknownEndSubscription with
        {
            ProviderSubscriptionId = "original-transaction-456",
            Status = "active",
            ProviderStatus = "1",
            CurrentPeriodEnd = knownAccessEnd,
            AccessEndsAt = knownAccessEnd
        };

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectId, ct);
        await PostgresIntegrationTestData.InsertProjectAsync(conn, projectId, "it-subscription-provider-status", ct);

        try
        {
            var unknownProcessed = await repository.UpsertWithEventAsync(
                unknownEndSubscription,
                "evt_write_repo_unknown_access_end",
                ct);
            var knownProcessed = await repository.UpsertWithEventAsync(
                knownEndSubscription,
                "evt_write_repo_known_access_end",
                ct);

            var unknownAccessEnd = await GetAccessEndsAtAsync(
                conn,
                projectId,
                unknownEndSubscription.Provider,
                unknownEndSubscription.ProviderSubscriptionId,
                ct);
            var storedKnownAccessEnd = await GetAccessEndsAtAsync(
                conn,
                projectId,
                knownEndSubscription.Provider,
                knownEndSubscription.ProviderSubscriptionId,
                ct);
            Assert.Equal(SubscriptionWriteOutcome.Applied, unknownProcessed);
            Assert.Equal(SubscriptionWriteOutcome.Applied, knownProcessed);
            Assert.Null(unknownAccessEnd);
            Assert.Equal(knownAccessEnd, storedKnownAccessEnd);
        }
        finally
        {
            await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectId, ct);
        }
    }

    [Trait("Category", "Integration")]
    [RequiresPostgresFact]
    public async Task UpsertWithEventAsync_AppliesNewerProviderEventsAndIgnoresOlderOnes()
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

        var repository = new PostgresSubscriptionWriteRepository(factory);
        var projectId = Guid.NewGuid();
        var newerEventCreatedAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var olderEventCreatedAt = newerEventCreatedAt.AddMinutes(-5);
        var latestEventCreatedAt = newerEventCreatedAt.AddMinutes(5);
        var accessEndsAt = DateTimeOffset.UtcNow.AddDays(10);

        var newerSubscription = new NormalizedSubscription(
            ProjectId: projectId,
            Environment: SubscriptionEnvironment.Production,
            ExternalUserId: "user-123",
            Provider: "stripe",
            ProviderSubscriptionId: "sub-ordering-123",
            ProductId: "prod-monthly",
            PriceId: "price-monthly",
            Status: "canceled",
            ProviderStatus: "canceled",
            CurrentPeriodEnd: accessEndsAt,
            AccessEndsAt: accessEndsAt,
            CancelAtPeriodEnd: true,
            ProviderEventCreatedAt: newerEventCreatedAt);

        var olderSubscription = newerSubscription with
        {
            Status = "active",
            ProviderStatus = "active",
            CancelAtPeriodEnd = false,
            ProviderEventCreatedAt = olderEventCreatedAt
        };
        var latestSubscription = newerSubscription with
        {
            Status = "trialing",
            ProviderStatus = "trialing",
            CancelAtPeriodEnd = false,
            ProviderEventCreatedAt = latestEventCreatedAt
        };

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectId, ct);
        await PostgresIntegrationTestData.InsertProjectAsync(conn, projectId, "it-subscription-event-ordering", ct);

        try
        {
            var newerProcessed = await repository.UpsertWithEventAsync(
                newerSubscription,
                eventId: "evt_newer_subscription_state",
                ct);
            var olderProcessed = await repository.UpsertWithEventAsync(
                olderSubscription,
                eventId: "evt_older_subscription_state",
                ct);

            var status = await GetSubscriptionStatusAsync(
                conn,
                projectId,
                newerSubscription.Provider,
                newerSubscription.ProviderSubscriptionId,
                ct);
            var storedEventCreatedAt = await GetProviderEventCreatedAtAsync(
                conn,
                projectId,
                newerSubscription.Provider,
                newerSubscription.ProviderSubscriptionId,
                ct);
            Assert.Equal(SubscriptionWriteOutcome.Applied, newerProcessed);
            Assert.Equal(SubscriptionWriteOutcome.Stale, olderProcessed);
            Assert.Equal("canceled", status);
            Assert.Equal(newerEventCreatedAt, storedEventCreatedAt);

            var latestProcessed = await repository.UpsertWithEventAsync(
                latestSubscription,
                eventId: "evt_latest_subscription_state",
                ct);
            status = await GetSubscriptionStatusAsync(
                conn,
                projectId,
                newerSubscription.Provider,
                newerSubscription.ProviderSubscriptionId,
                ct);
            storedEventCreatedAt = await GetProviderEventCreatedAtAsync(
                conn,
                projectId,
                newerSubscription.Provider,
                newerSubscription.ProviderSubscriptionId,
                ct);
            Assert.Equal(SubscriptionWriteOutcome.Applied, latestProcessed);
            Assert.Equal("trialing", status);
            Assert.Equal(latestEventCreatedAt, storedEventCreatedAt);

            var timestampLessProcessed = await repository.UpsertWithEventAsync(
                latestSubscription with
                {
                    Status = "expired",
                    ProviderStatus = "expired",
                    ProviderEventCreatedAt = null
                },
                eventId: "evt_timestamp_less_subscription_state",
                ct);
            status = await GetSubscriptionStatusAsync(
                conn,
                projectId,
                newerSubscription.Provider,
                newerSubscription.ProviderSubscriptionId,
                ct);
            storedEventCreatedAt = await GetProviderEventCreatedAtAsync(
                conn,
                projectId,
                newerSubscription.Provider,
                newerSubscription.ProviderSubscriptionId,
                ct);

            Assert.Equal(SubscriptionWriteOutcome.Stale, timestampLessProcessed);
            Assert.Equal("trialing", status);
            Assert.Equal(latestEventCreatedAt, storedEventCreatedAt);
        }
        finally
        {
            await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectId, ct);
        }
    }

    [Trait("Category", "Integration")]
    [RequiresPostgresFact]
    public async Task UpsertWithEventAsync_ReplacesAccessEndAcrossStripeRecoveryTransitions()
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

        var repository = new PostgresSubscriptionWriteRepository(factory);
        var projectId = Guid.NewGuid();
        var firstEventAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var activeAccessEnd = firstEventAt.AddDays(30);
        var recoveredAccessEnd = firstEventAt.AddDays(60);
        var terminalAccessEnd = firstEventAt.AddDays(-1);

        var active = new NormalizedSubscription(
            ProjectId: projectId,
            Environment: SubscriptionEnvironment.Production,
            ExternalUserId: "user-123",
            Provider: "stripe",
            ProviderSubscriptionId: "sub-recovery-123",
            ProductId: "prod-monthly",
            PriceId: "price-monthly",
            Status: "active",
            ProviderStatus: "active",
            CurrentPeriodEnd: activeAccessEnd,
            AccessEndsAt: activeAccessEnd,
            CancelAtPeriodEnd: false,
            ProviderEventCreatedAt: firstEventAt);
        var pastDue = active with
        {
            Status = "billing_grace",
            ProviderStatus = "past_due",
            CurrentPeriodEnd = terminalAccessEnd,
            AccessEndsAt = null,
            ProviderEventCreatedAt = firstEventAt.AddMinutes(1)
        };
        var recovered = active with
        {
            CurrentPeriodEnd = recoveredAccessEnd,
            AccessEndsAt = recoveredAccessEnd,
            ProviderEventCreatedAt = firstEventAt.AddMinutes(2)
        };
        var unpaid = active with
        {
            Status = "expired",
            ProviderStatus = "unpaid",
            CurrentPeriodEnd = terminalAccessEnd,
            AccessEndsAt = terminalAccessEnd,
            ProviderEventCreatedAt = firstEventAt.AddMinutes(3)
        };

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);
        await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectId, ct);
        await PostgresIntegrationTestData.InsertProjectAsync(conn, projectId, "it-stripe-access-end-transitions", ct);

        try
        {
            Assert.Equal(
                SubscriptionWriteOutcome.Applied,
                await repository.UpsertWithEventAsync(active, "evt_recovery_active", ct));
            var stored = await GetSubscriptionAsync(
                conn,
                projectId,
                active.Provider,
                active.ProviderSubscriptionId,
                ct);
            Assert.Equal("active", stored.Status);
            Assert.Equal("active", stored.ProviderStatus);
            Assert.Equal(activeAccessEnd, stored.AccessEndsAt);

            Assert.Equal(
                SubscriptionWriteOutcome.Applied,
                await repository.UpsertWithEventAsync(pastDue, "evt_recovery_past_due", ct));
            stored = await GetSubscriptionAsync(
                conn,
                projectId,
                active.Provider,
                active.ProviderSubscriptionId,
                ct);
            Assert.Equal("billing_grace", stored.Status);
            Assert.Equal("past_due", stored.ProviderStatus);
            Assert.Null(stored.AccessEndsAt);

            Assert.Equal(
                SubscriptionWriteOutcome.Applied,
                await repository.UpsertWithEventAsync(recovered, "evt_recovery_active_again", ct));
            stored = await GetSubscriptionAsync(
                conn,
                projectId,
                active.Provider,
                active.ProviderSubscriptionId,
                ct);
            Assert.Equal("active", stored.Status);
            Assert.Equal("active", stored.ProviderStatus);
            Assert.Equal(recoveredAccessEnd, stored.AccessEndsAt);

            Assert.Equal(
                SubscriptionWriteOutcome.Applied,
                await repository.UpsertWithEventAsync(unpaid, "evt_recovery_unpaid", ct));
            stored = await GetSubscriptionAsync(
                conn,
                projectId,
                active.Provider,
                active.ProviderSubscriptionId,
                ct);
            Assert.Equal("expired", stored.Status);
            Assert.Equal("unpaid", stored.ProviderStatus);
            Assert.Equal(terminalAccessEnd, stored.AccessEndsAt);
        }
        finally
        {
            await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectId, ct);
        }
    }

    [Trait("Category", "Integration")]
    [RequiresPostgresFact]
    public async Task UpsertGoogleWithEventAsync_ExpiresLinkedPurchaseTokenInSameTransaction()
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

        var repository = new PostgresSubscriptionWriteRepository(factory);
        var projectId = Guid.NewGuid();
        const string oldPurchaseToken = "old-purchase-token-123";
        const string newPurchaseToken = "new-purchase-token-456";
        const string protectedPurchaseToken = "protected-purchase-token-789";
        var protectedEventCreatedAt = DateTimeOffset.UtcNow;

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectId, ct);
        await PostgresIntegrationTestData.InsertProjectAsync(conn, projectId, "it-google-linked-token", ct);

        try
        {
            await repository.UpsertWithEventAsync(
                new NormalizedSubscription(
                    ProjectId: projectId,
                    Environment: SubscriptionEnvironment.Production,
                    ExternalUserId: "user-123",
                    Provider: "google",
                    ProviderSubscriptionId: oldPurchaseToken,
                    ProductId: "monthly_premium",
                    PriceId: "monthly-base",
                    Status: "active",
                    ProviderStatus: "SUBSCRIPTION_STATE_ACTIVE",
                    CurrentPeriodEnd: DateTimeOffset.UtcNow.AddDays(30),
                    AccessEndsAt: DateTimeOffset.UtcNow.AddDays(30),
                    CancelAtPeriodEnd: false),
                eventId: "evt_google_old_token",
                ct);

            var processed = await repository.UpsertGoogleWithEventAsync(
                new NormalizedSubscription(
                    ProjectId: projectId,
                    Environment: SubscriptionEnvironment.Production,
                    ExternalUserId: "user-123",
                    Provider: "google",
                    ProviderSubscriptionId: newPurchaseToken,
                    ProductId: "monthly_premium_plus",
                    PriceId: "monthly-plus-base",
                    Status: "active",
                    ProviderStatus: "SUBSCRIPTION_STATE_ACTIVE",
                    CurrentPeriodEnd: DateTimeOffset.UtcNow.AddDays(30),
                    AccessEndsAt: DateTimeOffset.UtcNow.AddDays(30),
                    CancelAtPeriodEnd: false),
                eventId: "evt_google_new_token",
                linkedPurchaseToken: oldPurchaseToken,
                ct);

            var oldStatus = await GetSubscriptionStatusAsync(conn, projectId, "google", oldPurchaseToken, ct);
            var newStatus = await GetSubscriptionStatusAsync(conn, projectId, "google", newPurchaseToken, ct);

            Assert.Equal(SubscriptionWriteOutcome.Applied, processed);
            Assert.Equal("expired", oldStatus);
            Assert.Equal("active", newStatus);

            await repository.UpsertWithEventAsync(
                new NormalizedSubscription(
                    ProjectId: projectId,
                    Environment: SubscriptionEnvironment.Production,
                    ExternalUserId: "user-123",
                    Provider: "google",
                    ProviderSubscriptionId: protectedPurchaseToken,
                    ProductId: "monthly_protected",
                    PriceId: "monthly-protected-base",
                    Status: "active",
                    ProviderStatus: "SUBSCRIPTION_STATE_ACTIVE",
                    CurrentPeriodEnd: DateTimeOffset.UtcNow.AddDays(30),
                    AccessEndsAt: DateTimeOffset.UtcNow.AddDays(30),
                    CancelAtPeriodEnd: false,
                    ProviderEventCreatedAt: protectedEventCreatedAt),
                eventId: "evt_google_protected_token",
                ct);

            var timestampLessProcessed = await repository.UpsertGoogleWithEventAsync(
                new NormalizedSubscription(
                    ProjectId: projectId,
                    Environment: SubscriptionEnvironment.Production,
                    ExternalUserId: "user-123",
                    Provider: "google",
                    ProviderSubscriptionId: "timestamp-less-new-token",
                    ProductId: "monthly_new",
                    PriceId: "monthly-new-base",
                    Status: "active",
                    ProviderStatus: "SUBSCRIPTION_STATE_ACTIVE",
                    CurrentPeriodEnd: DateTimeOffset.UtcNow.AddDays(30),
                    AccessEndsAt: DateTimeOffset.UtcNow.AddDays(30),
                    CancelAtPeriodEnd: false),
                eventId: "evt_google_timestamp_less_link",
                linkedPurchaseToken: protectedPurchaseToken,
                ct);

            var protectedStatus = await GetSubscriptionStatusAsync(
                conn,
                projectId,
                "google",
                protectedPurchaseToken,
                ct);
            var timestampLessNewStatus = await GetSubscriptionStatusAsync(
                conn,
                projectId,
                "google",
                "timestamp-less-new-token",
                ct);

            Assert.Equal(SubscriptionWriteOutcome.Applied, timestampLessProcessed);
            Assert.Equal("active", protectedStatus);
            Assert.Equal("active", timestampLessNewStatus);
        }
        finally
        {
            await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectId, ct);
        }
    }

    [Trait("Category", "Integration")]
    [RequiresPostgresFact]
    public async Task UpsertGoogleWithEventAsync_SupersessionCapsAccessWithoutMovingEarlierEndForward()
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

        var repository = new PostgresSubscriptionWriteRepository(factory);
        var projectId = Guid.NewGuid();
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var alreadyEndedAt = now.AddDays(-2);
        var futureEnd = now.AddDays(30);

        var linkedSubscription = new NormalizedSubscription(
            ProjectId: projectId,
            Environment: SubscriptionEnvironment.Production,
            ExternalUserId: "user-123",
            Provider: "google",
            ProviderSubscriptionId: "linked-already-ended",
            ProductId: "monthly_premium",
            PriceId: "monthly-base",
            Status: "active",
            ProviderStatus: "SUBSCRIPTION_STATE_ACTIVE",
            CurrentPeriodEnd: futureEnd,
            AccessEndsAt: alreadyEndedAt,
            CancelAtPeriodEnd: false);
        var futureLinkedSubscription = linkedSubscription with
        {
            ProviderSubscriptionId = "linked-future-end",
            AccessEndsAt = futureEnd
        };

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);
        await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectId, ct);
        await PostgresIntegrationTestData.InsertProjectAsync(conn, projectId, "it-google-supersession-access-end", ct);

        try
        {
            await repository.UpsertWithEventAsync(linkedSubscription, "evt_linked_already_ended", ct);
            await repository.UpsertWithEventAsync(futureLinkedSubscription, "evt_linked_future_end", ct);

            var replacement = linkedSubscription with
            {
                ProviderSubscriptionId = "replacement-for-ended",
                CurrentPeriodEnd = futureEnd.AddDays(30),
                AccessEndsAt = futureEnd.AddDays(30)
            };
            await repository.UpsertGoogleWithEventAsync(
                replacement,
                "evt_replace_already_ended",
                linkedSubscription.ProviderSubscriptionId,
                ct);

            var alreadyEnded = await GetSubscriptionAsync(
                conn,
                projectId,
                linkedSubscription.Provider,
                linkedSubscription.ProviderSubscriptionId,
                ct);
            Assert.Equal("expired", alreadyEnded.Status);
            Assert.Equal(alreadyEndedAt, alreadyEnded.AccessEndsAt);

            var beforeFutureSupersession = DateTimeOffset.UtcNow;
            await repository.UpsertGoogleWithEventAsync(
                replacement with { ProviderSubscriptionId = "replacement-for-future" },
                "evt_replace_future_end",
                futureLinkedSubscription.ProviderSubscriptionId,
                ct);
            var afterFutureSupersession = DateTimeOffset.UtcNow;

            var futureEnded = await GetSubscriptionAsync(
                conn,
                projectId,
                futureLinkedSubscription.Provider,
                futureLinkedSubscription.ProviderSubscriptionId,
                ct);
            Assert.Equal("expired", futureEnded.Status);
            Assert.NotNull(futureEnded.AccessEndsAt);
            Assert.InRange(
                futureEnded.AccessEndsAt.Value,
                beforeFutureSupersession.AddSeconds(-1),
                afterFutureSupersession.AddSeconds(1));
        }
        finally
        {
            await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectId, ct);
        }
    }

    [Trait("Category", "Integration")]
    [RequiresPostgresFact]
    public async Task UpsertGoogleManualAsync_PreventsOlderWebhookFromOverwritingRefreshedState()
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

        var repository = new PostgresSubscriptionWriteRepository(factory);
        var projectId = Guid.NewGuid();
        const string purchaseToken = "manual-refresh-token-123";
        const string linkedPurchaseToken = "manual-refresh-legacy-token-456";
        var beforeManualRefresh = DateTimeOffset.UtcNow;

        var refreshedSubscription = new NormalizedSubscription(
            ProjectId: projectId,
            Environment: SubscriptionEnvironment.Production,
            ExternalUserId: "user-123",
            Provider: "google",
            ProviderSubscriptionId: purchaseToken,
            ProductId: "monthly_premium",
            PriceId: "monthly-base",
            Status: "active",
            ProviderStatus: "SUBSCRIPTION_STATE_ACTIVE",
            CurrentPeriodEnd: DateTimeOffset.UtcNow.AddDays(30),
            AccessEndsAt: DateTimeOffset.UtcNow.AddDays(30),
            CancelAtPeriodEnd: false);

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectId, ct);
        await PostgresIntegrationTestData.InsertProjectAsync(conn, projectId, "it-google-manual-ordering", ct);

        try
        {
            await repository.UpsertWithEventAsync(
                refreshedSubscription with
                {
                    ProviderSubscriptionId = linkedPurchaseToken,
                    ProviderEventCreatedAt = beforeManualRefresh.AddMinutes(-10)
                },
                eventId: "evt_google_legacy_before_manual_refresh",
                ct);

            await repository.UpsertGoogleManualAsync(
                refreshedSubscription,
                linkedPurchaseToken,
                ct);
            var afterManualRefresh = DateTimeOffset.UtcNow;
            var manualRefreshTimestamp = await GetProviderEventCreatedAtAsync(
                conn,
                projectId,
                "google",
                purchaseToken,
                ct);

            var delayedWebhookProcessed = await repository.UpsertGoogleWithEventAsync(
                refreshedSubscription with
                {
                    Status = "canceled",
                    ProviderStatus = "SUBSCRIPTION_STATE_CANCELED",
                    CurrentPeriodEnd = DateTimeOffset.UtcNow.AddDays(1),
                    CancelAtPeriodEnd = true,
                    ProviderEventCreatedAt = beforeManualRefresh.AddMinutes(-5)
                },
                eventId: "evt_google_delayed_after_manual_refresh",
                linkedPurchaseToken: null,
                ct);
            var delayedLinkedWebhookProcessed = await repository.UpsertGoogleWithEventAsync(
                refreshedSubscription with
                {
                    ProviderSubscriptionId = linkedPurchaseToken,
                    ProviderEventCreatedAt = beforeManualRefresh.AddMinutes(-5)
                },
                eventId: "evt_google_delayed_linked_after_manual_refresh",
                linkedPurchaseToken: null,
                ct);

            var status = await GetSubscriptionStatusAsync(conn, projectId, "google", purchaseToken, ct);
            var linkedStatus = await GetSubscriptionStatusAsync(
                conn,
                projectId,
                "google",
                linkedPurchaseToken,
                ct);
            var storedTimestamp = await GetProviderEventCreatedAtAsync(
                conn,
                projectId,
                "google",
                purchaseToken,
                ct);

            Assert.Equal(SubscriptionWriteOutcome.Stale, delayedWebhookProcessed);
            Assert.Equal(SubscriptionWriteOutcome.Stale, delayedLinkedWebhookProcessed);
            Assert.NotNull(manualRefreshTimestamp);
            Assert.InRange(manualRefreshTimestamp.Value, beforeManualRefresh, afterManualRefresh);
            Assert.Equal("active", status);
            Assert.Equal("expired", linkedStatus);
            Assert.Equal(manualRefreshTimestamp, storedTimestamp);
        }
        finally
        {
            await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectId, ct);
        }
    }

    [Trait("Category", "Integration")]
    [RequiresPostgresFact]
    public async Task UpsertGoogleWithEventAsync_ReturnsFalseForDuplicateGoogleMessageId()
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

        var repository = new PostgresSubscriptionWriteRepository(factory);
        var projectId = Guid.NewGuid();
        const string eventId = "evt_google_duplicate_message";

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectId, ct);
        await PostgresIntegrationTestData.InsertProjectAsync(conn, projectId, "it-google-duplicate-message", ct);

        try
        {
            var firstProcessed = await repository.UpsertGoogleWithEventAsync(
                new NormalizedSubscription(
                    ProjectId: projectId,
                    Environment: SubscriptionEnvironment.Production,
                    ExternalUserId: "user-123",
                    Provider: "google",
                    ProviderSubscriptionId: "purchase-token-123",
                    ProductId: "monthly_premium",
                    PriceId: "monthly-base",
                    Status: "active",
                    ProviderStatus: "SUBSCRIPTION_STATE_ACTIVE",
                    CurrentPeriodEnd: DateTimeOffset.UtcNow.AddDays(30),
                    AccessEndsAt: DateTimeOffset.UtcNow.AddDays(30),
                    CancelAtPeriodEnd: false),
                eventId,
                linkedPurchaseToken: null,
                ct);

            var secondProcessed = await repository.UpsertGoogleWithEventAsync(
                new NormalizedSubscription(
                    ProjectId: projectId,
                    Environment: SubscriptionEnvironment.Production,
                    ExternalUserId: "user-123",
                    Provider: "google",
                    ProviderSubscriptionId: "purchase-token-123",
                    ProductId: "monthly_premium",
                    PriceId: "monthly-base",
                    Status: "canceled",
                    ProviderStatus: "SUBSCRIPTION_STATE_CANCELED",
                    CurrentPeriodEnd: DateTimeOffset.UtcNow.AddDays(1),
                    AccessEndsAt: DateTimeOffset.UtcNow.AddDays(1),
                    CancelAtPeriodEnd: true),
                eventId,
                linkedPurchaseToken: null,
                ct);

            var status = await GetSubscriptionStatusAsync(conn, projectId, "google", "purchase-token-123", ct);

            Assert.Equal(SubscriptionWriteOutcome.Applied, firstProcessed);
            Assert.Equal(SubscriptionWriteOutcome.Duplicate, secondProcessed);
            Assert.Equal("active", status);
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
                """
                SELECT to_regclass('public.projects') IS NOT NULL
                    AND to_regclass('public.subscriptions') IS NOT NULL
                    AND to_regclass('public.processed_events') IS NOT NULL;
                """,
                conn);

            var result = await cmd.ExecuteScalarAsync(ct);
            return result is true;
        }
        catch (NpgsqlException)
        {
            return null;
        }
    }

    private sealed record StoredSubscription(
        string Status,
        string? ProviderStatus,
        DateTimeOffset? AccessEndsAt);

    private static async Task<StoredSubscription> GetSubscriptionAsync(
        NpgsqlConnection conn,
        Guid projectId,
        string provider,
        string providerSubscriptionId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT status, provider_status, access_ends_at
            FROM subscriptions
            WHERE project_id = @project_id
              AND provider = @provider
              AND provider_subscription_id = @provider_subscription_id;
            """,
            conn);

        cmd.Parameters.AddWithValue("project_id", projectId);
        cmd.Parameters.AddWithValue("provider", provider);
        cmd.Parameters.AddWithValue("provider_subscription_id", providerSubscriptionId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException("Expected subscription row was not found.");
        }

        return new StoredSubscription(
            Status: reader.GetString(0),
            ProviderStatus: reader.IsDBNull(1) ? null : reader.GetString(1),
            AccessEndsAt: reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2));
    }

    private static async Task<string?> GetSubscriptionStatusAsync(
        NpgsqlConnection conn,
        Guid projectId,
        string provider,
        string providerSubscriptionId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT status
            FROM subscriptions
            WHERE project_id = @project_id
                AND provider = @provider
                AND provider_subscription_id = @provider_subscription_id;
            """,
            conn);

        cmd.Parameters.AddWithValue("project_id", projectId);
        cmd.Parameters.AddWithValue("provider", provider);
        cmd.Parameters.AddWithValue("provider_subscription_id", providerSubscriptionId);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    private static async Task<string?> GetProviderStatusAsync(
        NpgsqlConnection conn,
        Guid projectId,
        string provider,
        string providerSubscriptionId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT provider_status FROM subscriptions WHERE project_id = @project_id AND provider = @provider AND provider_subscription_id = @provider_subscription_id;",
            conn);

        cmd.Parameters.AddWithValue("project_id", projectId);
        cmd.Parameters.AddWithValue("provider", provider);
        cmd.Parameters.AddWithValue("provider_subscription_id", providerSubscriptionId);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    private static async Task<DateTimeOffset?> GetAccessEndsAtAsync(
        NpgsqlConnection conn,
        Guid projectId,
        string provider,
        string providerSubscriptionId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT access_ends_at FROM subscriptions WHERE project_id = @project_id AND provider = @provider AND provider_subscription_id = @provider_subscription_id;",
            conn);

        cmd.Parameters.AddWithValue("project_id", projectId);
        cmd.Parameters.AddWithValue("provider", provider);
        cmd.Parameters.AddWithValue("provider_subscription_id", providerSubscriptionId);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result switch
        {
            null or DBNull => null,
            DateTimeOffset value => value,
            DateTime value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException("Unexpected access_ends_at value type.")
        };
    }

    private static async Task<DateTimeOffset?> GetProviderEventCreatedAtAsync(
        NpgsqlConnection conn,
        Guid projectId,
        string provider,
        string providerSubscriptionId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT provider_event_created_at
            FROM subscriptions
            WHERE project_id = @project_id
              AND provider = @provider
              AND provider_subscription_id = @provider_subscription_id;
            """,
            conn);

        cmd.Parameters.AddWithValue("project_id", projectId);
        cmd.Parameters.AddWithValue("provider", provider);
        cmd.Parameters.AddWithValue("provider_subscription_id", providerSubscriptionId);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result switch
        {
            DateTimeOffset value => value,
            DateTime value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
            _ => null
        };
    }

    private static async Task<int> CountProcessedEventsAsync(
        NpgsqlConnection conn,
        Guid projectId,
        string provider,
        string eventId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT COUNT(*)
            FROM processed_events
            WHERE project_id = @project_id AND provider = @provider
                AND event_id = @event_id;
            """,
            conn);

        cmd.Parameters.AddWithValue("project_id", projectId);
        cmd.Parameters.AddWithValue("provider", provider);
        cmd.Parameters.AddWithValue("event_id", eventId);

        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    private static string? GetTestConnectionString()
        => Environment.GetEnvironmentVariable("SUBTRU_TEST_DB_CONNECTION");
}
