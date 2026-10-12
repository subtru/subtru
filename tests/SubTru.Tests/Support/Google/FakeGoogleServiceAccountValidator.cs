using SubTru.Infrastructure.Providers.Google;

namespace SubTru.Tests.Support.Google;

internal sealed class FakeGoogleServiceAccountValidator : IGoogleServiceAccountValidator
{
    public const string ValidJson =
        "{\"type\":\"service_account\",\"project_id\":\"play-project\",\"private_key_id\":\"key-123\",\"private_key\":\"-----BEGIN PRIVATE KEY-----\\\\nabc\\\\n-----END PRIVATE KEY-----\\\\n\",\"client_email\":\"svc-acme@play-project.iam.gserviceaccount.com\",\"token_uri\":\"https://oauth2.googleapis.com/token\"}";

    public Task<ValidatedGoogleServiceAccount> ValidateAsync(
        string serviceAccountJson,
        CancellationToken ct = default)
    {
        if (serviceAccountJson == "invalid-google-json")
        {
            throw new GoogleServiceAccountValidationException(
                "invalid_service_account_json",
                "Field 'serviceAccountJson' must be valid JSON.");
        }

        if (serviceAccountJson == "transient-google-error")
        {
            throw new GoogleServiceAccountValidationException(
                "service_account_validation_unavailable",
                "Google could not validate 'serviceAccountJson' right now. Please retry.",
                isTransient: true);
        }

        return Task.FromResult(new ValidatedGoogleServiceAccount(
            ClientEmail: "svc-acme@play-project.iam.gserviceaccount.com",
            PrivateKeyId: "key-123",
            ValidatedAt: DateTimeOffset.UtcNow));
    }
}
