using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;

namespace SubTru.Infrastructure.Providers.Google;

public sealed class GoogleServiceAccountValidator : IGoogleServiceAccountValidator
{
    private static readonly string[] RequiredScopes = ["https://www.googleapis.com/auth/androidpublisher"];

    public async Task<ValidatedGoogleServiceAccount> ValidateAsync(
        string serviceAccountJson,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serviceAccountJson))
        {
            throw new GoogleServiceAccountValidationException(
                "missing_service_account_json",
                "Field 'serviceAccountJson' is required.");
        }

        var document = GoogleServiceAccountJsonValidator.Validate(serviceAccountJson);

        try
        {
            var serviceAccountCredential = GoogleServiceAccountJsonValidator.CreateCredential(document);
            var credential = GoogleCredential
                .FromServiceAccountCredential(serviceAccountCredential)
                .CreateScoped(RequiredScopes);
            var tokenAccess = credential.UnderlyingCredential as ITokenAccess
                ?? throw new InvalidOperationException("Google credential does not support token access.");

            // This validates that the uploaded JSON can be loaded as a service account
            // credential and used by Google's auth library. It does not prove that the
            // service account has the required Google Play Console permissions.
            _ = await tokenAccess.GetAccessTokenForRequestAsync(cancellationToken: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (GoogleServiceAccountValidationException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new GoogleServiceAccountValidationException(
                "service_account_validation_unavailable",
                "Google could not validate 'serviceAccountJson' right now. Please retry.",
                ex,
                isTransient: true);
        }
        catch (OperationCanceledException ex)
        {
            throw new GoogleServiceAccountValidationException(
                "service_account_validation_unavailable",
                "Google could not validate 'serviceAccountJson' right now. Please retry.",
                ex,
                isTransient: true);
        }
        catch (TokenResponseException ex)
        {
            throw new GoogleServiceAccountValidationException(
                "invalid_service_account_credentials",
                "Field 'serviceAccountJson' contains a Google service account credential that could not be used for authentication.",
                ex);
        }
        catch (Exception ex)
        {
            throw new GoogleServiceAccountValidationException(
                "invalid_service_account_json",
                "Field 'serviceAccountJson' is not a valid Google service account credential.",
                ex);
        }

        var clientEmail = document.ClientEmail!;

        return new ValidatedGoogleServiceAccount(
            ClientEmail: clientEmail,
            PrivateKeyId: document.PrivateKeyId,
            ValidatedAt: DateTimeOffset.UtcNow
        );
    }

}
