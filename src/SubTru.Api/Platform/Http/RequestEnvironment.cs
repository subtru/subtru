using SubTru.Api.Platform.Authentication;
using SubTru.Domain.Subscriptions;

namespace SubTru.Api.Platform.Http;

public static class RequestEnvironment
{
    public static bool TryResolve(
        HttpContext context,
        string? requestedEnvironment,
        out string environment,
        out ApiErrorResponse? error)
    {
        environment = SubscriptionEnvironment.Production;
        error = null;

        var projectContext = context.GetRequiredProjectContext();
        var normalizedRequest = Normalize(requestedEnvironment);
        if (normalizedRequest is null && !string.IsNullOrWhiteSpace(requestedEnvironment))
        {
            error = new ApiErrorResponse(
                "invalid_environment",
                "Query parameter 'environment' must be one of: production, test.");
            return false;
        }

        if (projectContext.IsApiKeyAuth)
        {
            environment = projectContext.Environment ?? SubscriptionEnvironment.Production;

            if (normalizedRequest is not null
                && !string.Equals(normalizedRequest, environment, StringComparison.Ordinal))
            {
                error = new ApiErrorResponse(
                    "environment_mismatch",
                    "The requested environment does not match this API key.");
                return false;
            }

            return true;
        }

        environment = normalizedRequest ?? SubscriptionEnvironment.Production;
        return true;
    }

    public static string? Normalize(string? environment)
    {
        if (string.IsNullOrWhiteSpace(environment))
        {
            return null;
        }

        var normalized = environment.Trim().ToLowerInvariant();
        return SubscriptionEnvironment.IsValid(normalized) ? normalized : null;
    }
}
