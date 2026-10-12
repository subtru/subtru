namespace SubTru.Api.Features.Subscriptions;

public sealed record SubscriptionDto(
    string Provider,
    string ProviderSubscriptionId,
    string Environment,
    string ProductId,
    string? PriceId,
    string Status,
    string? ProviderStatus,
    DateTimeOffset? CurrentPeriodEnd,
    DateTimeOffset? AccessEndsAt
);
