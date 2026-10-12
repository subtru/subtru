namespace SubTru.Infrastructure.Persistence.Postgres.WebhookKeys;

public interface IWebhookKeyManagementRepository
{
    Task<IReadOnlyList<WebhookKeyListItem>> ListByProjectAsync(
        Guid projectId,
        CancellationToken ct = default);

    Task<CreatedWebhookKey> CreateAsync(
        Guid projectId,
        string provider,
        string environment,
        string? signingSecret,
        CancellationToken ct = default);

    Task<DeactivatedWebhookKey?> DeactivateAsync(
        Guid projectId,
        Guid webhookKeyId,
        CancellationToken ct = default);

    Task<bool> UpdateSigningSecretAsync(
        Guid projectId,
        Guid webhookKeyId,
        string signingSecret,
        CancellationToken ct = default);
}
