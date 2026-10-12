namespace SubTru.Api.Features.Webhooks.Events;

public sealed record WebhookEventDto(
    string Id,
    string Provider,
    string Environment,
    string? Type,
    string? Change,
    string? ExternalUserId,
    string? ProviderSubscriptionId,
    string Status,
    string? ErrorCode,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? ProcessedAt
);
