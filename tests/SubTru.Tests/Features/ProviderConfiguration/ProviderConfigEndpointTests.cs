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
using SubTru.Api.Features.ProviderConfiguration;
using SubTru.Api.Platform.Authentication;
using SubTru.Infrastructure.Persistence.Postgres.ApiKeys;
using SubTru.Infrastructure.Persistence.Postgres.Authentication;
using SubTru.Infrastructure.Providers.Google;
using SubTru.Infrastructure.Persistence.Postgres.ProviderConfiguration;
using SubTru.Infrastructure.Persistence.Postgres.WebhookEvents;
using SubTru.Infrastructure.Persistence.Postgres.WebhookKeys;
using SubTru.Tests.Support;

namespace SubTru.Tests.Features.ProviderConfiguration;

public sealed class ProviderConfigEndpointTests : IClassFixture<ProviderConfigWebApplicationFactory>
{
    private const string ProjectId = "11111111-1111-1111-1111-111111111111";

    private readonly HttpClient _client;

    public ProviderConfigEndpointTests(ProviderConfigWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "fake-jwt-token");
    }

    [Fact]
    public async Task GetAppleProviderConfig_NotFound_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/provider-configs/apple");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal("provider_config_not_found", error?.Error);
    }

    [Fact]
    public async Task PutAppleProviderConfig_ValidRequest_ReturnsOk()
    {
        var response = await _client.PutAsJsonAsync($"/v1/projects/{ProjectId}/provider-configs/apple", new
        {
            bundleId = "com.acme.app",
            appAppleId = 1234567890,
            environment = "both"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AppleProviderConfigDto>();
        Assert.NotNull(body);
        Assert.Equal("apple", body.Provider);
        Assert.Equal("com.acme.app", body.BundleId);
        Assert.Equal(1234567890, body.AppAppleId);
    }

    [Fact]
    public async Task PutAppleProviderConfig_MissingBundleId_ReturnsBadRequest()
    {
        var response = await _client.PutAsJsonAsync($"/v1/projects/{ProjectId}/provider-configs/apple", new
        {
            bundleId = "",
            appAppleId = 1234567890,
            environment = "both"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal("missing_bundle_id", error?.Error);
    }

    [Fact]
    public async Task PutAppleProviderConfig_InvalidAppleId_ReturnsBadRequest()
    {
        var response = await _client.PutAsJsonAsync($"/v1/projects/{ProjectId}/provider-configs/apple", new
        {
            bundleId = "com.acme.app",
            appAppleId = 0,
            environment = "both"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal("invalid_app_apple_id", error?.Error);
    }

    [Fact]
    public async Task PutAppleProviderConfig_LegacyEnvironmentField_IsIgnored()
    {
        var response = await _client.PutAsJsonAsync($"/v1/projects/{ProjectId}/provider-configs/apple", new
        {
            bundleId = "com.acme.app",
            appAppleId = 1234567890,
            environment = "qa"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetGoogleProviderConfig_NotFound_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/provider-configs/google");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal("provider_config_not_found", error?.Error);
    }

    [Fact]
    public async Task PutGoogleProviderConfig_ValidRequest_ReturnsOk()
    {
        var response = await _client.PutAsJsonAsync($"/v1/projects/{ProjectId}/provider-configs/google", new
        {
            packageName = "com.acme.app",
            pubSubAuthServiceAccountEmail = "push-auth@acme.iam.gserviceaccount.com",
            serviceAccountJson = FakeGoogleServiceAccountValidator.ValidJson
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<GoogleProviderConfigDto>();
        Assert.NotNull(body);
        Assert.Equal("google", body.Provider);
        Assert.Equal("com.acme.app", body.PackageName);
        Assert.Equal("push-auth@acme.iam.gserviceaccount.com", body.PubSubAuthServiceAccountEmail);
        Assert.True(body.HasServiceAccountCredentials);
        Assert.Equal("svc-acme@play-project.iam.gserviceaccount.com", body.ServiceAccountClientEmail);
        Assert.Equal("key-123", body.ServiceAccountKeyId);
        Assert.NotNull(body.ServiceAccountLastValidatedAt);
    }

    [Fact]
    public async Task PutGoogleProviderConfig_ValidRequest_LogsProductEventWithoutServiceAccountJson()
    {
        using var loggerProvider = new CapturingLoggerProvider();
        using var baseFactory = new ProviderConfigWebApplicationFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.AddProvider(loggerProvider));
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "fake-jwt-token");

        var response = await client.PutAsJsonAsync($"/v1/projects/{ProjectId}/provider-configs/google", new
        {
            packageName = "com.acme.app",
            pubSubAuthServiceAccountEmail = "push-auth@acme.iam.gserviceaccount.com",
            serviceAccountJson = FakeGoogleServiceAccountValidator.ValidJson
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entry = Assert.Single(loggerProvider.Entries.Where(entry =>
            entry.Category == "SubTru.ProductEvents" &&
            Equals(entry.State["product_event"], "provider_config.upserted")));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(Guid.Parse(ProjectId), entry.State["project_id"]);
        Assert.Equal("google", entry.State["provider"]);
        Assert.Equal(true, entry.State["service_account_credentials_updated"]);
        Assert.DoesNotContain(FakeGoogleServiceAccountValidator.ValidJson, entry.Message);
        Assert.DoesNotContain("push-auth@acme.iam.gserviceaccount.com", entry.Message);
        Assert.DoesNotContain("key-123", entry.Message);
        Assert.DoesNotContain(FakeGoogleServiceAccountValidator.ValidJson, string.Join(" ", entry.State.Values));
    }

    [Fact]
    public async Task PutGoogleProviderConfig_MissingServiceAccountJsonWithoutStoredCredentials_SavesConfigWithoutCredentials()
    {
        var response = await _client.PutAsJsonAsync($"/v1/projects/{ProjectId}/provider-configs/google", new
        {
            packageName = "com.acme.app",
            pubSubAuthServiceAccountEmail = "push-auth@acme.iam.gserviceaccount.com"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<GoogleProviderConfigDto>();
        Assert.NotNull(body);
        Assert.Equal("google", body.Provider);
        Assert.Equal("com.acme.app", body.PackageName);
        Assert.Equal("push-auth@acme.iam.gserviceaccount.com", body.PubSubAuthServiceAccountEmail);
        Assert.False(body.HasServiceAccountCredentials);
        Assert.Null(body.ServiceAccountClientEmail);
        Assert.Null(body.ServiceAccountKeyId);
    }

    [Fact]
    public async Task PutGoogleProviderConfig_InvalidAuthServiceAccountEmail_ReturnsBadRequest()
    {
        var response = await _client.PutAsJsonAsync($"/v1/projects/{ProjectId}/provider-configs/google", new
        {
            packageName = "com.acme.app",
            pubSubAuthServiceAccountEmail = "not-an-email",
            serviceAccountJson = FakeGoogleServiceAccountValidator.ValidJson
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal("invalid_pubsub_auth_service_account_email", error?.Error);
    }

    [Fact]
    public async Task PutGoogleProviderConfig_InvalidServiceAccountJson_ReturnsBadRequest()
    {
        var response = await _client.PutAsJsonAsync($"/v1/projects/{ProjectId}/provider-configs/google", new
        {
            packageName = "com.acme.app",
            pubSubAuthServiceAccountEmail = "push-auth@acme.iam.gserviceaccount.com",
            serviceAccountJson = "invalid-google-json"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal("invalid_service_account_json", error?.Error);
    }

    [Fact]
    public async Task PutGoogleProviderConfig_WhenGoogleValidationIsUnavailable_ReturnsServiceUnavailable()
    {
        var response = await _client.PutAsJsonAsync($"/v1/projects/{ProjectId}/provider-configs/google", new
        {
            packageName = "com.acme.app",
            pubSubAuthServiceAccountEmail = "push-auth@acme.iam.gserviceaccount.com",
            serviceAccountJson = "transient-google-error"
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal("service_account_validation_unavailable", error?.Error);
    }

}

public sealed class ProviderConfigHappyPathTests : IClassFixture<ProviderConfigHappyPathWebApplicationFactory>
{
    private const string ProjectId = "11111111-1111-1111-1111-111111111111";

    private readonly HttpClient _client;

    public ProviderConfigHappyPathTests(ProviderConfigHappyPathWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "fake-jwt-token");
    }

    [Fact]
    public async Task GetAppleProviderConfig_ExistingConfig_ReturnsOk()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/provider-configs/apple");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AppleProviderConfigDto>();
        Assert.NotNull(body);
        Assert.Equal("com.acme.existing", body.BundleId);
        Assert.Equal(9876543210, body.AppAppleId);
    }

    [Fact]
    public async Task GetGoogleProviderConfig_ExistingConfig_ReturnsOk()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/provider-configs/google");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<GoogleProviderConfigDto>();
        Assert.NotNull(body);
        Assert.Equal("com.acme.existing", body.PackageName);
        Assert.True(body.HasServiceAccountCredentials);
        Assert.Equal("svc-existing@play-project.iam.gserviceaccount.com", body.ServiceAccountClientEmail);
    }

    [Fact]
    public async Task PutGoogleProviderConfig_WithoutServiceAccountJson_WhenStoredCredentialExists_ReturnsOk()
    {
        var response = await _client.PutAsJsonAsync($"/v1/projects/{ProjectId}/provider-configs/google", new
        {
            packageName = "com.acme.updated",
            pubSubAuthServiceAccountEmail = "push-auth@acme.iam.gserviceaccount.com"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<GoogleProviderConfigDto>();
        Assert.NotNull(body);
        Assert.Equal("com.acme.updated", body.PackageName);
        Assert.True(body.HasServiceAccountCredentials);
        Assert.Equal("svc-existing@play-project.iam.gserviceaccount.com", body.ServiceAccountClientEmail);
    }

}

public sealed class ProviderConfigApiKeyAuthTests : IClassFixture<ProviderConfigApiKeyWebApplicationFactory>
{
    private const string ProjectId = "11111111-1111-1111-1111-111111111111";

    private readonly HttpClient _client;

    public ProviderConfigApiKeyAuthTests(ProviderConfigApiKeyWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "subtru_live_test.secret");
    }

    [Fact]
    public async Task GetAppleProviderConfig_WithApiKeyAuth_ReturnsForbidden()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/provider-configs/apple");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PutAppleProviderConfig_WithApiKeyAuth_ReturnsForbidden()
    {
        var response = await _client.PutAsJsonAsync($"/v1/projects/{ProjectId}/provider-configs/apple", new
        {
            bundleId = "com.acme.app",
            appAppleId = 1234567890,
            environment = "both"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetGoogleProviderConfig_WithApiKeyAuth_ReturnsForbidden()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/provider-configs/google");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PutGoogleProviderConfig_WithApiKeyAuth_ReturnsForbidden()
    {
        var response = await _client.PutAsJsonAsync($"/v1/projects/{ProjectId}/provider-configs/google", new
        {
            packageName = "com.acme.app",
            pubSubAuthServiceAccountEmail = "push-auth@acme.iam.gserviceaccount.com",
            serviceAccountJson = FakeGoogleServiceAccountValidator.ValidJson
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}

public sealed class ProviderConfigWebApplicationFactory : WebApplicationFactory<Program>
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
            services.RemoveAll<IProviderConfigRepository>();
            services.RemoveAll<IGoogleServiceAccountValidator>();

            services.AddScoped<IProjectResolver>(_ => new FakeNonAdminProjectResolverForProviderConfigs(
                Guid.Parse("22222222-2222-2222-2222-222222222222")));
            services.AddScoped<IProviderConfigRepository, FakeProviderConfigRepository>();
            services.AddScoped<IGoogleServiceAccountValidator, FakeGoogleServiceAccountValidator>();
        });
    }
}

public sealed class ProviderConfigHappyPathWebApplicationFactory : WebApplicationFactory<Program>
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
            services.RemoveAll<IProviderConfigRepository>();
            services.RemoveAll<IGoogleServiceAccountValidator>();

            services.AddScoped<IProjectResolver>(_ => new FakeNonAdminProjectResolverForProviderConfigs(
                Guid.Parse("22222222-2222-2222-2222-222222222222")));
            services.AddScoped<IProviderConfigRepository, SuccessFakeProviderConfigRepository>();
            services.AddScoped<IGoogleServiceAccountValidator, FakeGoogleServiceAccountValidator>();
        });
    }
}

public sealed class ProviderConfigApiKeyWebApplicationFactory : WebApplicationFactory<Program>
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
            services.RemoveAll<IProviderConfigRepository>();
            services.RemoveAll<IGoogleServiceAccountValidator>();

            services.AddScoped<IProjectResolver>(_ => new FakeApiKeyProjectResolverForProviderConfigs(
                Guid.Parse("22222222-2222-2222-2222-222222222222")));
            services.AddScoped<IProviderConfigRepository, FakeProviderConfigRepository>();
            services.AddScoped<IGoogleServiceAccountValidator, FakeGoogleServiceAccountValidator>();
        });
    }
}

internal sealed class FakeNonAdminProjectResolverForProviderConfigs : IProjectResolver
{
    private readonly Guid _userId;

    public FakeNonAdminProjectResolverForProviderConfigs(Guid userId) => _userId = userId;

    public Task<AuthResult?> ResolveAsync(string bearerToken, CancellationToken ct = default)
        => Task.FromResult<AuthResult?>(new AuthResult(
            UserId: _userId,
            ProjectId: null,
            IsApiKeyAuth: false,
            IsAdminAuth: true));
}

internal sealed class FakeApiKeyProjectResolverForProviderConfigs : IProjectResolver
{
    private readonly Guid _userId;

    public FakeApiKeyProjectResolverForProviderConfigs(Guid userId) => _userId = userId;

    public Task<AuthResult?> ResolveAsync(string bearerToken, CancellationToken ct = default)
        => Task.FromResult<AuthResult?>(new AuthResult(
            UserId: _userId,
            ProjectId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            IsApiKeyAuth: true));
}

internal sealed class FakeProviderConfigRepository : IProviderConfigRepository
{
    public Task<AppleProviderConfigReadModel?> GetAppleProviderConfigAsync(Guid projectId, CancellationToken ct = default)
        => Task.FromResult<AppleProviderConfigReadModel?>(null);

    public Task<AppleProviderConfigReadModel> UpsertAppleProviderConfigAsync(Guid projectId, AppleProviderConfigWriteModel writeModel, CancellationToken ct = default)
        => Task.FromResult(new AppleProviderConfigReadModel(
            BundleId: writeModel.BundleId,
            AppAppleId: writeModel.AppAppleId,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow));

    public Task<GoogleProviderConfigReadModel?> GetGoogleProviderConfigAsync(Guid projectId, CancellationToken ct = default)
        => Task.FromResult<GoogleProviderConfigReadModel?>(null);

    public Task<GoogleProviderCredentialReadModel?> GetGoogleProviderCredentialAsync(Guid projectId, CancellationToken ct = default)
        => Task.FromResult<GoogleProviderCredentialReadModel?>(null);

    public Task<GoogleProviderConfigReadModel> UpsertGoogleProviderConfigAsync(Guid projectId, GoogleProviderConfigWriteModel writeModel, ValidatedGoogleServiceAccount? validatedServiceAccount, CancellationToken ct = default)
        => Task.FromResult(new GoogleProviderConfigReadModel(
            PackageName: writeModel.PackageName,
            PubSubAuthServiceAccountEmail: writeModel.PubSubAuthServiceAccountEmail,
            HasServiceAccountCredentials: !string.IsNullOrWhiteSpace(writeModel.ServiceAccountJson),
            ServiceAccountClientEmail: validatedServiceAccount?.ClientEmail,
            ServiceAccountKeyId: validatedServiceAccount?.PrivateKeyId,
            ServiceAccountCreatedAt: DateTimeOffset.UtcNow,
            ServiceAccountLastValidatedAt: validatedServiceAccount?.ValidatedAt,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow));
}

internal sealed class SuccessFakeProviderConfigRepository : IProviderConfigRepository
{
    public Task<AppleProviderConfigReadModel?> GetAppleProviderConfigAsync(Guid projectId, CancellationToken ct = default)
        => Task.FromResult<AppleProviderConfigReadModel?>(new AppleProviderConfigReadModel(
            BundleId: "com.acme.existing",
            AppAppleId: 9876543210,
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-5),
            UpdatedAt: DateTimeOffset.UtcNow));

    public Task<AppleProviderConfigReadModel> UpsertAppleProviderConfigAsync(Guid projectId, AppleProviderConfigWriteModel writeModel, CancellationToken ct = default)
        => Task.FromResult(new AppleProviderConfigReadModel(
            BundleId: writeModel.BundleId,
            AppAppleId: writeModel.AppAppleId,
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt: DateTimeOffset.UtcNow));

    public Task<GoogleProviderConfigReadModel?> GetGoogleProviderConfigAsync(Guid projectId, CancellationToken ct = default)
        => Task.FromResult<GoogleProviderConfigReadModel?>(new GoogleProviderConfigReadModel(
            PackageName: "com.acme.existing",
            PubSubAuthServiceAccountEmail: "push-auth@acme.iam.gserviceaccount.com",
            HasServiceAccountCredentials: true,
            ServiceAccountClientEmail: "svc-existing@play-project.iam.gserviceaccount.com",
            ServiceAccountKeyId: "key-existing",
            ServiceAccountCreatedAt: DateTimeOffset.UtcNow.AddDays(-10),
            ServiceAccountLastValidatedAt: DateTimeOffset.UtcNow.AddHours(-2),
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-5),
            UpdatedAt: DateTimeOffset.UtcNow));

    public Task<GoogleProviderCredentialReadModel?> GetGoogleProviderCredentialAsync(Guid projectId, CancellationToken ct = default)
        => Task.FromResult<GoogleProviderCredentialReadModel?>(new GoogleProviderCredentialReadModel(
            ServiceAccountJson: FakeGoogleServiceAccountValidator.ValidJson,
            ClientEmail: "svc-existing@play-project.iam.gserviceaccount.com",
            PrivateKeyId: "key-existing",
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-10),
            LastValidatedAt: DateTimeOffset.UtcNow.AddHours(-2)));

    public Task<GoogleProviderConfigReadModel> UpsertGoogleProviderConfigAsync(Guid projectId, GoogleProviderConfigWriteModel writeModel, ValidatedGoogleServiceAccount? validatedServiceAccount, CancellationToken ct = default)
    {
        var hasNewCredential = !string.IsNullOrWhiteSpace(writeModel.ServiceAccountJson);

        return Task.FromResult(new GoogleProviderConfigReadModel(
            PackageName: writeModel.PackageName,
            PubSubAuthServiceAccountEmail: writeModel.PubSubAuthServiceAccountEmail,
            HasServiceAccountCredentials: true,
            ServiceAccountClientEmail: hasNewCredential
                ? validatedServiceAccount?.ClientEmail
                : "svc-existing@play-project.iam.gserviceaccount.com",
            ServiceAccountKeyId: hasNewCredential
                ? validatedServiceAccount?.PrivateKeyId
                : "key-existing",
            ServiceAccountCreatedAt: hasNewCredential
                ? DateTimeOffset.UtcNow
                : DateTimeOffset.UtcNow.AddDays(-10),
            ServiceAccountLastValidatedAt: hasNewCredential
                ? validatedServiceAccount?.ValidatedAt
                : DateTimeOffset.UtcNow.AddHours(-2),
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt: DateTimeOffset.UtcNow));
    }
}
