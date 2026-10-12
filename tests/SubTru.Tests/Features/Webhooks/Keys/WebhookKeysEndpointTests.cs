using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SubTru.Api.Platform.Http;
using SubTru.Api.Platform.Authentication;
using SubTru.Infrastructure.Persistence.Postgres.ApiKeys;
using SubTru.Infrastructure.Persistence.Postgres.Authentication;
using SubTru.Infrastructure.Persistence.Postgres.ProviderConfiguration;
using SubTru.Infrastructure.Persistence.Postgres.WebhookEvents;
using SubTru.Infrastructure.Persistence.Postgres.WebhookKeys;
using SubTru.Tests.Support;

namespace SubTru.Tests.Features.Webhooks.Keys;

public sealed class WebhookKeysEndpointTests : IClassFixture<WebhookKeysWebApplicationFactory>
{
    private const string ProjectId = "11111111-1111-1111-1111-111111111111";
    private static readonly Guid WebhookKeyId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly HttpClient _client;

    public WebhookKeysEndpointTests(WebhookKeysWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "fake-jwt-token");
    }

    // --- Create ---

    [Fact]
    public async Task CreateWebhookKey_ValidRequest_ReturnsCreated()
    {
        var response = await _client.PostAsJsonAsync($"/v1/projects/{ProjectId}/webhook-keys", new
        {
            provider = "stripe",
            signingSecret = "whsec_test123"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<CreatedWebhookKeyDto>();
        Assert.NotNull(body);
        Assert.Equal("stripe", body.Provider);
        Assert.NotNull(body.WebhookKey);
        Assert.NotNull(body.WebhookUrl);
        Assert.StartsWith("https://api.test.subtru.com/webhooks/stripe/", body.WebhookUrl);
    }

    [Fact]
    public async Task CreateWebhookKey_ValidRequest_SetsNoStoreHeaders()
    {
        var response = await _client.PostAsJsonAsync($"/v1/projects/{ProjectId}/webhook-keys", new
        {
            provider = "stripe"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("no-cache", string.Join(",", response.Headers.Pragma.Select(x => x.Name)));
    }

    [Fact]
    public async Task CreateWebhookKey_MissingProvider_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync($"/v1/projects/{ProjectId}/webhook-keys", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal("missing_provider", error?.Error);
    }

    [Fact]
    public async Task CreateWebhookKey_InvalidProvider_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync($"/v1/projects/{ProjectId}/webhook-keys", new
        {
            provider = "invalid-provider"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal("invalid_provider", error?.Error);
    }

    [Fact]
    public async Task CreateWebhookKey_WithoutSigningSecret_ReturnsCreated()
    {
        var response = await _client.PostAsJsonAsync($"/v1/projects/{ProjectId}/webhook-keys", new
        {
            provider = "apple"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<CreatedWebhookKeyDto>();
        Assert.NotNull(body);
        Assert.Equal("apple", body.Provider);
    }

    [Theory]
    [InlineData("stripe")]
    [InlineData("apple")]
    [InlineData("google")]
    public async Task CreateWebhookKey_AllValidProviders_ReturnsCreated(string provider)
    {
        var response = await _client.PostAsJsonAsync($"/v1/projects/{ProjectId}/webhook-keys", new
        {
            provider
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // --- List ---

    [Fact]
    public async Task ListWebhookKeys_ReturnsOk()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/webhook-keys");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<WebhookKeyListDto>();
        Assert.NotNull(body);
        Assert.NotNull(body.Items);
    }

    // --- Delete ---

    [Fact]
    public async Task DeleteWebhookKey_InvalidId_ReturnsBadRequest()
    {
        var response = await _client.DeleteAsync($"/v1/projects/{ProjectId}/webhook-keys/not-a-uuid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal("invalid_webhook_key_id", error?.Error);
    }

    [Fact]
    public async Task DeleteWebhookKey_NotFound_ReturnsNotFound()
    {
        var response = await _client.DeleteAsync($"/v1/projects/{ProjectId}/webhook-keys/{WebhookKeyId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal("webhook_key_not_found", error?.Error);
    }

    // --- Update Secret ---

    [Fact]
    public async Task UpdateSigningSecret_InvalidId_ReturnsBadRequest()
    {
        var response = await _client.PatchAsJsonAsync($"/v1/projects/{ProjectId}/webhook-keys/not-a-uuid", new
        {
            signingSecret = "whsec_test"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal("invalid_webhook_key_id", error?.Error);
    }

    [Fact]
    public async Task UpdateSigningSecret_MissingSecret_ReturnsBadRequest()
    {
        var response = await _client.PatchAsJsonAsync($"/v1/projects/{ProjectId}/webhook-keys/{WebhookKeyId}", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal("missing_signing_secret", error?.Error);
    }

    [Fact]
    public async Task UpdateSigningSecret_NotFound_ReturnsNotFound()
    {
        var response = await _client.PatchAsJsonAsync($"/v1/projects/{ProjectId}/webhook-keys/{WebhookKeyId}", new
        {
            signingSecret = "whsec_test"
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal("webhook_key_not_found", error?.Error);
    }

    [Fact]
    public async Task CreateWebhookKey_WhitespaceProvider_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync($"/v1/projects/{ProjectId}/webhook-keys", new
        {
            provider = "   "
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal("missing_provider", error?.Error);
    }

    // --- Response DTOs (private, for deserialization only) ---

    private sealed record CreatedWebhookKeyDto(
        Guid Id,
        string KeyId,
        string WebhookKey,
        string Provider,
        string WebhookUrl,
        DateTimeOffset CreatedAt);

    private sealed record WebhookKeyListDto(
        IReadOnlyList<WebhookKeyItemDto> Items);

    private sealed record WebhookKeyItemDto(
        Guid Id,
        string KeyId,
        string Provider,
        bool HasSigningSecret,
        bool IsActive,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}

public sealed class WebhookKeysHappyPathTests : IClassFixture<WebhookKeysHappyPathWebApplicationFactory>
{
    private const string ProjectId = "11111111-1111-1111-1111-111111111111";
    private static readonly Guid WebhookKeyId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly HttpClient _client;

    public WebhookKeysHappyPathTests(WebhookKeysHappyPathWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "fake-jwt-token");
    }

    [Fact]
    public async Task DeleteWebhookKey_ExistingKey_ReturnsOk()
    {
        var response = await _client.DeleteAsync($"/v1/projects/{ProjectId}/webhook-keys/{WebhookKeyId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<DeleteWebhookKeyDto>();
        Assert.NotNull(body);
        Assert.Equal(WebhookKeyId, body.Id);
        Assert.False(body.IsActive);
        Assert.True(body.Success);
    }

    [Fact]
    public async Task UpdateSigningSecret_ExistingKey_ReturnsOk()
    {
        var response = await _client.PatchAsJsonAsync($"/v1/projects/{ProjectId}/webhook-keys/{WebhookKeyId}", new
        {
            signingSecret = "whsec_new_secret"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<UpdateSigningSecretDto>();
        Assert.NotNull(body);
        Assert.True(body.Success);
    }

    [Fact]
    public async Task UpdateSigningSecret_ExistingKey_LogsProductEventWithoutSecret()
    {
        using var loggerProvider = new CapturingLoggerProvider();
        using var baseFactory = new WebhookKeysHappyPathWebApplicationFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.AddProvider(loggerProvider));
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "fake-jwt-token");

        const string submittedSecret = "whsec_new_secret";
        var response = await client.PatchAsJsonAsync($"/v1/projects/{ProjectId}/webhook-keys/{WebhookKeyId}", new
        {
            signingSecret = submittedSecret
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entry = Assert.Single(loggerProvider.Entries.Where(entry =>
            entry.Category == "SubTru.ProductEvents" &&
            Equals(entry.State["product_event"], "webhook_key.secret_rotated")));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(Guid.Parse(ProjectId), entry.State["project_id"]);
        Assert.Equal(WebhookKeyId, entry.State["webhook_key_id"]);
        Assert.DoesNotContain(submittedSecret, entry.Message);
        Assert.DoesNotContain(submittedSecret, string.Join(" ", entry.State.Values));
    }

    private sealed record DeleteWebhookKeyDto(Guid Id, bool IsActive, bool Success);
    private sealed record UpdateSigningSecretDto(bool Success);
}

public sealed class WebhookKeysApiKeyAuthTests : IClassFixture<WebhookKeysApiKeyWebApplicationFactory>
{
    private const string ProjectId = "11111111-1111-1111-1111-111111111111";

    private readonly HttpClient _client;

    public WebhookKeysApiKeyAuthTests(WebhookKeysApiKeyWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "subtru_live_test.secret");
    }

    [Fact]
    public async Task ListWebhookKeys_WithApiKeyAuth_ReturnsForbidden()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/webhook-keys");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal("forbidden", error?.Error);
    }

    [Fact]
    public async Task CreateWebhookKey_WithApiKeyAuth_ReturnsForbidden()
    {
        var response = await _client.PostAsJsonAsync($"/v1/projects/{ProjectId}/webhook-keys", new
        {
            provider = "stripe"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeleteWebhookKey_WithApiKeyAuth_ReturnsForbidden()
    {
        var response = await _client.DeleteAsync(
            $"/v1/projects/{ProjectId}/webhook-keys/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UpdateSigningSecret_WithApiKeyAuth_ReturnsForbidden()
    {
        var response = await _client.PatchAsJsonAsync(
            $"/v1/projects/{ProjectId}/webhook-keys/{Guid.NewGuid()}", new
            {
                signingSecret = "whsec_test"
            });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}

// --- Factories ---

public sealed class WebhookKeysWebApplicationFactory : WebApplicationFactory<Program>
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
            services.RemoveAll<IWebhookKeyManagementRepository>();

            services.AddScoped<IProjectResolver>(_ => new FakeNonAdminProjectResolverForWebhookKeys(
                Guid.Parse("22222222-2222-2222-2222-222222222222")));
            services.AddScoped<IWebhookKeyManagementRepository, FakeWebhookKeyManagementRepository>();
        });
    }
}

public sealed class WebhookKeysApiKeyWebApplicationFactory : WebApplicationFactory<Program>
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
            services.RemoveAll<IWebhookKeyManagementRepository>();

            services.AddScoped<IProjectResolver>(_ => new FakeApiKeyProjectResolverForWebhookKeys(
                Guid.Parse("22222222-2222-2222-2222-222222222222")));
            services.AddScoped<IWebhookKeyManagementRepository, FakeWebhookKeyManagementRepository>();
        });
    }
}

// --- Fakes ---

internal sealed class FakeNonAdminProjectResolverForWebhookKeys : IProjectResolver
{
    private readonly Guid _userId;

    public FakeNonAdminProjectResolverForWebhookKeys(Guid userId) => _userId = userId;

    public Task<AuthResult?> ResolveAsync(string bearerToken, CancellationToken ct = default)
        => Task.FromResult<AuthResult?>(new AuthResult(
            UserId: _userId,
            ProjectId: null,
            IsApiKeyAuth: false,
            IsAdminAuth: true));
}

internal sealed class FakeApiKeyProjectResolverForWebhookKeys : IProjectResolver
{
    private readonly Guid _userId;

    public FakeApiKeyProjectResolverForWebhookKeys(Guid userId) => _userId = userId;

    public Task<AuthResult?> ResolveAsync(string bearerToken, CancellationToken ct = default)
        => Task.FromResult<AuthResult?>(new AuthResult(
            UserId: _userId,
            ProjectId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            IsApiKeyAuth: true));
}

internal sealed class FakeWebhookKeyManagementRepository : IWebhookKeyManagementRepository
{
    public Task<IReadOnlyList<WebhookKeyListItem>> ListByProjectAsync(Guid projectId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WebhookKeyListItem>>(Array.Empty<WebhookKeyListItem>());

    public Task<CreatedWebhookKey> CreateAsync(Guid projectId, string provider, string environment, string? signingSecret, CancellationToken ct = default)
        => Task.FromResult(new CreatedWebhookKey(
            Id: Guid.NewGuid(),
            KeyId: "subtru_wh_test1234",
            WebhookKey: "subtru_wh_test1234.fakesecret",
            Provider: provider,
            Environment: environment,
            CreatedAt: DateTimeOffset.UtcNow));

    public Task<DeactivatedWebhookKey?> DeactivateAsync(Guid projectId, Guid webhookKeyId, CancellationToken ct = default)
        => Task.FromResult<DeactivatedWebhookKey?>(null);

    public Task<bool> UpdateSigningSecretAsync(Guid projectId, Guid webhookKeyId, string signingSecret, CancellationToken ct = default)
        => Task.FromResult(false);
}

internal sealed class SuccessFakeWebhookKeyManagementRepository : IWebhookKeyManagementRepository
{
    public Task<IReadOnlyList<WebhookKeyListItem>> ListByProjectAsync(Guid projectId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WebhookKeyListItem>>(Array.Empty<WebhookKeyListItem>());

    public Task<CreatedWebhookKey> CreateAsync(Guid projectId, string provider, string environment, string? signingSecret, CancellationToken ct = default)
        => Task.FromResult(new CreatedWebhookKey(
            Id: Guid.NewGuid(),
            KeyId: "subtru_wh_test1234",
            WebhookKey: "subtru_wh_test1234.fakesecret",
            Provider: provider,
            Environment: environment,
            CreatedAt: DateTimeOffset.UtcNow));

    public Task<DeactivatedWebhookKey?> DeactivateAsync(Guid projectId, Guid webhookKeyId, CancellationToken ct = default)
        => Task.FromResult<DeactivatedWebhookKey?>(new DeactivatedWebhookKey(
            Id: webhookKeyId,
            IsActive: false,
            UpdatedAt: DateTimeOffset.UtcNow));

    public Task<bool> UpdateSigningSecretAsync(Guid projectId, Guid webhookKeyId, string signingSecret, CancellationToken ct = default)
        => Task.FromResult(true);
}

public sealed class WebhookKeysHappyPathWebApplicationFactory : WebApplicationFactory<Program>
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
            services.RemoveAll<IWebhookKeyManagementRepository>();

            services.AddScoped<IProjectResolver>(_ => new FakeNonAdminProjectResolverForWebhookKeys(
                Guid.Parse("22222222-2222-2222-2222-222222222222")));
            services.AddScoped<IWebhookKeyManagementRepository, SuccessFakeWebhookKeyManagementRepository>();
        });
    }
}
