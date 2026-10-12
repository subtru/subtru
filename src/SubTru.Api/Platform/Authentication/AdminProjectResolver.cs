using System.Security.Cryptography;
using System.Text;

namespace SubTru.Api.Platform.Authentication;

/// <summary>
/// Authenticates the installation-wide administrator used by the self-hosted CLI and Console.
/// </summary>
public sealed class AdminProjectResolver : IProjectResolver
{
    private const string RequiredPrefix = "subtru_admin_";
    private const int MinimumSecretLength = 32;
    private readonly byte[] _configuredToken;

    public AdminProjectResolver(string? configuredToken)
    {
        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            throw new InvalidOperationException(
                "Missing required configuration: SUBTRU_ADMIN_API_KEY");
        }

        if (!configuredToken.StartsWith(RequiredPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"SUBTRU_ADMIN_API_KEY must start with '{RequiredPrefix}'.");
        }

        if (configuredToken.Length < RequiredPrefix.Length + MinimumSecretLength)
        {
            throw new InvalidOperationException(
                $"SUBTRU_ADMIN_API_KEY must contain at least {MinimumSecretLength} characters after the '{RequiredPrefix}' prefix.");
        }

        _configuredToken = Encoding.UTF8.GetBytes(configuredToken);
    }

    public Task<AuthResult?> ResolveAsync(string bearerToken, CancellationToken ct = default)
    {
        var candidate = Encoding.UTF8.GetBytes(bearerToken);
        var matches = candidate.Length == _configuredToken.Length &&
                      CryptographicOperations.FixedTimeEquals(candidate, _configuredToken);

        AuthResult? result = matches
            ? new AuthResult(
                UserId: Guid.Empty,
                ProjectId: null,
                IsApiKeyAuth: false,
                IsAdminAuth: true)
            : null;

        return Task.FromResult(result);
    }
}
