namespace SubTru.Infrastructure.Persistence.Postgres.Subscriptions;

public sealed record SubscriptionReadModel(
    Guid ProjectId,
    string Environment,
    string ExternalUserId,
    string Provider,
    string ProviderSubscriptionId,
    string ProductId,
    string? PriceId,
    string Status,
    string? ProviderStatus,
    DateTimeOffset CurrentPeriodEnd,
    DateTimeOffset? AccessEndsAt,
    bool CancelAtPeriodEnd
);
