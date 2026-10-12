namespace SubTru.Infrastructure.Providers.Google;

public sealed record GooglePubSubPushTokenClaims(
    string Audience,
    string Email,
    bool EmailVerified,
    DateTimeOffset ExpiresAt
);
