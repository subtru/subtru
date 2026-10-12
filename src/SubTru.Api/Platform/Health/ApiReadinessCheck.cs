namespace SubTru.Api.Platform.Health;

public sealed class ApiReadinessCheck(
    IConfiguration configuration,
    IPostgresReadinessProbe postgresProbe,
    TimeProvider timeProvider,
    ILogger<ApiReadinessCheck> logger) : IApiReadinessCheck
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(5);

    private static readonly string[] RequiredConfigurationKeys =
    [
        "ConnectionStrings:Postgres",
        "API_KEY_PEPPER",
        "SUBTRU_ENCRYPTION_KEY",
        "WebhookUrls:PublicBaseUrl",
        "AppleVerification:RootCertificateDirectory"
    ];

    private readonly IConfiguration _configuration = configuration;
    private readonly IPostgresReadinessProbe _postgresProbe = postgresProbe;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<ApiReadinessCheck> _logger = logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Lock _cacheGate = new();

    private CachedReadinessResult? _cachedResult;

    public async Task<ReadinessResult> CheckAsync(CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow();
        var cachedResult = GetCachedResult(now);
        if (cachedResult is not null)
        {
            return cachedResult;
        }

        await _refreshLock.WaitAsync(ct);
        try
        {
            now = _timeProvider.GetUtcNow();
            cachedResult = GetCachedResult(now);
            if (cachedResult is not null)
            {
                return cachedResult;
            }

            var result = await EvaluateAsync(ct);
            CacheResult(result, now);

            return result;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<ReadinessResult> EvaluateAsync(CancellationToken ct)
    {
        if (!HasRequiredConfiguration())
        {
            _logger.LogWarning("API readiness check failed because required configuration is missing.");
            return new ReadinessResult(IsReady: false);
        }

        try
        {
            var postgresReady = await _postgresProbe.IsReadyAsync(ct);
            return new ReadinessResult(postgresReady);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "API readiness check failed because PostgreSQL probe threw. exception_type={ExceptionType}",
                ex.GetType().Name);

            return new ReadinessResult(IsReady: false);
        }
    }

    private bool HasRequiredConfiguration()
        => RequiredConfigurationKeys.All(key => !string.IsNullOrWhiteSpace(_configuration[key]));

    private ReadinessResult? GetCachedResult(DateTimeOffset now)
    {
        lock (_cacheGate)
        {
            if (_cachedResult is not null && _cachedResult.ExpiresAtUtc > now)
            {
                return _cachedResult.Result;
            }
        }

        return null;
    }

    private void CacheResult(ReadinessResult result, DateTimeOffset evaluatedAtUtc)
    {
        var cacheEntry = new CachedReadinessResult(
            result,
            evaluatedAtUtc.Add(CacheDuration));

        lock (_cacheGate)
        {
            _cachedResult = cacheEntry;
        }
    }

    private sealed record CachedReadinessResult(
        ReadinessResult Result,
        DateTimeOffset ExpiresAtUtc);
}
