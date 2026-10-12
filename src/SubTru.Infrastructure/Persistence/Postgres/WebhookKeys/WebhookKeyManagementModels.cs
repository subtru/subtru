namespace SubTru.Infrastructure.Persistence.Postgres.WebhookKeys;

public sealed record WebhookKeyListItem(
    Guid Id,
    string KeyId,
    string Provider,
    string Environment,
    bool HasSigningSecret,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public sealed record CreatedWebhookKey(
    Guid Id,
    string KeyId,
    string WebhookKey,
    string Provider,
    string Environment,
    DateTimeOffset CreatedAt
);

public sealed record DeactivatedWebhookKey(
    Guid Id,
    bool IsActive,
    DateTimeOffset UpdatedAt
);
