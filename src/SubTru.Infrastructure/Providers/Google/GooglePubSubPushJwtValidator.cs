using Google.Apis.Auth;
using Google.Apis.Util;

namespace SubTru.Infrastructure.Providers.Google;

public sealed class GooglePubSubPushJwtValidator : IGooglePubSubPushJwtValidator
{
    public async Task<GooglePubSubPushTokenClaims> ValidateAsync(
        string bearerToken,
        string expectedAudience,
        string expectedEmail,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            throw new GooglePubSubPushJwtValidationException(
                "missing_google_pubsub_bearer_token",
                "Google Pub/Sub push request is missing the bearer token.");
        }

        if (string.IsNullOrWhiteSpace(expectedAudience))
        {
            throw new ArgumentException("Expected audience cannot be null or empty.", nameof(expectedAudience));
        }

        if (string.IsNullOrWhiteSpace(expectedEmail))
        {
            throw new ArgumentException("Expected email cannot be null or empty.", nameof(expectedEmail));
        }

        ct.ThrowIfCancellationRequested();

        GoogleJsonWebSignature.Payload payload;
        try
        {
            payload = await GoogleJsonWebSignature.ValidateAsync(
                bearerToken,
                new GoogleJsonWebSignature.ValidationSettings
                {
                    Audience = [expectedAudience],
                    Clock = SystemClock.Default,
                    IssuedAtClockTolerance = TimeSpan.FromSeconds(30),
                    ExpirationTimeClockTolerance = TimeSpan.Zero
                });
        }
        catch (HttpRequestException ex)
        {
            throw new GooglePubSubPushJwtValidationException(
                "google_pubsub_jwt_validation_network_error",
                "Google Pub/Sub push JWT could not be validated because the Google token verification request failed.",
                ex,
                isTransient: true);
        }
        catch (TaskCanceledException ex)
        {
            throw new GooglePubSubPushJwtValidationException(
                "google_pubsub_jwt_validation_timeout",
                "Google Pub/Sub push JWT could not be validated because Google token verification timed out.",
                ex,
                isTransient: true);
        }
        catch (InvalidJwtException ex)
        {
            throw new GooglePubSubPushJwtValidationException(
                "invalid_google_pubsub_bearer_token",
                "Google Pub/Sub push bearer token is invalid.",
                ex);
        }

        if (payload.Issuer is not ("accounts.google.com" or "https://accounts.google.com"))
        {
            throw new GooglePubSubPushJwtValidationException(
                "invalid_google_pubsub_token_issuer",
                "Google Pub/Sub push bearer token issuer is invalid.");
        }

        var email = payload.Email?.Trim();
        if (!string.Equals(email, expectedEmail, StringComparison.OrdinalIgnoreCase))
        {
            throw new GooglePubSubPushJwtValidationException(
                "invalid_google_pubsub_token_email",
                "Google Pub/Sub push bearer token email is invalid.");
        }

        if (payload.EmailVerified != true)
        {
            throw new GooglePubSubPushJwtValidationException(
                "invalid_google_pubsub_token_email_verified",
                "Google Pub/Sub push bearer token email must be verified.");
        }

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(payload.ExpirationTimeSeconds ?? 0);

        return new GooglePubSubPushTokenClaims(
            Audience: expectedAudience,
            Email: email!,
            EmailVerified: true,
            ExpiresAt: expiresAt
        );
    }
}
