using System.Net;
using SubTru.Api.Platform.Http;

namespace SubTru.Api.Platform.Authentication;

public sealed class ApiKeyAuthMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next;

    public async Task InvokeAsync(
        HttpContext context,
        IEnumerable<IProjectResolver> resolvers,
        ILogger<ApiKeyAuthMiddleware> logger)
    {
        // Allow unauthenticated endpoints
        var path = context.Request.Path.Value ?? string.Empty;

        if (IsHealthPath(path) ||
            path.StartsWith("/webhooks/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (!TryGetBearerToken(context.Request, out var token))
        {
            await WriteJsonError(
                context,
                statusCode: (int)HttpStatusCode.Unauthorized,
                error: "missing_api_key",
                message: "Missing API key. Use 'Authorization: Bearer ...'."
            );
            return;
        }

        AuthResult? authResult = null;
        foreach (var resolver in resolvers)
        {
            authResult = await resolver.ResolveAsync(token!, context.RequestAborted);
            if (authResult is not null)
            {
                break;
            }
        }

        if (authResult is null)
        {
            logger.LogWarning("Invalid bearer token presented.");

            await WriteJsonError(
                context,
                statusCode: (int)HttpStatusCode.Unauthorized,
                error: "invalid_api_key",
                message: "Invalid API key."
            );
            return;
        }

        // Check {projectId} in route
        if (context.Request.RouteValues.TryGetValue("projectId", out var projectIdRaw)
            && Guid.TryParse(projectIdRaw?.ToString(), out var parsedProjectId))
        {
            if (authResult.IsApiKeyAuth)
            {
                // API key auth: the key must belong to the requested project
                if (authResult.ProjectId != parsedProjectId)
                {
                    logger.LogWarning(
                        "API key for project {KeyProjectId} used on project {RouteProjectId}.",
                        authResult.ProjectId, parsedProjectId);

                    await WriteJsonError(
                        context,
                        statusCode: (int)HttpStatusCode.Forbidden,
                        error: "forbidden",
                        message: "This API key does not have access to the requested project."
                    );
                    return;
                }
            }
        }

        context.Items[AuthConstants.ProjectContextItemKey] = new ProjectContext(
            UserId: authResult.UserId,
            IsApiKeyAuth: authResult.IsApiKeyAuth,
            Environment: authResult.Environment,
            Email: authResult.Email,
            IsAdminAuth: authResult.IsAdminAuth);

        await _next(context);
    }

    private static bool IsHealthPath(string path)
        => path.Equals("/health/live", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/health/ready", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetBearerToken(HttpRequest request, out string? token)
    {
        token = null;

        if (!request.Headers.TryGetValue("Authorization", out var authHeaderValues))
            return false;

        var authHeader = authHeaderValues.ToString();
        if (string.IsNullOrWhiteSpace(authHeader))
            return false;

        // Expect: "Bearer ..."
        const string prefix = "Bearer ";
        if (!authHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var candidate = authHeader.Substring(prefix.Length).Trim();
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        token = candidate;
        return true;
    }

    private static async Task WriteJsonError(HttpContext context, int statusCode, string error, string message)
    {
        context.Response.StatusCode = statusCode;

        if (statusCode == (int)HttpStatusCode.Unauthorized)
        {
            context.Response.Headers["WWW-Authenticate"] = "Bearer";
        }

        var payload = new ApiErrorResponse(error, message);
        await context.Response.WriteAsJsonAsync(payload);
    }
}
