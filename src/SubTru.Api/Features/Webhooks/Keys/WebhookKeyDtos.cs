namespace SubTru.Api.Features.Webhooks.Keys;

public sealed record WebhookKeyDto(
    Guid Id,
    string KeyId,
    string Provider,
    string Environment,
    bool HasSigningSecret,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public sealed record WebhookKeyListResponse(
    IReadOnlyList<WebhookKeyDto> Items
);

public sealed record CreateWebhookKeyRequest(
    string Provider,
    string? Environment,
    string? SigningSecret
);

public sealed record CreatedWebhookKeyResponse(
    Guid Id,
    string KeyId,
    string WebhookKey,
    string Provider,
    string Environment,
    string WebhookUrl,
    DateTimeOffset CreatedAt
);

public sealed record DeleteWebhookKeyResponse(
    Guid Id,
    bool IsActive,
    bool Success
);

public sealed record UpdateSigningSecretRequest(
    string SigningSecret
);

public sealed record UpdateSigningSecretResponse(
    bool Success
);
