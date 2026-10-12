using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubTru.Api.Platform.Http;
using SubTru.Api.Platform.Authentication;
using SubTru.Infrastructure.Persistence.Postgres.ApiKeys;
using SubTru.Infrastructure.Persistence.Postgres.Authentication;
using SubTru.Infrastructure.Persistence.Postgres.ProviderConfiguration;
using SubTru.Infrastructure.Persistence.Postgres.WebhookEvents;
using SubTru.Infrastructure.Persistence.Postgres.WebhookKeys;

namespace SubTru.Tests.Features.Webhooks.Events;

public sealed class WebhookEventsEndpointTests : IClassFixture<WebhookEventsWebApplicationFactory>
{
    private const string ProjectId = "11111111-1111-1111-1111-111111111111";

    private readonly HttpClient _client;

    public WebhookEventsEndpointTests(WebhookEventsWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "subtru_live_test.secret");
    }

    [Fact]
    public async Task GetWebhookEvents_WithApiKeyAuth_ReturnsForbidden()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/webhook-events");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ApiErrorResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("forbidden", payload.Error);
        Assert.Equal(
            "Webhook event access requires the installation admin API key.",
            payload.Message);
    }

    [Fact]
    public async Task GetWebhookEvents_WithoutAuth_ReturnsUnauthorized()
    {
        using var unauthenticatedClient = new WebhookEventsWebApplicationFactory().CreateClient();

        var response = await unauthenticatedClient.GetAsync($"/v1/projects/{ProjectId}/webhook-events");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed record GetWebhookEventsResponseDto(
        IReadOnlyList<WebhookEventDto> Items,
        string? NextCursor
    );

    private sealed record WebhookEventDto(
        string Id,
        string Provider,
        string? Type,
        string? Change,
        string? ExternalUserId,
        string? ProviderSubscriptionId,
        string Status,
        string? ErrorCode,
        DateTimeOffset ReceivedAt,
        DateTimeOffset? ProcessedAt
    );

    private sealed record ApiErrorResponseDto(
        string Error,
        string Message
    );

    private static string EncodeCursor(DateTimeOffset receivedAt, string eventId)
    {
        var payload = $"{receivedAt.ToUnixTimeMilliseconds()}|{eventId}";
        return Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(payload));
    }
}

public sealed class WebhookEventsAdminEndpointTests : IClassFixture<WebhookEventsAdminWebApplicationFactory>
{
    private const string ProjectId = "11111111-1111-1111-1111-111111111111";

    private readonly HttpClient _client;

    public WebhookEventsAdminEndpointTests(WebhookEventsAdminWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "fake-admin-token");
    }

    [Fact]
    public async Task GetWebhookEvents_WithAdminAuth_ReturnsListEnvelope()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/webhook-events");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<GetWebhookEventsResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(4, payload.Items.Count);
        Assert.Null(payload.NextCursor);
        Assert.DoesNotContain(payload.Items, x =>
            x.Status == WebhookEventStatus.Ignored && x.Type != "google.test_notification");
        Assert.Equal("evt_005", payload.Items[0].Id);
        Assert.Equal("google", payload.Items[0].Provider);
        Assert.Equal("ignored", payload.Items[0].Status);
        Assert.Equal("google.test_notification", payload.Items[0].Type);

        Assert.True(payload.Items[0].ReceivedAt > payload.Items[1].ReceivedAt);
        Assert.True(payload.Items[1].ReceivedAt > payload.Items[2].ReceivedAt);
        Assert.True(payload.Items[2].ReceivedAt > payload.Items[3].ReceivedAt);

        var failedEvent = payload.Items.Single(x => x.Id == "evt_002");
        Assert.Equal("google", failedEvent.Provider);
        Assert.Equal("failed", failedEvent.Status);
        Assert.Equal("google_unbound_purchase", failedEvent.ErrorCode);
        Assert.Equal("token-unbound", failedEvent.ProviderSubscriptionId);
        Assert.Null(failedEvent.ProcessedAt);

        var stripeEvent = payload.Items.Single(x => x.Id == "evt_003");
        Assert.Equal("cancellation_scheduled", stripeEvent.Change);
    }

    [Fact]
    public async Task GetWebhookEvents_FilterByProvider_ReturnsOnlyThatProvider()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/webhook-events?provider=stripe");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<GetWebhookEventsResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(2, payload.Items.Count);
        Assert.All(payload.Items, x => Assert.Equal("stripe", x.Provider));
    }

    [Fact]
    public async Task GetWebhookEvents_FilterByFailedStatus_ReturnsOnlyFailures()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/webhook-events?status=failed");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<GetWebhookEventsResponseDto>();
        Assert.NotNull(payload);
        Assert.All(payload.Items, x => Assert.Equal("failed", x.Status));
        Assert.Contains(payload.Items, x => x.Id == "evt_002");
    }

    [Fact]
    public async Task GetWebhookEvents_FilterBySucceededStatus_ExcludesFailures()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/webhook-events?status=succeeded");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<GetWebhookEventsResponseDto>();
        Assert.NotNull(payload);
        Assert.DoesNotContain(payload.Items, x => x.Status == "failed");
        Assert.Contains(payload.Items, x => x.Id == "evt_005");
    }

    [Fact]
    public async Task GetWebhookEvents_InvalidProvider_ReturnsBadRequest()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/webhook-events?provider=paypal");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal("invalid_provider", error?.Error);
    }

    [Fact]
    public async Task GetWebhookEvents_InvalidStatus_ReturnsBadRequest()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/webhook-events?status=pending");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal("invalid_status", error?.Error);
    }

    [Fact]
    public async Task GetWebhookEvents_EmptyDataset_ReturnsEmptyItemsAndNullCursor()
    {
        using var client = new EmptyWebhookEventsAdminWebApplicationFactory().CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "fake-admin-token");

        var response = await client.GetAsync($"/v1/projects/{ProjectId}/webhook-events");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<GetWebhookEventsResponseDto>();
        Assert.NotNull(payload);
        Assert.Empty(payload.Items);
        Assert.Null(payload.NextCursor);
    }

    [Fact]
    public async Task GetWebhookEvents_WithLimitAndCursor_Paginates()
    {
        var firstResponse = await _client.GetAsync($"/v1/projects/{ProjectId}/webhook-events?limit=2");

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        var firstPayload = await firstResponse.Content.ReadFromJsonAsync<GetWebhookEventsResponseDto>();
        Assert.NotNull(firstPayload);
        Assert.Equal(2, firstPayload.Items.Count);
        Assert.False(string.IsNullOrWhiteSpace(firstPayload.NextCursor));

        var secondResponse = await _client.GetAsync($"/v1/projects/{ProjectId}/webhook-events?limit=2&cursor={firstPayload.NextCursor}");

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);

        var secondPayload = await secondResponse.Content.ReadFromJsonAsync<GetWebhookEventsResponseDto>();
        Assert.NotNull(secondPayload);
        Assert.Equal(2, secondPayload.Items.Count);
        Assert.Null(secondPayload.NextCursor);
        Assert.Equal("evt_002", secondPayload.Items[0].Id);
        Assert.Equal("evt_001", secondPayload.Items[1].Id);
    }

    [Fact]
    public async Task GetWebhookEvents_CursorPastEnd_ReturnsEmptyItemsAndNullCursor()
    {
        var cursorPastEnd = EncodeCursor(new DateTimeOffset(2026, 2, 17, 12, 0, 0, TimeSpan.Zero), "evt_000");

        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/webhook-events?limit=50&cursor={cursorPastEnd}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<GetWebhookEventsResponseDto>();
        Assert.NotNull(payload);
        Assert.Empty(payload.Items);
        Assert.Null(payload.NextCursor);
    }

    [Fact]
    public async Task GetWebhookEvents_LimitEqualsMaxLimit_ReturnsOk()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/webhook-events?limit=100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<GetWebhookEventsResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(4, payload.Items.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task GetWebhookEvents_InvalidLimit_ReturnsBadRequest(int limit)
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/webhook-events?limit={limit}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ApiErrorResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("invalid_limit", payload.Error);
    }

    [Fact]
    public async Task GetWebhookEvents_InvalidCursor_ReturnsBadRequest()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/webhook-events?cursor=not-valid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ApiErrorResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("invalid_cursor", payload.Error);
    }

    [Fact]
    public async Task GetWebhookEvents_WithoutAuth_ReturnsUnauthorized()
    {
        using var unauthenticatedClient = new WebhookEventsAdminWebApplicationFactory().CreateClient();

        var response = await unauthenticatedClient.GetAsync($"/v1/projects/{ProjectId}/webhook-events");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed record GetWebhookEventsResponseDto(
        IReadOnlyList<WebhookEventDto> Items,
        string? NextCursor
    );

    private sealed record WebhookEventDto(
        string Id,
        string Provider,
        string? Type,
        string? Change,
        string? ExternalUserId,
        string? ProviderSubscriptionId,
        string Status,
        string? ErrorCode,
        DateTimeOffset ReceivedAt,
        DateTimeOffset? ProcessedAt
    );

    private sealed record ApiErrorResponseDto(
        string Error,
        string Message
    );

    private static string EncodeCursor(DateTimeOffset receivedAt, string eventId)
    {
        var payload = $"{receivedAt.ToUnixTimeMilliseconds()}|{eventId}";
        return Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(payload));
    }

}

public sealed class WebhookEventsWebApplicationFactory : WebApplicationFactory<Program>
{
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
                ["AppleVerification:RootCertificateDirectory"] = TestAppleTrustStoreDirectory.DirectoryPath
            };

            configBuilder.AddInMemoryCollection(config);
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IProjectAuthService>();
            services.RemoveAll<IWebhookEventLogRepository>();

            services.AddSingleton<IProjectAuthService>(new AllowAllProjectAuthServiceForWebhookEvents());
            services.AddScoped<IWebhookEventLogRepository, FakeWebhookEventLogRepository>();
        });
    }
}

public sealed class EmptyWebhookEventsWebApplicationFactory : WebApplicationFactory<Program>
{
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
                ["AppleVerification:RootCertificateDirectory"] = TestAppleTrustStoreDirectory.DirectoryPath
            };

            configBuilder.AddInMemoryCollection(config);
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IProjectAuthService>();
            services.RemoveAll<IWebhookEventLogRepository>();

            services.AddSingleton<IProjectAuthService>(new AllowAllProjectAuthServiceForWebhookEvents());
            services.AddScoped<IWebhookEventLogRepository, EmptyWebhookEventLogRepository>();
        });
    }
}

public sealed class WebhookEventsAdminWebApplicationFactory : WebApplicationFactory<Program>
{
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
                ["AppleVerification:RootCertificateDirectory"] = TestAppleTrustStoreDirectory.DirectoryPath
            };

            configBuilder.AddInMemoryCollection(config);
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IProjectAuthService>();
            services.RemoveAll<IProjectResolver>();
            services.RemoveAll<IWebhookEventLogRepository>();

            services.AddScoped<IProjectResolver>(_ => new FakeAdminProjectResolverForWebhookEvents(
                Guid.Parse("22222222-2222-2222-2222-222222222222")));
            services.AddScoped<IWebhookEventLogRepository, FakeWebhookEventLogRepository>();
        });
    }
}

public sealed class EmptyWebhookEventsAdminWebApplicationFactory : WebApplicationFactory<Program>
{
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
                ["AppleVerification:RootCertificateDirectory"] = TestAppleTrustStoreDirectory.DirectoryPath
            };

            configBuilder.AddInMemoryCollection(config);
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IProjectAuthService>();
            services.RemoveAll<IProjectResolver>();
            services.RemoveAll<IWebhookEventLogRepository>();

            services.AddScoped<IProjectResolver>(_ => new FakeAdminProjectResolverForWebhookEvents(
                Guid.Parse("22222222-2222-2222-2222-222222222222")));
            services.AddScoped<IWebhookEventLogRepository, EmptyWebhookEventLogRepository>();
        });
    }
}

internal sealed class AllowAllProjectAuthServiceForWebhookEvents : IProjectAuthService
{
    public Task<ValidatedApiKey?> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
        => Task.FromResult<ValidatedApiKey?>(new ValidatedApiKey(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "production"));
}

internal sealed class FakeAdminProjectResolverForWebhookEvents : IProjectResolver
{
    private readonly Guid _userId;

    public FakeAdminProjectResolverForWebhookEvents(Guid userId) => _userId = userId;

    public Task<AuthResult?> ResolveAsync(string bearerToken, CancellationToken ct = default)
        => Task.FromResult<AuthResult?>(new AuthResult(
            UserId: _userId,
            ProjectId: null,
            IsApiKeyAuth: false,
            IsAdminAuth: true));
}

internal sealed class FakeWebhookEventLogRepository : IWebhookEventLogRepository
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly IReadOnlyList<WebhookEventListItemReadModel> Items =
    [
        new(
            EventId: "evt_005",
            Provider: "google",
            Environment: "production",
            EventType: "google.test_notification",
            ExternalUserId: null,
            Status: WebhookEventStatus.Ignored,
            ReceivedAt: new DateTimeOffset(2026, 2, 17, 12, 5, 0, TimeSpan.Zero),
            ProcessedAt: new DateTimeOffset(2026, 2, 17, 12, 5, 1, TimeSpan.Zero),
            ErrorCode: null,
            ProviderSubscriptionId: null),
        new(
            EventId: "evt_004",
            Provider: "stripe",
            Environment: "production",
            EventType: "invoice.created",
            ExternalUserId: null,
            Status: WebhookEventStatus.Ignored,
            ReceivedAt: new DateTimeOffset(2026, 2, 17, 12, 4, 0, TimeSpan.Zero),
            ProcessedAt: new DateTimeOffset(2026, 2, 17, 12, 4, 1, TimeSpan.Zero),
            ErrorCode: null,
            ProviderSubscriptionId: null),
        new(
            EventId: "evt_003",
            Provider: "stripe",
            Environment: "production",
            EventType: "customer.subscription.updated",
            ExternalUserId: "user-3",
            Status: WebhookEventStatus.Success,
            ReceivedAt: new DateTimeOffset(2026, 2, 17, 12, 3, 0, TimeSpan.Zero),
            ProcessedAt: new DateTimeOffset(2026, 2, 17, 12, 3, 2, TimeSpan.Zero),
            ErrorCode: null,
            ProviderSubscriptionId: null,
            EventChange: "cancellation_scheduled"),
        new(
            EventId: "evt_002",
            Provider: "google",
            Environment: "production",
            EventType: "google.subscription.recovered",
            ExternalUserId: null,
            Status: WebhookEventStatus.Failed,
            ReceivedAt: new DateTimeOffset(2026, 2, 17, 12, 2, 0, TimeSpan.Zero),
            ProcessedAt: null,
            ErrorCode: "google_unbound_purchase",
            ProviderSubscriptionId: "token-unbound"),
        new(
            EventId: "evt_001",
            Provider: "stripe",
            Environment: "production",
            EventType: "customer.subscription.created",
            ExternalUserId: "user-1",
            Status: WebhookEventStatus.Success,
            ReceivedAt: new DateTimeOffset(2026, 2, 17, 12, 1, 0, TimeSpan.Zero),
            ProcessedAt: new DateTimeOffset(2026, 2, 17, 12, 1, 2, TimeSpan.Zero),
            ErrorCode: null,
            ProviderSubscriptionId: null),
    ];

    public Task UpsertAsync(WebhookEventLogWriteModel writeModel, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<WebhookEventListPageReadModel> ListRecentAsync(Guid projectId, string environment, int limit, WebhookEventCursorReadModel? cursor, string? provider = null, WebhookEventStatusFilter? statusFilter = null, CancellationToken ct = default)
    {
        Assert.Equal(ProjectId, projectId);
        Assert.Equal("production", environment);

        var query = Items.Where(x =>
            x.Status is WebhookEventStatus.Success or WebhookEventStatus.Failed
            || (x.Status == WebhookEventStatus.Ignored
                && x.EventType is "google.test_notification" or "TEST"));

        if (provider is not null)
        {
            query = query.Where(x => x.Provider == provider);
        }

        if (statusFilter == WebhookEventStatusFilter.Failed)
        {
            query = query.Where(x => x.Status == WebhookEventStatus.Failed);
        }
        else if (statusFilter == WebhookEventStatusFilter.Succeeded)
        {
            query = query.Where(x => x.Status != WebhookEventStatus.Failed);
        }

        if (cursor is not null)
        {
            query = query.Where(x =>
                x.ReceivedAt < cursor.ReceivedAt
                || (x.ReceivedAt == cursor.ReceivedAt && string.CompareOrdinal(x.EventId, cursor.EventId) < 0));
        }

        var selected = query
            .OrderByDescending(x => x.ReceivedAt)
            .ThenByDescending(x => x.EventId)
            .Take(limit + 1)
            .ToList();

        WebhookEventCursorReadModel? nextCursor = null;
        if (selected.Count > limit)
        {
            var next = selected[limit - 1];
            nextCursor = new WebhookEventCursorReadModel(next.ReceivedAt, next.EventId);
            selected = selected.Take(limit).ToList();
        }

        return Task.FromResult(new WebhookEventListPageReadModel(selected, nextCursor));
    }
}

internal sealed class EmptyWebhookEventLogRepository : IWebhookEventLogRepository
{
    public Task UpsertAsync(WebhookEventLogWriteModel writeModel, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<WebhookEventListPageReadModel> ListRecentAsync(Guid projectId, string environment, int limit, WebhookEventCursorReadModel? cursor, string? provider = null, WebhookEventStatusFilter? statusFilter = null, CancellationToken ct = default)
    {
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), projectId);
        Assert.Equal("production", environment);
        return Task.FromResult(new WebhookEventListPageReadModel([], null));
    }
}
