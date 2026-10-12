namespace SubTru.Infrastructure.Persistence.Postgres.ApiKeys;

public sealed record ApiKeyListItem(
    Guid Id,
    string Name,
    string KeyId,
    string Environment,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? ExpiresAt,
    bool IsRevoked,
    DateTimeOffset? RevokedAt
);

public sealed record CreatedApiKey(
    Guid Id,
    string Name,
    string KeyId,
    string ApiKey,
    string Environment,
    DateTimeOffset CreatedAt
);

public sealed record RevokedApiKey(
    Guid Id,
    bool IsRevoked,
    DateTimeOffset? RevokedAt
);
