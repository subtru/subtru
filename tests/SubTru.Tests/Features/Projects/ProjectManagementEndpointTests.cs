using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubTru.Api.Features.Projects;
using SubTru.Api.Platform.Authentication;
using SubTru.Infrastructure.Persistence.Postgres.ApiKeys;
using SubTru.Infrastructure.Persistence.Postgres.Authentication;
using SubTru.Infrastructure.Persistence.Postgres.Projects;

namespace SubTru.Tests.Features.Projects;

public sealed class ProjectManagementEndpointTests
    : IClassFixture<ProjectManagementWebApplicationFactory>
{
    private readonly ProjectManagementWebApplicationFactory _factory;

    public ProjectManagementEndpointTests(ProjectManagementWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.Repository.Reset();
    }

    [Fact]
    public async Task ListProjects_WithAdminAuthentication_ReturnsAllProjects()
    {
        using var client = CreateAdminClient();

        var response = await client.GetAsync("/v1/projects");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ProjectListResponse>();
        Assert.NotNull(body);
        Assert.Single(body.Items);
    }

    [Fact]
    public async Task CreateProject_NormalizesSlug()
    {
        using var client = CreateAdminClient();

        var response = await client.PostAsJsonAsync("/v1/projects", new
        {
            name = "  Mobile App  ",
            slug = "  MOBILE-APP  "
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ProjectDto>();
        Assert.NotNull(body);
        Assert.Equal("Mobile App", body.Name);
        Assert.Equal("mobile-app", body.Slug);
        Assert.True(body.IsActive);
    }

    [Fact]
    public async Task CreateProject_AllowsDuplicateNameAndSlug()
    {
        using var client = CreateAdminClient();
        var request = new
        {
            name = "Shared Label",
            slug = "shared-label"
        };

        var firstResponse = await client.PostAsJsonAsync("/v1/projects", request);
        var secondResponse = await client.PostAsJsonAsync("/v1/projects", request);

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        var first = await firstResponse.Content.ReadFromJsonAsync<ProjectDto>();
        var second = await secondResponse.Content.ReadFromJsonAsync<ProjectDto>();
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task CreateProject_WithInvalidSlug_ReturnsBadRequest()
    {
        using var client = CreateAdminClient();

        var response = await client.PostAsJsonAsync("/v1/projects", new
        {
            name = "Mobile App",
            slug = "mobile_app"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdateProject_CanDeactivateProject()
    {
        using var client = CreateAdminClient();
        var projectId = _factory.Repository.Projects.Single().Id;

        var response = await client.PutAsJsonAsync($"/v1/projects/{projectId}", new
        {
            name = "Archived App",
            slug = "archived-app",
            isActive = false
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ProjectDto>();
        Assert.NotNull(body);
        Assert.False(body.IsActive);
    }

    [Fact]
    public async Task ProjectApiKey_CannotManageProjects()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "project-key");

        var response = await client.GetAsync("/v1/projects");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private HttpClient CreateAdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "admin-key");
        return client;
    }
}

public sealed class ProjectManagementWebApplicationFactory : WebApplicationFactory<Program>
{
    internal FakeProjectManagementRepository Repository { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        TestWebHostConfiguration.UseAdminApiKey(builder);
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=localhost;Database=subtru_test;Username=test;Password=test",
                ["API_KEY_PEPPER"] = "test-pepper",
                ["SUBTRU_ENCRYPTION_KEY"] = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                ["WebhookUrls:PublicBaseUrl"] = "https://api.test.subtru.com",
                ["AppleVerification:RootCertificateDirectory"] = TestAppleTrustStoreDirectory.DirectoryPath
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IProjectResolver>();
            services.RemoveAll<IProjectManagementRepository>();
            services.AddSingleton<IProjectResolver, FakeProjectManagementResolver>();
            services.AddSingleton<IProjectManagementRepository>(Repository);
        });
    }
}

internal sealed class FakeProjectManagementResolver : IProjectResolver
{
    public Task<AuthResult?> ResolveAsync(string bearerToken, CancellationToken ct = default)
        => Task.FromResult<AuthResult?>(bearerToken switch
        {
            "admin-key" => new AuthResult(
                UserId: Guid.Empty,
                ProjectId: null,
                IsApiKeyAuth: false,
                IsAdminAuth: true),
            "project-key" => new AuthResult(
                UserId: null,
                ProjectId: Guid.NewGuid(),
                IsApiKeyAuth: true),
            _ => null
        });
}

internal sealed class FakeProjectManagementRepository : IProjectManagementRepository
{
    public List<ProjectListItem> Projects { get; } = [];

    public void Reset()
    {
        Projects.Clear();
        var now = DateTimeOffset.UtcNow;
        Projects.Add(new ProjectListItem(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "Example App",
            "example-app",
            true,
            now,
            now));
    }

    public Task<IReadOnlyList<ProjectListItem>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ProjectListItem>>(Projects.ToList());

    public Task<ProjectListItem> CreateAsync(
        ProjectWriteModel project,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var created = new ProjectListItem(
            Guid.NewGuid(),
            project.Name,
            project.Slug,
            project.IsActive,
            now,
            now);
        Projects.Add(created);
        return Task.FromResult(created);
    }

    public Task<ProjectListItem?> UpdateAsync(
        Guid projectId,
        ProjectWriteModel project,
        CancellationToken ct = default)
    {
        var index = Projects.FindIndex(item => item.Id == projectId);
        if (index < 0)
        {
            return Task.FromResult<ProjectListItem?>(null);
        }

        var updated = Projects[index] with
        {
            Name = project.Name,
            Slug = project.Slug,
            IsActive = project.IsActive,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        Projects[index] = updated;
        return Task.FromResult<ProjectListItem?>(updated);
    }
}
