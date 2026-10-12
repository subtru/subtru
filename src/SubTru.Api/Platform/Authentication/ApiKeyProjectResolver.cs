using SubTru.Infrastructure.Persistence.Postgres.Authentication;

namespace SubTru.Api.Platform.Authentication;

public sealed class ApiKeyProjectResolver : IProjectResolver
{
    private readonly IProjectAuthService _authService;

    public ApiKeyProjectResolver(IProjectAuthService authService)
    {
        _authService = authService;
    }

    public async Task<AuthResult?> ResolveAsync(string bearerToken, CancellationToken ct = default)
    {
        if (!LooksLikeApiKey(bearerToken))
        {
            return null;
        }

        var apiKey = await _authService.ValidateApiKeyAsync(bearerToken, ct);
        if (apiKey is null)
        {
            return null;
        }

        return new AuthResult(
            UserId: null,
            ProjectId: apiKey.ProjectId,
            IsApiKeyAuth: true,
            Environment: apiKey.Environment);
    }

    private static bool LooksLikeApiKey(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        if (!token.StartsWith("subtru_live_", StringComparison.OrdinalIgnoreCase) &&
            !token.StartsWith("subtru_test_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var dotIndex = token.IndexOf('.');
        return dotIndex > 0 && dotIndex < token.Length - 1;
    }
}
