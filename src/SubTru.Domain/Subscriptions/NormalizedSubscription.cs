namespace SubTru.Domain.Subscriptions;

public sealed record NormalizedSubscription(
    Guid ProjectId,
    string Environment,
    string ExternalUserId,
    string Provider,
    string ProviderSubscriptionId,
    string? ProductId,
    string? PriceId,
    string Status,
    string? ProviderStatus,
    DateTimeOffset CurrentPeriodEnd,
    DateTimeOffset? AccessEndsAt,
    bool CancelAtPeriodEnd,
    DateTimeOffset? ProviderEventCreatedAt = null
);
