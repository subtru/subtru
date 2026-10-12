using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SubTru.Tests.Platform.Observability;

public sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    public IReadOnlyCollection<CapturedLogEntry> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName)
        => new CapturingLogger(categoryName, _entries, () => _scopeProvider);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
        => _scopeProvider = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(
        string category,
        ConcurrentQueue<CapturedLogEntry> entries,
        Func<IExternalScopeProvider> scopeProvider)
        : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => scopeProvider().Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var scopes = new List<IReadOnlyDictionary<string, object?>>();
            scopeProvider().ForEachScope((scope, capturedScopes) =>
            {
                capturedScopes.Add(ToDictionary(scope));
            }, scopes);

            entries.Enqueue(new CapturedLogEntry(
                Category: category,
                Level: logLevel,
                EventId: eventId,
                Message: formatter(state, exception),
                State: ToDictionary(state),
                Scopes: scopes,
                Exception: exception));
        }
    }

    private static Dictionary<string, object?> ToDictionary(object? value)
    {
        if (value is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            return pairs.ToDictionary(pair => pair.Key, pair => pair.Value);
        }

        return [];
    }
}

public sealed record CapturedLogEntry(
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    IReadOnlyDictionary<string, object?> State,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Scopes,
    Exception? Exception);
