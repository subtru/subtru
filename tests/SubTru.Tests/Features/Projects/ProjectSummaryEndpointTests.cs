using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubTru.Api.Platform.Authentication;
using SubTru.Infrastructure.Persistence.Postgres.ApiKeys;
using SubTru.Infrastructure.Persistence.Postgres.Authentication;
using SubTru.Infrastructure.Persistence.Postgres.Projects;

namespace SubTru.Tests.Features.Projects;

public sealed class ProjectSummaryEndpointTests : IClassFixture<ProjectSummaryWebApplicationFactory>
{
    private const string ProjectId = "11111111-1111-1111-1111-111111111111";

    private readonly HttpClient _client;

    public ProjectSummaryEndpointTests(ProjectSummaryWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "subtru_live_test.secret");
    }

    [Fact]
    public async Task GetProjectSummary_WithApiKeyAuth_ReturnsForbidden()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/summary");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ApiErrorResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("forbidden", payload.Error);
        Assert.Equal(
            "Project summary access requires the installation admin API key.",
            payload.Message);
    }

    [Fact]
    public async Task GetProjectSummary_WithoutAuth_ReturnsUnauthorized()
    {
        using var unauthenticatedClient = new ProjectSummaryWebApplicationFactory().CreateClient();

        var response = await unauthenticatedClient.GetAsync($"/v1/projects/{ProjectId}/summary");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed record ProjectSummaryResponseDto(
        int UsersWithAccess,
        int ActiveSubscriptions,
        int ExpiredSubscriptions,
        DateTimeOffset? LastWebhookReceivedAt
    );

    private sealed record ApiErrorResponseDto(string Error, string Message);
}

public sealed class ProjectSummaryAdminEndpointTests : IClassFixture<ProjectSummaryAdminWebApplicationFactory>
{
    private const string ProjectId = "11111111-1111-1111-1111-111111111111";

    private readonly HttpClient _client;

    public ProjectSummaryAdminEndpointTests(ProjectSummaryAdminWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "fake-admin-token");
    }

    [Fact]
    public async Task GetProjectSummary_WithAdminAuth_ReturnsSummaryPayload()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ProjectSummaryResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(128, payload.UsersWithAccess);
        Assert.Equal(156, payload.ActiveSubscriptions);
        Assert.Equal(24, payload.ExpiredSubscriptions);
        Assert.Equal(new DateTimeOffset(2026, 2, 14, 21, 4, 2, TimeSpan.Zero), payload.LastWebhookReceivedAt);
    }

    private sealed record ProjectSummaryResponseDto(
        int UsersWithAccess,
        int ActiveSubscriptions,
        int ExpiredSubscriptions,
        DateTimeOffset? LastWebhookReceivedAt
    );
}

public sealed class ProjectSummaryWebApplicationFactory : WebApplicationFactory<Program>
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
            services.RemoveAll<IProjectSummaryRepository>();

            services.AddSingleton<IProjectAuthService>(new AllowAllProjectAuthServiceForProjectSummary());
            services.AddScoped<IProjectSummaryRepository, FakeProjectSummaryRepository>();
        });
    }
}

public sealed class ProjectSummaryAdminWebApplicationFactory : WebApplicationFactory<Program>
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
            services.RemoveAll<IProjectSummaryRepository>();

            services.AddScoped<IProjectResolver>(_ => new FakeAdminProjectResolverForProjectSummary(
                Guid.Parse("22222222-2222-2222-2222-222222222222")));
            services.AddScoped<IProjectSummaryRepository, FakeProjectSummaryRepository>();
        });
    }
}

internal sealed class AllowAllProjectAuthServiceForProjectSummary : IProjectAuthService
{
    public Task<ValidatedApiKey?> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
        => Task.FromResult<ValidatedApiKey?>(new ValidatedApiKey(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "production"));
}

internal sealed class FakeAdminProjectResolverForProjectSummary : IProjectResolver
{
    private readonly Guid _userId;

    public FakeAdminProjectResolverForProjectSummary(Guid userId) => _userId = userId;

    public Task<AuthResult?> ResolveAsync(string bearerToken, CancellationToken ct = default)
        => Task.FromResult<AuthResult?>(new AuthResult(
            UserId: _userId,
            ProjectId: null,
            IsApiKeyAuth: false,
            IsAdminAuth: true));
}

internal sealed class FakeProjectSummaryRepository : IProjectSummaryRepository
{
    public Task<ProjectSummaryReadModel> GetSummaryAsync(Guid projectId, string environment, CancellationToken ct)
        => Task.FromResult(new ProjectSummaryReadModel(
            UsersWithAccess: 128,
            ActiveSubscriptions: 156,
            ExpiredSubscriptions: 24,
            LastWebhookReceivedAt: new DateTimeOffset(2026, 2, 14, 21, 4, 2, TimeSpan.Zero)
        ));
}
