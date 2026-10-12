using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Apis.Auth.OAuth2;

namespace SubTru.Infrastructure.Providers.Google;

internal static class GoogleServiceAccountJsonValidator
{
    internal const string GoogleOAuthTokenUri = "https://oauth2.googleapis.com/token";

    public static GoogleServiceAccountDocument Validate(string serviceAccountJson)
    {
        UnvalidatedGoogleServiceAccountDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<UnvalidatedGoogleServiceAccountDocument>(serviceAccountJson);
        }
        catch (JsonException ex)
        {
            throw new GoogleServiceAccountValidationException(
                "invalid_service_account_json",
                "Field 'serviceAccountJson' must be valid JSON.",
                ex);
        }

        if (document is null)
        {
            throw new GoogleServiceAccountValidationException(
                "invalid_service_account_json",
                "Field 'serviceAccountJson' must be valid JSON.");
        }

        if (!string.Equals(document.Type, "service_account", StringComparison.Ordinal))
        {
            throw new GoogleServiceAccountValidationException(
                "invalid_service_account_type",
                "Field 'serviceAccountJson' must contain a Google service account credential.");
        }

        var clientEmail = document.ClientEmail;
        if (string.IsNullOrWhiteSpace(clientEmail))
        {
            throw new GoogleServiceAccountValidationException(
                "missing_service_account_client_email",
                "Field 'serviceAccountJson' must contain 'client_email'.");
        }

        var privateKey = document.PrivateKey;
        if (string.IsNullOrWhiteSpace(privateKey))
        {
            throw new GoogleServiceAccountValidationException(
                "missing_service_account_private_key",
                "Field 'serviceAccountJson' must contain 'private_key'.");
        }

        var projectId = document.ProjectId;
        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new GoogleServiceAccountValidationException(
                "missing_service_account_project_id",
                "Field 'serviceAccountJson' must contain 'project_id'.");
        }

        var tokenUri = document.TokenUri;
        if (!string.Equals(tokenUri, GoogleOAuthTokenUri, StringComparison.Ordinal))
        {
            throw new GoogleServiceAccountValidationException(
                "invalid_service_account_token_uri",
                $"Field 'serviceAccountJson.token_uri' must be '{GoogleOAuthTokenUri}'.");
        }

        return new GoogleServiceAccountDocument(
            ProjectId: projectId,
            PrivateKeyId: document.PrivateKeyId,
            PrivateKey: privateKey,
            ClientEmail: clientEmail,
            TokenUri: GoogleOAuthTokenUri);
    }

    public static ServiceAccountCredential CreateCredential(GoogleServiceAccountDocument document)
    {
        var initializer = new ServiceAccountCredential.Initializer(
            document.ClientEmail,
            GoogleOAuthTokenUri)
        {
            ProjectId = document.ProjectId,
            KeyId = document.PrivateKeyId
        };

        return new ServiceAccountCredential(initializer.FromPrivateKey(document.PrivateKey));
    }
}

internal sealed record GoogleServiceAccountDocument(
    string ProjectId,
    string? PrivateKeyId,
    string PrivateKey,
    string ClientEmail,
    string TokenUri
);

internal sealed record UnvalidatedGoogleServiceAccountDocument(
    [property: JsonPropertyName("type")]
    string? Type,
    [property: JsonPropertyName("project_id")]
    string? ProjectId,
    [property: JsonPropertyName("private_key_id")]
    string? PrivateKeyId,
    [property: JsonPropertyName("private_key")]
    string? PrivateKey,
    [property: JsonPropertyName("client_email")]
    string? ClientEmail,
    [property: JsonPropertyName("token_uri")]
    string? TokenUri
);
