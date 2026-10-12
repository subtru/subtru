using Google.Apis.Auth.OAuth2;
using SubTru.Infrastructure.Providers.Google;

namespace SubTru.Tests.Infrastructure.Providers.Google;

public sealed class GoogleServiceAccountCredentialCacheTests
{
    private static readonly TimeSpan IdleExpiration = TimeSpan.FromHours(1);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

    [Fact]
    public void GetOrAdd_ReusesCredentialAndRefreshesIdleExpiration()
    {
        var timeProvider = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        using var cache = CreateCache(timeProvider, maxEntries: 2);
        var original = CreateTokenAccess("original");
        var replacement = CreateTokenAccess("replacement");

        var first = cache.GetOrAdd("credential", () => original);
        timeProvider.Advance(TimeSpan.FromMinutes(45));
        var second = cache.GetOrAdd("credential", () => replacement);
        timeProvider.Advance(TimeSpan.FromMinutes(45));
        cache.RemoveExpired();

        Assert.Same(original, first);
        Assert.Same(original, second);
        Assert.True(cache.Contains("credential"));
    }

    [Fact]
    public void RemoveExpired_RemovesCredentialAfterIdleExpiration()
    {
        var timeProvider = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        using var cache = CreateCache(timeProvider, maxEntries: 2);

        cache.GetOrAdd("old-credential", () => CreateTokenAccess("old"));
        timeProvider.Advance(IdleExpiration);
        cache.RemoveExpired();

        Assert.False(cache.Contains("old-credential"));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void GetOrAdd_AtCapacityEvictsLeastRecentlyUsedCredential()
    {
        var timeProvider = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        using var cache = CreateCache(timeProvider, maxEntries: 2);

        cache.GetOrAdd("credential-a", () => CreateTokenAccess("a"));
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        cache.GetOrAdd("credential-b", () => CreateTokenAccess("b"));
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        cache.GetOrAdd("credential-a", () => CreateTokenAccess("unused"));
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        cache.GetOrAdd("credential-c", () => CreateTokenAccess("c"));

        Assert.True(cache.Contains("credential-a"));
        Assert.False(cache.Contains("credential-b"));
        Assert.True(cache.Contains("credential-c"));
        Assert.Equal(2, cache.Count);
    }

    private static GoogleServiceAccountCredentialCache CreateCache(
        TimeProvider timeProvider,
        int maxEntries)
        => new(
            timeProvider,
            maxEntries,
            IdleExpiration,
            CleanupInterval,
            startCleanupTimer: false);

    private static ITokenAccess CreateTokenAccess(string token)
        => GoogleCredential.FromAccessToken(token).UnderlyingCredential;

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
