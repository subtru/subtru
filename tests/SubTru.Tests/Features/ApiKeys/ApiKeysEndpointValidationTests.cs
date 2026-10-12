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
using SubTru.Tests.Support;

namespace SubTru.Tests.Features.ApiKeys;

public sealed class ApiKeysEndpointValidationTests : IClassFixture<ApiKeysWebApplicationFactory>
{
    private const string ProjectId = "11111111-1111-1111-1111-111111111111";

    private readonly HttpClient _client;

    public ApiKeysEndpointValidationTests(ApiKeysWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "fake-jwt-token");
    }

    [Fact]
    public async Task CreateApiKey_PastExpiresAt_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync($"/v1/projects/{ProjectId}/api-keys", new
        {
            name = "Test Key",
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal("invalid_expires_at", error?.Error);
    }

    [Fact]
    public async Task CreateApiKey_ValidRequest_SetsNoStoreHeaders()
    {
        var response = await _client.PostAsJsonAsync($"/v1/projects/{ProjectId}/api-keys", new
        {
            name = "Test Key",
            expiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("no-cache", string.Join(",", response.Headers.Pragma.Select(x => x.Name)));
    }

    [Fact]
    public async Task CreateApiKey_ValidRequest_LogsProductEventWithoutRawApiKey()
    {
        using var loggerProvider = new CapturingLoggerProvider();
        using var baseFactory = new ApiKeysWebApplicationFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.AddProvider(loggerProvider));
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "fake-jwt-token");

        var response = await client.PostAsJsonAsync($"/v1/projects/{ProjectId}/api-keys", new
        {
            name = "Logged Key",
            expiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var entry = Assert.Single(loggerProvider.Entries.Where(entry =>
            entry.Category == "SubTru.ProductEvents" &&
            Equals(entry.State["product_event"], "api_key.created")));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(Guid.Parse(ProjectId), entry.State["project_id"]);
        Assert.Equal("subtru_live_test", entry.State["key_id"]);
        Assert.Equal("production", entry.State["environment"]);
        Assert.DoesNotContain("subtru_live_test.secret", entry.Message);
        Assert.DoesNotContain("subtru_live_test.secret", string.Join(" ", entry.State.Values));
    }

}

public sealed class ApiKeysWebApplicationFactory : WebApplicationFactory<Program>
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
            services.RemoveAll<IApiKeyManagementRepository>();

            services.AddScoped<IProjectResolver>(_ => new FakeNonAdminProjectResolverForApiKeys(
                Guid.Parse("22222222-2222-2222-2222-222222222222")));
            services.AddScoped<IApiKeyManagementRepository, FakeApiKeyManagementRepository>();
        });
    }
}

internal sealed class FakeNonAdminProjectResolverForApiKeys : IProjectResolver
{
    private readonly Guid _userId;

    public FakeNonAdminProjectResolverForApiKeys(Guid userId) => _userId = userId;

    public Task<AuthResult?> ResolveAsync(string bearerToken, CancellationToken ct = default)
        => Task.FromResult<AuthResult?>(new AuthResult(
            UserId: _userId,
            ProjectId: null,
            IsApiKeyAuth: false,
            IsAdminAuth: true));
}

internal sealed class FakeApiKeyManagementRepository : IApiKeyManagementRepository
{
    public Task<IReadOnlyList<ApiKeyListItem>> ListByProjectAsync(Guid projectId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ApiKeyListItem>>(Array.Empty<ApiKeyListItem>());

    public Task<CreatedApiKey> CreateAsync(Guid projectId, string name, string environment, DateTimeOffset? expiresAt, CancellationToken ct = default)
        => Task.FromResult(new CreatedApiKey(
            Id: Guid.NewGuid(),
            Name: name,
            Environment: environment,
            KeyId: "subtru_live_test",
            ApiKey: "subtru_live_test.secret",
            CreatedAt: DateTimeOffset.UtcNow));

    public Task<RevokedApiKey?> RevokeAsync(Guid projectId, Guid apiKeyId, CancellationToken ct = default)
        => Task.FromResult<RevokedApiKey?>(null);
}
