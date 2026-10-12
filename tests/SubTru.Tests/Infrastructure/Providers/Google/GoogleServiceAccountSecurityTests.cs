using System.Security.Cryptography;
using System.Text.Json;
using SubTru.Infrastructure.Providers.Google;

namespace SubTru.Tests.Infrastructure.Providers.Google;

public sealed class GoogleServiceAccountSecurityTests
{
    [Theory]
    [InlineData("http://127.0.0.1/token")]
    [InlineData("https://oauth2.googleapis.com.example.com/token")]
    [InlineData("https://oauth2.googleapis.com/token?redirect=http://127.0.0.1")]
    [InlineData("")]
    public async Task ValidateAsync_NonGoogleTokenUri_IsRejectedBeforeTokenRequest(string tokenUri)
    {
        var validator = new GoogleServiceAccountValidator();

        var exception = await Assert.ThrowsAsync<GoogleServiceAccountValidationException>(
            () => validator.ValidateAsync(CreateServiceAccountJson(tokenUri)));

        Assert.Equal("invalid_service_account_token_uri", exception.ErrorCode);
        Assert.False(exception.IsTransient);
    }

    [Fact]
    public void Validate_StandardGoogleTokenUri_IsAccepted()
    {
        var document = GoogleServiceAccountJsonValidator.Validate(
            CreateServiceAccountJson(GoogleServiceAccountJsonValidator.GoogleOAuthTokenUri));
        var credential = GoogleServiceAccountJsonValidator.CreateCredential(document);

        Assert.Equal(GoogleServiceAccountJsonValidator.GoogleOAuthTokenUri, document.TokenUri);
        Assert.Equal(GoogleServiceAccountJsonValidator.GoogleOAuthTokenUri, credential.TokenServerUrl);
        Assert.Equal("service-account@example-project.iam.gserviceaccount.com", document.ClientEmail);
    }

    [Fact]
    public async Task GetSubscriptionAsync_NonGoogleTokenUri_IsRejectedBeforeOutboundRequest()
    {
        var handler = new CountingHttpMessageHandler();
        var client = new GooglePlaySubscriptionClient(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<GooglePlayApiException>(() =>
            client.GetSubscriptionAsync(
                "com.example.app",
                "purchase-token",
                CreateServiceAccountJson("http://127.0.0.1/token")));

        Assert.Equal("google_access_token_credential_error", exception.ErrorCode);
        var validationException = Assert.IsType<GoogleServiceAccountValidationException>(exception.InnerException);
        Assert.Equal("invalid_service_account_token_uri", validationException.ErrorCode);
        Assert.Equal(0, handler.RequestCount);
    }

    private static string CreateServiceAccountJson(string tokenUri)
    {
        using var rsa = RSA.Create(2048);

        return JsonSerializer.Serialize(new
        {
            type = "service_account",
            project_id = "example-project",
            private_key_id = "example-key-id",
            private_key = rsa.ExportPkcs8PrivateKeyPem(),
            client_email = "service-account@example-project.iam.gserviceaccount.com",
            token_uri = tokenUri
        });
    }

    private sealed class CountingHttpMessageHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
