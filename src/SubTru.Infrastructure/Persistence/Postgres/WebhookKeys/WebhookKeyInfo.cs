namespace SubTru.Infrastructure.Persistence.Postgres.WebhookKeys;

/// <summary>
/// Information associated with a webhook key, used to route and verify incoming webhooks.
/// </summary>
/// <param name="ProjectId">The SubTru project this webhook key belongs to.</param>
/// <param name="Environment">The subscription environment this webhook key accepts.</param>
/// <param name="SigningSecret">The provider-specific webhook signing secret for signature verification. Null for providers that don't use signing secrets (Apple, Google).</param>
public sealed record WebhookKeyInfo(
    Guid ProjectId,
    string Environment,
    string? SigningSecret);
