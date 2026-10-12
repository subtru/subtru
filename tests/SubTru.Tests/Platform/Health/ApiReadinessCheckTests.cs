using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SubTru.Api.Platform.Health;
using SubTru.Infrastructure.Persistence.Postgres;

namespace SubTru.Tests.Platform.Health;

public sealed class ApiReadinessCheckTests
{
    [Fact]
    public async Task CheckAsync_WhenRequiredRuntimeConfigurationIsMissing_ReturnsNotReadyAndSkipsPostgres()
    {
        var config = BuildConfiguration(("WebhookUrls:PublicBaseUrl", null));
        var postgresProbe = new SequencePostgresReadinessProbe(true);
        var readinessCheck = CreateReadinessCheck(config, postgresProbe);

        var result = await readinessCheck.CheckAsync();

        Assert.False(result.IsReady);
        Assert.Equal(0, postgresProbe.CallCount);
    }

    [Fact]
    public async Task CheckAsync_WhenEncryptionKeyIsMissing_ReturnsNotReadyAndSkipsPostgres()
    {
        var config = BuildConfiguration(("SUBTRU_ENCRYPTION_KEY", null));
        var postgresProbe = new SequencePostgresReadinessProbe(true);
        var readinessCheck = CreateReadinessCheck(config, postgresProbe);

        var result = await readinessCheck.CheckAsync();

        Assert.False(result.IsReady);
        Assert.Equal(0, postgresProbe.CallCount);
    }

    [Fact]
    public async Task CheckAsync_WhenPostgresProbeReturnsFalse_ReturnsNotReady()
    {
        var postgresProbe = new SequencePostgresReadinessProbe(false);
        var readinessCheck = CreateReadinessCheck(BuildConfiguration(), postgresProbe);

        var result = await readinessCheck.CheckAsync();

        Assert.False(result.IsReady);
        Assert.Equal(1, postgresProbe.CallCount);
    }

    [Fact]
    public async Task CheckAsync_WhenPostgresProbeThrows_ReturnsNotReady()
    {
        var postgresProbe = new ThrowingPostgresReadinessProbe();
        var readinessCheck = CreateReadinessCheck(BuildConfiguration(), postgresProbe);

        var result = await readinessCheck.CheckAsync();

        Assert.False(result.IsReady);
        Assert.Equal(1, postgresProbe.CallCount);
    }

    [Fact]
    public async Task CheckAsync_WhenPostgresIsReady_ReturnsReady()
    {
        var postgresProbe = new SequencePostgresReadinessProbe(true);
        var readinessCheck = CreateReadinessCheck(BuildConfiguration(), postgresProbe);

        var result = await readinessCheck.CheckAsync();

        Assert.True(result.IsReady);
        Assert.Equal(1, postgresProbe.CallCount);
    }

    [Fact]
    public async Task CheckAsync_CachesReadinessResultForFiveSeconds()
    {
        var timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero));
        var postgresProbe = new SequencePostgresReadinessProbe(false, true);
        var readinessCheck = CreateReadinessCheck(BuildConfiguration(), postgresProbe, timeProvider);

        var firstResult = await readinessCheck.CheckAsync();

        timeProvider.Advance(TimeSpan.FromSeconds(4));
        var cachedResult = await readinessCheck.CheckAsync();

        timeProvider.Advance(TimeSpan.FromSeconds(2));
        var refreshedResult = await readinessCheck.CheckAsync();

        Assert.False(firstResult.IsReady);
        Assert.False(cachedResult.IsReady);
        Assert.True(refreshedResult.IsReady);
        Assert.Equal(2, postgresProbe.CallCount);
    }

    [Fact]
    public async Task NpgsqlPostgresReadinessProbe_WhenPostgresIsUnavailable_ReturnsFalse()
    {
        var connectionFactory = new NpgsqlConnectionFactory(
            "Host=127.0.0.1;Port=1;Database=subtru_missing;Username=test;Password=test;Timeout=1;Command Timeout=1");

        var probe = new NpgsqlPostgresReadinessProbe(
            connectionFactory,
            NullLogger<NpgsqlPostgresReadinessProbe>.Instance);

        var result = await probe.IsReadyAsync();

        Assert.False(result);
        Assert.Equal(TimeSpan.FromSeconds(2), NpgsqlPostgresReadinessProbe.ReadinessTimeout);
    }

    private static ApiReadinessCheck CreateReadinessCheck(
        IConfiguration configuration,
        IPostgresReadinessProbe postgresProbe,
        TimeProvider? timeProvider = null)
        => new(
            configuration,
            postgresProbe,
            timeProvider ?? TimeProvider.System,
            NullLogger<ApiReadinessCheck>.Instance);

    private static IConfiguration BuildConfiguration(params (string Key, string? Value)[] overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Port=5432;Database=subtru_test;Username=test;Password=test",
            ["API_KEY_PEPPER"] = "test-pepper",
            ["SUBTRU_ENCRYPTION_KEY"] = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            ["WebhookUrls:PublicBaseUrl"] = "https://api.test.subtru.com",
            ["AppleVerification:RootCertificateDirectory"] = TestAppleTrustStoreDirectory.DirectoryPath
        };

        foreach (var (key, value) in overrides)
        {
            if (value is null)
            {
                values.Remove(key);
            }
            else
            {
                values[key] = value;
            }
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private sealed class SequencePostgresReadinessProbe : IPostgresReadinessProbe
    {
        private readonly Queue<bool> _results;

        public SequencePostgresReadinessProbe(params bool[] results)
        {
            _results = new Queue<bool>(results);
        }

        public int CallCount { get; private set; }

        public Task<bool> IsReadyAsync(CancellationToken ct = default)
        {
            CallCount++;

            if (_results.Count == 0)
            {
                return Task.FromResult(false);
            }

            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class ThrowingPostgresReadinessProbe : IPostgresReadinessProbe
    {
        public int CallCount { get; private set; }

        public Task<bool> IsReadyAsync(CancellationToken ct = default)
        {
            CallCount++;
            throw new InvalidOperationException("Host=secret-db;Password=secret-password");
        }
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public MutableTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan value)
        {
            _utcNow = _utcNow.Add(value);
        }
    }
}
