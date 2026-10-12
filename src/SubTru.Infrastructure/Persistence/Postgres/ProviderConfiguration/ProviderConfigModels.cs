namespace SubTru.Infrastructure.Persistence.Postgres.ProviderConfiguration;

public sealed record AppleProviderConfigReadModel(
    string BundleId,
    long AppAppleId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public sealed record AppleProviderConfigWriteModel(
    string BundleId,
    long AppAppleId
);

public sealed record GoogleProviderConfigReadModel(
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

public sealed record GoogleProviderConfigWriteModel(
    string PackageName,
    string PubSubAuthServiceAccountEmail,
    string? ServiceAccountJson
);

public sealed record GoogleProviderCredentialReadModel(
    string ServiceAccountJson,
    string ClientEmail,
    string? PrivateKeyId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastValidatedAt
);
