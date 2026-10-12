using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stripe;
using SubTru.Api.Features.Webhooks.Stripe;
using SubTru.Domain.Subscriptions;
using SubTru.Infrastructure.Providers.Stripe;
using SubTru.Infrastructure.Persistence.Postgres.Subscriptions;
using SubTru.Infrastructure.Persistence.Postgres.ProviderConfiguration;
using SubTru.Infrastructure.Persistence.Postgres.WebhookEvents;
using SubTru.Infrastructure.Persistence.Postgres.WebhookKeys;

namespace SubTru.Tests.Features.Webhooks.Stripe;

public sealed class StripeWebhookEndpointTests : IClassFixture<StripeWebhookWebApplicationFactory>
{
    private const string WebhookPath = "/webhooks/stripe/subtru_wh_test.secret";
    private const string SigningSecret = "whsec_test";
    private readonly StripeWebhookWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public StripeWebhookEndpointTests(StripeWebhookWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PostStripeWebhook_RecentValidSignature_ReturnsOk()
    {
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-4).ToUnixTimeSeconds();
        using var request = CreateRequest(
            eventId: "evt_recent_valid_signature",
            apiVersion: StripeConfiguration.ApiVersion,
            timestamp: timestamp);

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostStripeWebhook_OversizedPayload_ReturnsPayloadTooLarge()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, WebhookPath)
        {
            Content = new StringContent(
                new string('x', WebhookRequestBodyReader.MaxBodySizeBytes + 1),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add("Stripe-Signature", "t=0,v1=invalid");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task PostStripeWebhook_SignatureOlderThanFiveMinutes_ReturnsBadRequest()
    {
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-6).ToUnixTimeSeconds();
        using var request = CreateRequest(
            eventId: "evt_expired_signature",
            apiVersion: StripeConfiguration.ApiVersion,
            timestamp: timestamp);

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_stripe_signature", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PostStripeWebhook_ApiVersionMismatch_ReturnsOkWithSpecificErrorAndLogsFailure()
    {
        const string eventId = "evt_api_version_mismatch";
        const string mismatchedApiVersion = "2000-01-01";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var request = CreateRequest(eventId, mismatchedApiVersion, timestamp);

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("stripe_api_version_mismatch", body.GetProperty("error").GetString());
        Assert.Equal(mismatchedApiVersion, body.GetProperty("eventApiVersion").GetString());
        Assert.Equal(StripeConfiguration.ApiVersion, body.GetProperty("expectedApiVersion").GetString());

        Assert.Contains(
            _factory.EventLog.Writes,
            write => write.EventId == eventId
                && write.Status == WebhookEventStatus.Failed
                && write.ErrorCode == "stripe_api_version_mismatch");
    }

    [Fact]
    public async Task PostStripeWebhook_SubscriptionEventWithUnexpectedObject_LogsFailureAndReturnsBadRequest()
    {
        var eventId = $"evt_bad_subscription_object_{Guid.NewGuid():N}";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var request = CreateRequest(
            eventId,
            StripeConfiguration.ApiVersion,
            timestamp,
            "customer.subscription.created");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unexpected_event_object", body.GetProperty("error").GetString());
        Assert.Contains(
            _factory.EventLog.Writes,
            write => write.EventId == eventId &&
                write.Status == WebhookEventStatus.Failed &&
                write.ErrorCode == "unexpected_event_object");
    }

    [Fact]
    public async Task PostStripeWebhook_AlreadyProcessedSubscription_ShortCircuitsCoreWrite()
    {
        using var duplicateFactory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IProcessedEventRepository>();
                services.RemoveAll<ISubscriptionWriteRepository>();
                services.AddSingleton<IProcessedEventRepository, AlwaysProcessedEventRepository>();
                services.AddSingleton<ISubscriptionWriteRepository, ThrowingSubscriptionWriteRepository>();
            }));
        using var client = duplicateFactory.CreateClient();
        var eventId = $"evt_duplicate_{Guid.NewGuid():N}";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var request = CreateRequest(
            eventId,
            StripeConfiguration.ApiVersion,
            timestamp,
            "customer.subscription.created",
            SubscriptionObject(Guid.NewGuid(), timestamp));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            _factory.EventLog.Writes,
            write => write.EventId == eventId &&
                write.Status == WebhookEventStatus.Duplicate);
    }

    [Fact]
    public async Task PostStripeWebhook_ValidSubscription_LogsProviderSubscriptionId()
    {
        var eventId = $"evt_provider_subscription_id_{Guid.NewGuid():N}";
        var subscriptionId = $"sub_{Guid.NewGuid():N}";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var request = CreateRequest(
            eventId,
            StripeConfiguration.ApiVersion,
            timestamp,
            "customer.subscription.created",
            SubscriptionObject(Guid.NewGuid(), timestamp, subscriptionId));

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            _factory.EventLog.Writes,
            write => write.EventId == eventId &&
                write.Status == WebhookEventStatus.Success &&
                write.ProviderSubscriptionId == subscriptionId);
    }

    [Fact]
    public async Task PostStripeWebhook_SubscriptionUpdated_LogsChange()
    {
        var eventId = $"evt_cancellation_scheduled_{Guid.NewGuid():N}";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var request = CreateRequest(
            eventId,
            StripeConfiguration.ApiVersion,
            timestamp,
            "customer.subscription.updated",
            SubscriptionObject(Guid.NewGuid(), timestamp, cancelAtPeriodEnd: true),
            new { cancel_at_period_end = false });

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            _factory.EventLog.Writes,
            write => write.EventId == eventId
                && write.Status == WebhookEventStatus.Success
                && write.EventChange == StripeSubscriptionUpdateChanges.CancellationScheduled);
    }

    [Fact]
    public async Task PostStripeWebhook_FlexibleBillingCancellation_LogsChange()
    {
        var eventId = $"evt_flexible_cancellation_{Guid.NewGuid():N}";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var request = CreateRequest(
            eventId,
            StripeConfiguration.ApiVersion,
            timestamp,
            "customer.subscription.updated",
            SubscriptionObject(
                Guid.NewGuid(),
                timestamp,
                cancelAt: timestamp + 86400),
            new { cancel_at = (long?)null });

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            _factory.EventLog.Writes,
            write => write.EventId == eventId
                && write.Status == WebhookEventStatus.Success
                && write.EventChange == StripeSubscriptionUpdateChanges.CancellationScheduled);
    }

    [Fact]
    public async Task PostStripeWebhook_InvoicePaid_LogsProviderSubscriptionId()
    {
        var eventId = $"evt_invoice_provider_subscription_id_{Guid.NewGuid():N}";
        var subscriptionId = $"sub_{Guid.NewGuid():N}";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var request = CreateRequest(
            eventId,
            StripeConfiguration.ApiVersion,
            timestamp,
            "invoice.paid",
            InvoiceObject(subscriptionId));

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            _factory.EventLog.Writes,
            write => write.EventId == eventId &&
                write.Status == WebhookEventStatus.Ignored &&
                write.ProviderSubscriptionId == subscriptionId);
    }

    private static HttpRequestMessage CreateRequest(
        string eventId,
        string apiVersion,
        long timestamp,
        string eventType = "customer.created",
        object? dataObject = null,
        object? previousAttributes = null)
    {
        var payload = JsonSerializer.Serialize(new
        {
            id = eventId,
            @object = "event",
            api_version = apiVersion,
            created = timestamp,
            data = new
            {
                @object = dataObject ?? new
                {
                    id = "cus_test",
                    @object = "customer"
                },
                previous_attributes = previousAttributes
            },
            livemode = false,
            pending_webhooks = 0,
            request = new
            {
                id = (string?)null,
                idempotency_key = (string?)null
            },
            type = eventType
        });
        var signature = EventUtility.ComputeSignature(
            SigningSecret,
            timestamp.ToString(CultureInfo.InvariantCulture),
            payload);

        var request = new HttpRequestMessage(HttpMethod.Post, WebhookPath)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Stripe-Signature", $"t={timestamp},v1={signature}");
        return request;
    }

    private static object SubscriptionObject(
        Guid externalUserId,
        long timestamp,
        string? subscriptionId = null,
        bool cancelAtPeriodEnd = false,
        long? cancelAt = null)
        => new
        {
            id = subscriptionId ?? $"sub_{Guid.NewGuid():N}",
            @object = "subscription",
            cancel_at = cancelAt,
            cancel_at_period_end = cancelAtPeriodEnd,
            status = "trialing",
            trial_start = timestamp,
            metadata = new Dictionary<string, string>
            {
                ["subtru_external_user_id"] = externalUserId.ToString("D")
            },
            items = new
            {
                @object = "list",
                data = new[]
                {
                    new
                    {
                        id = $"si_{Guid.NewGuid():N}",
                        @object = "subscription_item",
                        current_period_end = timestamp + 86400,
                        price = new
                        {
                            id = "price_test",
                            @object = "price",
                            product = "prod_test"
                        }
                    }
                },
                has_more = false,
                url = "/v1/subscription_items"
            }
        };

    private static object InvoiceObject(string subscriptionId)
        => new
        {
            id = $"in_{Guid.NewGuid():N}",
            @object = "invoice",
            parent = new
            {
                type = "subscription_details",
                subscription_details = new
                {
                    subscription = subscriptionId
                }
            }
        };

}
public sealed class StripeWebhookWebApplicationFactory : WebApplicationFactory<Program>
{
    internal FakeStripeWebhookEventLogRepository EventLog { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        TestWebHostConfiguration.UseAdminApiKey(builder);
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            var config = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=localhost;Port=5432;Database=subtru_test;Username=test;Password=test",
                ["API_KEY_PEPPER"] = "test-pepper",
                ["SUBTRU_ENCRYPTION_KEY"] = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                ["WebhookUrls:PublicBaseUrl"] = "https://api.test.subtru.com",
                ["AppleVerification:RootCertificateDirectory"] = TestAppleTrustStoreDirectory.DirectoryPath,
                ["SUBTRU_ADMIN_API_KEY"] = "subtru_admin_0123456789abcdef0123456789abcdef"
            };

            configBuilder.AddInMemoryCollection(config);
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IWebhookKeyRepository>();
            services.RemoveAll<IProcessedEventRepository>();
            services.RemoveAll<IWebhookEventLogRepository>();
            services.RemoveAll<ISubscriptionWriteRepository>();

            services.AddScoped<IWebhookKeyRepository, FakeStripeWebhookKeyRepository>();
            services.AddScoped<IProcessedEventRepository, FakeStripeProcessedEventRepository>();
            services.AddSingleton<IWebhookEventLogRepository>(EventLog);
            services.AddScoped<ISubscriptionWriteRepository, FakeStripeSubscriptionWriteRepository>();
        });
    }
}

internal sealed class FakeStripeWebhookKeyRepository : IWebhookKeyRepository
{
    public Task<WebhookKeyInfo?> GetByKeyAsync(
        string webhookKey,
        string provider,
        CancellationToken ct = default)
    {
        var result = provider == "stripe" && webhookKey == "subtru_wh_test.secret"
            ? new WebhookKeyInfo(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                SubscriptionEnvironment.Test,
                "whsec_test")
            : null;

        return Task.FromResult(result);
    }
}

internal sealed class FakeStripeProcessedEventRepository : IProcessedEventRepository
{
    public Task<bool> IsProcessedAsync(
        Guid projectId,
        string provider,
        string environment,
        string eventId,
        CancellationToken ct = default)
        => Task.FromResult(false);
}

internal sealed class AlwaysProcessedEventRepository : IProcessedEventRepository
{
    public Task<bool> IsProcessedAsync(
        Guid projectId,
        string provider,
        string environment,
        string eventId,
        CancellationToken ct = default)
        => Task.FromResult(true);
}

internal sealed class FakeStripeWebhookEventLogRepository : IWebhookEventLogRepository
{
    public ConcurrentQueue<WebhookEventLogWriteModel> Writes { get; } = new();

    public Task UpsertAsync(
        WebhookEventLogWriteModel writeModel,
        CancellationToken ct = default)
    {
        Writes.Enqueue(writeModel);
        return Task.CompletedTask;
    }

    public Task<WebhookEventListPageReadModel> ListRecentAsync(
        Guid projectId,
        string environment,
        int limit,
        WebhookEventCursorReadModel? cursor,
        string? provider = null,
        WebhookEventStatusFilter? statusFilter = null,
        CancellationToken ct = default)
        => throw new NotSupportedException();
}

internal sealed class FakeStripeSubscriptionWriteRepository : ISubscriptionWriteRepository
{
    public Task<SubscriptionWriteOutcome> UpsertWithEventAsync(
        NormalizedSubscription sub,
        string eventId,
        CancellationToken ct)
        => Task.FromResult(SubscriptionWriteOutcome.Applied);
}

internal sealed class ThrowingSubscriptionWriteRepository : ISubscriptionWriteRepository
{
    public Task<SubscriptionWriteOutcome> UpsertWithEventAsync(
        NormalizedSubscription sub,
        string eventId,
        CancellationToken ct)
        => throw new InvalidOperationException("Core subscription write should not be called.");
}
