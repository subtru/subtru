namespace SubTru.Infrastructure.Persistence.Postgres.ApiKeys;

public interface IApiKeyManagementRepository
{
    Task<IReadOnlyList<ApiKeyListItem>> ListByProjectAsync(
        Guid projectId,
        CancellationToken ct = default);

    Task<CreatedApiKey> CreateAsync(
        Guid projectId,
        string name,
        string environment,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default);

    Task<RevokedApiKey?> RevokeAsync(
        Guid projectId,
        Guid apiKeyId,
        CancellationToken ct = default);
}
