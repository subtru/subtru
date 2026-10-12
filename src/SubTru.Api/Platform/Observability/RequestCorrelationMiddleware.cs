using System.Diagnostics;
using Microsoft.Extensions.Primitives;

namespace SubTru.Api.Platform.Observability;

public sealed class RequestCorrelationMiddleware
{
    public const string RequestIdHeaderName = "x-request-id";
    public const int MaxRequestIdLength = 128;

    private readonly RequestDelegate _next;
    private readonly ILogger<RequestCorrelationMiddleware> _logger;

    public RequestCorrelationMiddleware(
        RequestDelegate next,
        ILogger<RequestCorrelationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = GetValidRequestId(context.Request.Headers[RequestIdHeaderName])
            ?? CreateRequestId();

        context.TraceIdentifier = requestId;

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[RequestIdHeaderName] = requestId;
            return Task.CompletedTask;
        });

        var traceId = Activity.Current?.TraceId.ToString();
        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["request_id"] = requestId,
            ["trace_id"] = string.IsNullOrWhiteSpace(traceId) ? null : traceId
        });

        await _next(context);
    }

    internal static string? GetValidRequestId(StringValues values)
    {
        if (values.Count != 1)
        {
            return null;
        }

        var candidate = values[0];
        if (string.IsNullOrWhiteSpace(candidate)
            || candidate.Length > MaxRequestIdLength)
        {
            return null;
        }

        foreach (var character in candidate)
        {
            if (!IsAllowedRequestIdCharacter(character))
            {
                return null;
            }
        }

        return candidate;
    }

    private static string CreateRequestId()
        => Guid.NewGuid().ToString("N");

    private static bool IsAllowedRequestIdCharacter(char character)
        => character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '-'
            or '_'
            or '.'
            or ':';
}
