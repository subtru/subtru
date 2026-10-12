namespace SubTru.Infrastructure.Providers.Google;

public sealed record ValidatedGoogleServiceAccount(
    string ClientEmail,
    string? PrivateKeyId,
    DateTimeOffset ValidatedAt
);
