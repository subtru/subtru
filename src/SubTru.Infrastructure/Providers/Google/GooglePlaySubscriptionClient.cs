using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;

namespace SubTru.Infrastructure.Providers.Google;

public sealed class GooglePlaySubscriptionClient : IGooglePlaySubscriptionClient
{
    private const string AndroidPublisherScope = "https://www.googleapis.com/auth/androidpublisher";
    private const int MaxCachedCredentials = 500;
    private static readonly TimeSpan CredentialIdleExpiration = TimeSpan.FromHours(1);
    private static readonly TimeSpan CredentialCleanupInterval = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly GoogleServiceAccountCredentialCache CredentialCache = new(
        TimeProvider.System,
        MaxCachedCredentials,
        CredentialIdleExpiration,
        CredentialCleanupInterval);

    private readonly HttpClient _httpClient;

    public GooglePlaySubscriptionClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<GooglePlaySubscriptionPurchase> GetSubscriptionAsync(
        string packageName,
        string purchaseToken,
        string serviceAccountJson,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(purchaseToken);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://androidpublisher.googleapis.com/androidpublisher/v3/applications/{Uri.EscapeDataString(packageName)}/purchases/subscriptionsv2/tokens/{Uri.EscapeDataString(purchaseToken)}");

        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await GetAccessTokenAsync(serviceAccountJson, ct));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new GooglePlayApiException(
                "google_subscription_fetch_network_error",
                "Google Play subscription fetch failed because the HTTP request to Google Play failed.",
                innerException: ex,
                isTransient: true);
        }
        catch (TaskCanceledException ex)
        {
            throw new GooglePlayApiException(
                "google_subscription_fetch_timeout",
                "Google Play subscription fetch failed because the request to Google Play timed out.",
                innerException: ex,
                isTransient: true);
        }

        using (response)
        {
            var payload = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                throw CreateApiException(
                    "google_subscription_fetch_failed",
                    "Google Play subscription fetch failed.",
                    response.StatusCode,
                    payload);
            }

            var subscription = JsonSerializer.Deserialize<GooglePlaySubscriptionPurchase>(payload, JsonOptions);
            if (subscription is null)
            {
                throw new GooglePlayApiException(
                    "invalid_google_subscription_response",
                    "Google Play subscription response could not be parsed.");
            }

            return subscription with { RawResponseJson = payload };
        }
    }

    public async Task AcknowledgeAsync(
        string packageName,
        string purchaseToken,
        string serviceAccountJson,
        string? subscriptionId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(purchaseToken);

        var encodedSubscriptionId = string.IsNullOrWhiteSpace(subscriptionId)
            ? string.Empty
            : Uri.EscapeDataString(subscriptionId);

        var uri = new StringBuilder(
            $"https://androidpublisher.googleapis.com/androidpublisher/v3/applications/{Uri.EscapeDataString(packageName)}/purchases/subscriptions/{encodedSubscriptionId}/tokens/{Uri.EscapeDataString(purchaseToken)}:acknowledge");

        using var request = new HttpRequestMessage(HttpMethod.Post, uri.ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await GetAccessTokenAsync(serviceAccountJson, ct));
        request.Content = JsonContent.Create(new { });

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new GooglePlayApiException(
                "google_subscription_acknowledge_network_error",
                "Google Play subscription acknowledgement failed because the HTTP request to Google Play failed.",
                innerException: ex,
                isTransient: true);
        }
        catch (TaskCanceledException ex)
        {
            throw new GooglePlayApiException(
                "google_subscription_acknowledge_timeout",
                "Google Play subscription acknowledgement failed because the request to Google Play timed out.",
                innerException: ex,
                isTransient: true);
        }

        using (response)
        {
            var payload = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                throw CreateApiException(
                    "google_subscription_acknowledge_failed",
                    "Google Play subscription acknowledgement failed.",
                    response.StatusCode,
                    payload);
            }
        }
    }

    private static async Task<string> GetAccessTokenAsync(
        string serviceAccountJson,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serviceAccountJson))
        {
            throw new GooglePlayApiException(
                "missing_google_service_account_credentials",
                "Google service account credentials are missing.");
        }

        var bytes = Encoding.UTF8.GetBytes(serviceAccountJson);
        try
        {
            var cacheKey = Convert.ToHexString(SHA256.HashData(bytes));
            var tokenAccess = CredentialCache.GetOrAdd(
                cacheKey,
                () => CreateScopedTokenAccess(serviceAccountJson));

            var token = await tokenAccess.GetAccessTokenForRequestAsync(cancellationToken: ct);
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new GooglePlayApiException(
                    "google_access_token_empty",
                    "Google access token acquisition returned an empty token.",
                    isTransient: true);
            }

            return token;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new GooglePlayApiException(
                "google_access_token_network_error",
                "Google access token could not be acquired because the HTTP request to Google failed.",
                innerException: ex,
                isTransient: true);
        }
        catch (TaskCanceledException ex)
        {
            throw new GooglePlayApiException(
                "google_access_token_timeout",
                "Google access token could not be acquired because the request to Google timed out.",
                innerException: ex,
                isTransient: true);
        }
        catch (TokenResponseException ex)
        {
            throw new GooglePlayApiException(
                "google_access_token_token_response_error",
                "Google access token could not be acquired because Google returned an OAuth token error response.",
                innerException: ex,
                isTransient: true);
        }
        catch (GooglePlayApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new GooglePlayApiException(
                "google_access_token_credential_error",
                "Google access token could not be acquired because the configured service account credential could not be used.",
                innerException: ex,
                isTransient: true);
        }
        finally
        {
            Array.Clear(bytes, 0, bytes.Length);
        }
    }

    private static ITokenAccess CreateScopedTokenAccess(string serviceAccountJson)
    {
        var document = GoogleServiceAccountJsonValidator.Validate(serviceAccountJson);
        var serviceAccountCredential = GoogleServiceAccountJsonValidator.CreateCredential(document);
        var credential = GoogleCredential
            .FromServiceAccountCredential(serviceAccountCredential)
            .CreateScoped(AndroidPublisherScope);

        return credential.UnderlyingCredential as ITokenAccess
            ?? throw new InvalidOperationException("Google credential does not support token access.");
    }

    private static GooglePlayApiException CreateApiException(
        string errorCode,
        string message,
        HttpStatusCode statusCode,
        string responseBody)
    {
        var isTransient = statusCode is HttpStatusCode.Conflict or (HttpStatusCode)429
            || (int)statusCode >= 500;

        return new GooglePlayApiException(
            errorCode,
            responseBody.Length == 0 ? message : $"{message} HTTP {(int)statusCode}.",
            statusCode: statusCode,
            isTransient: isTransient);
    }
}
