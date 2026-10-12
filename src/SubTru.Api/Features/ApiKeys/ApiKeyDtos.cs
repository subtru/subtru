namespace SubTru.Api.Features.ApiKeys;

public sealed record ApiKeyDto(
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

public sealed record ApiKeyListResponse(
    IReadOnlyList<ApiKeyDto> Items,
    string? NextCursor
);

public sealed record CreateApiKeyRequest(
    string Name,
    string? Environment,
    DateTimeOffset? ExpiresAt
);

public sealed record CreatedApiKeyResponse(
    Guid Id,
    string Name,
    string KeyId,
    string ApiKey,
    string Environment,
    DateTimeOffset CreatedAt
);

public sealed record DeleteApiKeyResponse(
    Guid Id,
    bool IsRevoked,
    DateTimeOffset? RevokedAt,
    bool Success
);
