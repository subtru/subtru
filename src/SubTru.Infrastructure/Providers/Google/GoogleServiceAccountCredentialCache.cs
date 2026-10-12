using Google.Apis.Auth.OAuth2;

namespace SubTru.Infrastructure.Providers.Google;

internal sealed class GoogleServiceAccountCredentialCache : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly int _maxEntries;
    private readonly TimeSpan _idleExpiration;
    private readonly ITimer? _cleanupTimer;
    private bool _disposed;

    public GoogleServiceAccountCredentialCache(
        TimeProvider timeProvider,
        int maxEntries,
        TimeSpan idleExpiration,
        TimeSpan cleanupInterval,
        bool startCleanupTimer = true)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleExpiration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cleanupInterval, TimeSpan.Zero);

        _timeProvider = timeProvider;
        _maxEntries = maxEntries;
        _idleExpiration = idleExpiration;

        if (startCleanupTimer)
        {
            _cleanupTimer = timeProvider.CreateTimer(
                _ => RemoveExpired(),
                state: null,
                dueTime: cleanupInterval,
                period: cleanupInterval);
        }
    }

    internal ITokenAccess GetOrAdd(string key, Func<ITokenAccess> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var now = _timeProvider.GetUtcNow();
            RemoveExpired(now);

            if (_entries.TryGetValue(key, out var existing))
            {
                existing.LastAccessedAt = now;
                return existing.TokenAccess;
            }

            var tokenAccess = factory()
                ?? throw new InvalidOperationException("Google credential factory returned null.");

            if (_entries.Count >= _maxEntries)
            {
                var leastRecentlyUsed = _entries.MinBy(entry => entry.Value.LastAccessedAt);
                _entries.Remove(leastRecentlyUsed.Key);
            }

            _entries.Add(key, new CacheEntry(tokenAccess, now));
            return tokenAccess;
        }
    }

    internal int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    internal bool Contains(string key)
    {
        lock (_lock)
        {
            return _entries.ContainsKey(key);
        }
    }

    internal void RemoveExpired()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            RemoveExpired(_timeProvider.GetUtcNow());
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cleanupTimer?.Dispose();
            _entries.Clear();
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        var expiredKeys = _entries
            .Where(entry => now - entry.Value.LastAccessedAt >= _idleExpiration)
            .Select(entry => entry.Key)
            .ToArray();

        foreach (var key in expiredKeys)
        {
            _entries.Remove(key);
        }
    }

    private sealed class CacheEntry(ITokenAccess tokenAccess, DateTimeOffset lastAccessedAt)
    {
        public ITokenAccess TokenAccess { get; } = tokenAccess;

        public DateTimeOffset LastAccessedAt { get; set; } = lastAccessedAt;
    }
}
