namespace SubTru.Infrastructure.Persistence.Postgres.WebhookEvents;

public sealed record WebhookEventLogWriteModel(
    Guid ProjectId,
    string Provider,
    string Environment,
    string EventId,
    string? EventType,
    string? ExternalUserId,
    string Status,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset? ProviderCreatedAt,
    DateTimeOffset ReceivedAt,
    DateTimeOffset LastAttemptedAt,
    DateTimeOffset? ProcessedAt,
    string? ProviderSubscriptionId,
    string? EventChange = null
);

public sealed record WebhookEventCursorReadModel(
    DateTimeOffset ReceivedAt,
    string EventId
);

public sealed record WebhookEventListItemReadModel(
    string EventId,
    string Provider,
    string Environment,
    string? EventType,
    string? ExternalUserId,
    string Status,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? ProcessedAt,
    string? ErrorCode,
    string? ProviderSubscriptionId,
    string? EventChange = null
);

public sealed record WebhookEventListPageReadModel(
    IReadOnlyList<WebhookEventListItemReadModel> Items,
    WebhookEventCursorReadModel? NextCursor
);
