namespace SubTru.Api.Features.Subscriptions;

public sealed record GetSubscriptionsResponse(
    IReadOnlyList<SubscriptionDto> Items,
    string? NextCursor
);
