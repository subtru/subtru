using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubTru.Api.Platform.Health;

namespace SubTru.Tests.Platform.Health;

public sealed class HealthEndpointTests
{
    [Theory]
    [InlineData("/health/live")]
    public async Task LivenessEndpoints_ReturnOk(string path)
    {
        using var factory = new HealthEndpointWebApplicationFactory(isReady: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.Equal("ok", payload?.Status);
    }

    [Fact]
    public async Task Ready_WhenApiIsReady_ReturnsReady()
    {
        using var factory = new HealthEndpointWebApplicationFactory(isReady: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.Equal("ready", payload?.Status);
    }

    [Fact]
    public async Task Ready_WhenApiIsNotReady_ReturnsServiceUnavailable()
    {
        using var factory = new HealthEndpointWebApplicationFactory(isReady: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.Equal("not_ready", payload?.Status);
    }

    [Fact]
    public async Task Ready_WhenApiIsNotReady_ResponseDoesNotLeakDetails()
    {
        using var factory = new HealthEndpointWebApplicationFactory(isReady: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(HealthEndpointWebApplicationFactory.SecretMarker, body);
        Assert.DoesNotContain("Host=", body);
        Assert.DoesNotContain("Exception", body);

        using var json = JsonDocument.Parse(body);
        var properties = json.RootElement.EnumerateObject().ToArray();

        Assert.Single(properties);
        Assert.Equal("status", properties[0].Name);
        Assert.Equal("not_ready", properties[0].Value.GetString());
    }

    [Fact]
    public void Startup_WhenAdminApiKeyIsMissing_ThrowsConfigurationError()
    {
        using var factory = new MissingAdminApiKeyWebApplicationFactory();

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            using var client = factory.CreateClient();
        });

        Assert.Contains("SUBTRU_ADMIN_API_KEY", exception.Message);
    }

    private sealed record HealthResponse(string Status);
}

public sealed class MissingAdminApiKeyWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(
            "ConnectionStrings:Postgres",
            "Host=localhost;Port=5432;Database=subtru_test;Username=test;Password=test");
        builder.UseSetting("SUBTRU_ADMIN_API_KEY", " ");
    }
}

public sealed class HealthEndpointWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string SecretMarker = "health-endpoint-secret-marker";

    private readonly bool _isReady;

    public HealthEndpointWebApplicationFactory(bool isReady)
    {
        _isReady = isReady;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        TestWebHostConfiguration.UseAdminApiKey(builder);
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            var config = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = $"Host=localhost;Port=5432;Database=subtru_test;Username=test;Password={SecretMarker}",
                ["API_KEY_PEPPER"] = $"pepper-{SecretMarker}",
                ["SUBTRU_ENCRYPTION_KEY"] = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                ["WebhookUrls:PublicBaseUrl"] = "https://api.test.subtru.com",
                ["AppleVerification:RootCertificateDirectory"] = TestAppleTrustStoreDirectory.DirectoryPath,
                ["SUBTRU_ADMIN_API_KEY"] = "subtru_admin_0123456789abcdef0123456789abcdef"
            };

            configBuilder.AddInMemoryCollection(config);
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IApiReadinessCheck>();
            services.AddSingleton<IApiReadinessCheck>(new FakeApiReadinessCheck(_isReady));
        });
    }

    private sealed class FakeApiReadinessCheck : IApiReadinessCheck
    {
        private readonly bool _isReady;

        public FakeApiReadinessCheck(bool isReady)
        {
            _isReady = isReady;
        }

        public Task<ReadinessResult> CheckAsync(CancellationToken ct = default)
            => Task.FromResult(new ReadinessResult(_isReady));
    }
}
