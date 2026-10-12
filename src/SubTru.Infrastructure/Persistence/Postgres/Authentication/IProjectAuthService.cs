namespace SubTru.Infrastructure.Persistence.Postgres.Authentication;

/// <summary>
/// Validates API keys and resolves them to the project they grant access to.
/// </summary>
public interface IProjectAuthService
{
    /// <summary>
    /// Validates an API key and returns the project ID it grants access to, or null if invalid.
    /// </summary>
    Task<ValidatedApiKey?> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default);
}

public sealed record ValidatedApiKey(
    Guid ProjectId,
    string Environment
);
