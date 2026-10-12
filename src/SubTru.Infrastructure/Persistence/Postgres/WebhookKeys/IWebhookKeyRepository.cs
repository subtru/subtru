namespace SubTru.Infrastructure.Persistence.Postgres.WebhookKeys;

/// <summary>
/// Repository for looking up webhook keys to resolve project context and signing secrets.
/// </summary>
public interface IWebhookKeyRepository
{
    /// <summary>
    /// Looks up a webhook key and returns the associated project info and signing secret.
    /// Returns null if the key does not exist or is inactive.
    /// </summary>
    /// <param name="webhookKey">The unique webhook key from the URL path.</param>
    /// <param name="provider">The provider name (e.g., "stripe", "apple", "google").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Webhook key info if found and active; otherwise null.</returns>
    Task<WebhookKeyInfo?> GetByKeyAsync(
        string webhookKey,
        string provider,
        CancellationToken ct = default);
}
