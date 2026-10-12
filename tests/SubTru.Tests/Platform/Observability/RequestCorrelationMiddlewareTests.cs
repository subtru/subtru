using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using SubTru.Api.Platform.Observability;
using SubTru.Api.Platform.Authentication;

namespace SubTru.Tests.Platform.Observability;

public sealed class RequestCorrelationMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ValidRequestId_PreservesRequestIdInTraceIdentifierResponseHeaderAndScope()
    {
        var logger = new CapturingLogger<RequestCorrelationMiddleware>();
        var context = CreateContext();
        context.Request.Headers[RequestCorrelationMiddleware.RequestIdHeaderName] = "client-req_123.abc:xyz";
        var middleware = CreateMiddleware(logger);

        await middleware.InvokeAsync(context);
        await StartResponseAsync(context);

        Assert.Equal("client-req_123.abc:xyz", context.TraceIdentifier);
        Assert.Equal("client-req_123.abc:xyz", context.Response.Headers[RequestCorrelationMiddleware.RequestIdHeaderName].ToString());
        var scope = Assert.Single(logger.Scopes);
        Assert.Equal("client-req_123.abc:xyz", scope["request_id"]);
        Assert.Null(scope["trace_id"]);
    }

    [Fact]
    public async Task InvokeAsync_MissingRequestId_GeneratesReplacementAndReturnsIt()
    {
        var logger = new CapturingLogger<RequestCorrelationMiddleware>();
        var context = CreateContext();
        var middleware = CreateMiddleware(logger);

        await middleware.InvokeAsync(context);
        await StartResponseAsync(context);

        AssertGeneratedRequestId(context.TraceIdentifier);
        Assert.Equal(context.TraceIdentifier, context.Response.Headers[RequestCorrelationMiddleware.RequestIdHeaderName].ToString());
        Assert.Equal(context.TraceIdentifier, Assert.Single(logger.Scopes)["request_id"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("contains space")]
    [InlineData("contains/slash")]
    public async Task InvokeAsync_InvalidRequestId_ReplacesIt(string requestId)
    {
        var logger = new CapturingLogger<RequestCorrelationMiddleware>();
        var context = CreateContext();
        context.Request.Headers[RequestCorrelationMiddleware.RequestIdHeaderName] = requestId;
        var middleware = CreateMiddleware(logger);

        await middleware.InvokeAsync(context);
        await StartResponseAsync(context);

        AssertGeneratedRequestId(context.TraceIdentifier);
        Assert.NotEqual(requestId, context.TraceIdentifier);
        Assert.Equal(context.TraceIdentifier, context.Response.Headers[RequestCorrelationMiddleware.RequestIdHeaderName].ToString());
    }

    [Fact]
    public async Task InvokeAsync_DuplicatedRequestId_ReplacesIt()
    {
        var logger = new CapturingLogger<RequestCorrelationMiddleware>();
        var context = CreateContext();
        context.Request.Headers[RequestCorrelationMiddleware.RequestIdHeaderName] = new[] { "first", "second" };
        var middleware = CreateMiddleware(logger);

        await middleware.InvokeAsync(context);
        await StartResponseAsync(context);

        AssertGeneratedRequestId(context.TraceIdentifier);
        Assert.NotEqual("first", context.TraceIdentifier);
        Assert.NotEqual("second", context.TraceIdentifier);
    }

    [Fact]
    public async Task InvokeAsync_TooLongRequestId_ReplacesIt()
    {
        var tooLong = new string('a', RequestCorrelationMiddleware.MaxRequestIdLength + 1);
        var logger = new CapturingLogger<RequestCorrelationMiddleware>();
        var context = CreateContext();
        context.Request.Headers[RequestCorrelationMiddleware.RequestIdHeaderName] = tooLong;
        var middleware = CreateMiddleware(logger);

        await middleware.InvokeAsync(context);
        await StartResponseAsync(context);

        AssertGeneratedRequestId(context.TraceIdentifier);
        Assert.NotEqual(tooLong, context.TraceIdentifier);
    }

    [Fact]
    public async Task InvokeAsync_ResponseAlwaysIncludesRequestIdAfterResponseStarts()
    {
        var logger = new CapturingLogger<RequestCorrelationMiddleware>();
        var context = CreateContext();
        var middleware = new RequestCorrelationMiddleware(
            StartResponseAsync,
            logger);

        await middleware.InvokeAsync(context);

        Assert.False(string.IsNullOrWhiteSpace(context.Response.Headers[RequestCorrelationMiddleware.RequestIdHeaderName].ToString()));
        Assert.Equal(context.TraceIdentifier, context.Response.Headers[RequestCorrelationMiddleware.RequestIdHeaderName].ToString());
    }

    [Fact]
    public async Task InvokeAsync_ActiveActivity_AddsTraceIdToScope()
    {
        var logger = new CapturingLogger<RequestCorrelationMiddleware>();
        var context = CreateContext();
        var middleware = CreateMiddleware(logger);
        using var activity = new Activity("request-correlation-test");
        activity.SetIdFormat(ActivityIdFormat.W3C);
        activity.Start();

        try
        {
            await middleware.InvokeAsync(context);
            await StartResponseAsync(context);
        }
        finally
        {
            activity.Stop();
        }

        var scope = Assert.Single(logger.Scopes);
        Assert.Equal(activity.TraceId.ToString(), scope["trace_id"]);
    }

    private static DefaultHttpContext CreateContext()
    {
        var features = new FeatureCollection();
        features.Set<IHttpRequestFeature>(new HttpRequestFeature());
        features.Set<IHttpResponseFeature>(new CapturingResponseFeature());
        return new DefaultHttpContext(features);
    }

    private static Task StartResponseAsync(HttpContext context)
    {
        var feature = Assert.IsType<CapturingResponseFeature>(
            context.Features.Get<IHttpResponseFeature>());
        return feature.FireOnStartingAsync();
    }

    private static RequestCorrelationMiddleware CreateMiddleware(
        CapturingLogger<RequestCorrelationMiddleware> logger)
        => new(_ => Task.CompletedTask, logger);

    private static void AssertGeneratedRequestId(string requestId)
    {
        Assert.Equal(32, requestId.Length);
        Assert.Matches("^[0-9a-f]{32}$", requestId);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<IReadOnlyDictionary<string, object?>> Scopes { get; } = new();

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                Scopes.Add(values.ToDictionary(pair => pair.Key, pair => pair.Value));
            }

            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed class CapturingResponseFeature : IHttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _startingCallbacks = new();

        public int StatusCode { get; set; } = StatusCodes.Status200OK;

        public string? ReasonPhrase { get; set; }

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public Stream Body { get; set; } = new MemoryStream();

        public bool HasStarted { get; private set; }

        public void OnStarting(Func<object, Task> callback, object state)
            => _startingCallbacks.Add((callback, state));

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }

        public async Task FireOnStartingAsync()
        {
            if (HasStarted)
            {
                return;
            }

            for (var index = _startingCallbacks.Count - 1; index >= 0; index--)
            {
                var (callback, state) = _startingCallbacks[index];
                await callback(state);
            }

            HasStarted = true;
        }
    }
}
