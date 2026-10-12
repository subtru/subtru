namespace SubTru.Api.Features.ProviderConfiguration;

public sealed record AppleProviderConfigDto(
    string Provider,
    string BundleId,
    long AppAppleId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public sealed record UpsertAppleProviderConfigRequest(
    string BundleId,
    long AppAppleId
);

public sealed record GoogleProviderConfigDto(
    string Provider,
    string PackageName,
    string PubSubAuthServiceAccountEmail,
    bool HasServiceAccountCredentials,
    string? ServiceAccountClientEmail,
    string? ServiceAccountKeyId,
    DateTimeOffset? ServiceAccountCreatedAt,
    DateTimeOffset? ServiceAccountLastValidatedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public sealed record UpsertGoogleProviderConfigRequest(
    string PackageName,
    string PubSubAuthServiceAccountEmail,
    string? ServiceAccountJson
);
