using SubTru.Domain.Subscriptions;

namespace SubTru.Domain.Access;

public sealed record Subscription(
    SubscriptionProvider Provider,
    string ProductId,
    SubscriptionStatus Status,
    string? ProviderStatus,
    DateTimeOffset CurrentPeriodEnd,
    DateTimeOffset? AccessEndsAt,
    bool CancelAtPeriodEnd
);
