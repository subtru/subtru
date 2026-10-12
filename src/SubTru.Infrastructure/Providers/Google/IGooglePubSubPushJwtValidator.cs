namespace SubTru.Infrastructure.Providers.Google;

public interface IGooglePubSubPushJwtValidator
{
    Task<GooglePubSubPushTokenClaims> ValidateAsync(
        string bearerToken,
        string expectedAudience,
        string expectedEmail,
        CancellationToken ct = default);
}
