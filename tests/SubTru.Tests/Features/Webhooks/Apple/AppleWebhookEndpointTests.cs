using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubTru.Api.Features.Webhooks.Apple;
using SubTru.Domain.Subscriptions;
using SubTru.Infrastructure.Providers.Apple;
using SubTru.Infrastructure.Providers.Google;
using SubTru.Infrastructure.Persistence.Postgres.Subscriptions;
using SubTru.Infrastructure.Persistence.Postgres.ProviderConfiguration;
using SubTru.Infrastructure.Persistence.Postgres.WebhookEvents;
using SubTru.Infrastructure.Persistence.Postgres.WebhookKeys;

namespace SubTru.Tests.Features.Webhooks.Apple;

public sealed class AppleWebhookEndpointTests : IClassFixture<AppleWebhookWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly AppleWebhookWebApplicationFactory _factory;

    public AppleWebhookEndpointTests(AppleWebhookWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.WebhookEventLogRepository.Reset();
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PostAppleWebhook_InvalidWebhookKey_ReturnsOk()
    {
        var response = await _client.PostAsJsonAsync("/webhooks/apple/invalid-key", new { signedPayload = "payload" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostAppleWebhook_OversizedPayload_ReturnsPayloadTooLarge()
    {
        using var content = new StringContent(
            new string('x', WebhookRequestBodyReader.MaxBodySizeBytes + 1),
            Encoding.UTF8,
            "application/json");

        var response = await _client.PostAsync("/webhooks/apple/subtru_wh_test.secret", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task PostAppleWebhook_MissingSignedPayload_ReturnsOk()
    {
        var response = await _client.PostAsJsonAsync("/webhooks/apple/subtru_wh_test.secret", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostAppleWebhook_VerificationFailure_ReturnsOk()
    {
        var response = await _client.PostAsJsonAsync("/webhooks/apple/subtru_wh_test.secret", new { signedPayload = "bad" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostAppleWebhook_RetryableVerificationFailure_ReturnsServiceUnavailable()
    {
        var response = await _client.PostAsJsonAsync("/webhooks/apple/subtru_wh_test.secret", new { signedPayload = "retryable-chain" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task PostAppleWebhook_BundleIdMismatch_ReturnsServiceUnavailable()
    {
        var response = await _client.PostAsJsonAsync("/webhooks/apple/subtru_wh_test.secret", new { signedPayload = "bundle-mismatch" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task PostAppleWebhook_AppAppleIdMismatch_ReturnsServiceUnavailable()
    {
        var response = await _client.PostAsJsonAsync("/webhooks/apple/subtru_wh_test.secret", new { signedPayload = "app-apple-id-mismatch" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task PostAppleWebhook_MissingProviderConfig_ReturnsServiceUnavailable()
    {
        var response = await _client.PostAsJsonAsync("/webhooks/apple/subtru_wh_missing-config.secret", new { signedPayload = "valid" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task PostAppleWebhook_ProviderConfigLookupFailure_ReturnsInternalServerError()
    {
        var response = await _client.PostAsJsonAsync("/webhooks/apple/subtru_wh_config-error.secret", new { signedPayload = "valid" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task PostAppleWebhook_DuplicateNotification_ReturnsOk()
    {
        var response = await _client.PostAsJsonAsync("/webhooks/apple/subtru_wh_duplicate.secret", new { signedPayload = "duplicate" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostAppleWebhook_MappingFailure_ReturnsOk()
    {
        var response = await _client.PostAsJsonAsync("/webhooks/apple/subtru_wh_missing-user.secret", new { signedPayload = "missing-user" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostAppleWebhook_MissingCurrentPeriodEnd_ReturnsOk()
    {
        var response = await _client.PostAsJsonAsync("/webhooks/apple/subtru_wh_test.secret", new { signedPayload = "missing-period-end" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostAppleWebhook_SuccessfulNotification_ReturnsOk()
    {
        var response = await _client.PostAsJsonAsync("/webhooks/apple/subtru_wh_test.secret", new { signedPayload = "valid" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostAppleWebhook_IgnoredNotificationType_ReturnsOk()
    {
        var response = await _client.PostAsJsonAsync("/webhooks/apple/subtru_wh_test.secret", new { signedPayload = "test-notification" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("renewal-extension-summary", "123e4567-e89b-12d3-a456-426614174445")]
    [InlineData("external-purchase-token", "123e4567-e89b-12d3-a456-426614174446")]
    public async Task PostAppleWebhook_IgnoredNotificationWithoutData_LogsIgnored(
        string signedPayload,
        string notificationUuid)
    {
        var response = await _client.PostAsJsonAsync(
            "/webhooks/apple/subtru_wh_test.secret",
            new { signedPayload });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var eventLog = Assert.Single(_factory.WebhookEventLogRepository.Models.Where(x =>
            x.EventId == notificationUuid));
        Assert.Equal(WebhookEventStatus.Ignored, eventLog.Status);
        Assert.Null(eventLog.ErrorCode);
        Assert.NotNull(eventLog.ProcessedAt);
    }

    [Fact]
    public async Task PostAppleWebhook_NonAutoRenewableRefund_LogsIgnored()
    {
        var response = await _client.PostAsJsonAsync(
            "/webhooks/apple/subtru_wh_test.secret",
            new { signedPayload = "consumable-refund" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var eventLog = Assert.Single(_factory.WebhookEventLogRepository.Models.Where(x =>
            x.EventId == "123e4567-e89b-12d3-a456-426614174447"));
        Assert.Equal(WebhookEventStatus.Ignored, eventLog.Status);
        Assert.Null(eventLog.ErrorCode);
        Assert.NotNull(eventLog.ProcessedAt);
    }

    [Fact]
    public async Task PostAppleWebhook_UnsupportedNotificationType_ReturnsOk()
    {
        var response = await _client.PostAsJsonAsync("/webhooks/apple/subtru_wh_test.secret", new { signedPayload = "unsupported" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostAppleWebhook_SupportedNotificationMissingTransaction_ReturnsOk()
    {
        var response = await _client.PostAsJsonAsync("/webhooks/apple/subtru_wh_test.secret", new { signedPayload = "missing-transaction" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostAppleWebhook_MissingEnvironmentEvidence_ReturnsOkWithoutPersisting()
    {
        var response = await _client.PostAsJsonAsync("/webhooks/apple/subtru_wh_test.secret", new { signedPayload = "missing-environment" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

public sealed class AppleWebhookWebApplicationFactory : WebApplicationFactory<Program>
{
    internal FakeAppleWebhookEventLogRepository WebhookEventLogRepository { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        TestWebHostConfiguration.UseAdminApiKey(builder);
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            var config = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=localhost;Port=5432;Database=subtru_test;Username=test;Password=test",
                ["API_KEY_PEPPER"] = "test-pepper",
                ["SUBTRU_ENCRYPTION_KEY"] = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                ["WebhookUrls:PublicBaseUrl"] = "https://api.test.subtru.com",
                ["AppleVerification:RootCertificateDirectory"] = TestAppleTrustStoreDirectory.DirectoryPath,
                ["SUBTRU_ADMIN_API_KEY"] = "subtru_admin_0123456789abcdef0123456789abcdef"
            };

            configBuilder.AddInMemoryCollection(config);
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IWebhookKeyRepository>();
            services.RemoveAll<IProviderConfigRepository>();
            services.RemoveAll<IAppleSignedPayloadVerifier>();
            services.RemoveAll<IProcessedEventRepository>();
            services.RemoveAll<IWebhookEventLogRepository>();
            services.RemoveAll<ISubscriptionWriteRepository>();

            services.AddScoped<IWebhookKeyRepository, FakeAppleWebhookKeyRepository>();
            services.AddScoped<IProviderConfigRepository, FakeAppleProviderConfigRepository>();
            services.AddScoped<IAppleSignedPayloadVerifier, FakeAppleSignedPayloadVerifier>();
            services.AddScoped<IProcessedEventRepository, FakeAppleProcessedEventRepository>();
            services.AddSingleton<IWebhookEventLogRepository>(WebhookEventLogRepository);
            services.AddScoped<ISubscriptionWriteRepository, FakeSubscriptionWriteRepository>();
        });
    }
}

internal sealed class FakeAppleWebhookKeyRepository : IWebhookKeyRepository
{
    public Task<WebhookKeyInfo?> GetByKeyAsync(string webhookKey, string provider, CancellationToken ct = default)
    {
        if (provider != "apple")
        {
            return Task.FromResult<WebhookKeyInfo?>(null);
        }

        return webhookKey switch
        {
            "subtru_wh_test.secret" => Task.FromResult<WebhookKeyInfo?>(new WebhookKeyInfo(Guid.Parse("11111111-1111-1111-1111-111111111111"), SubscriptionEnvironment.Production, null)),
            "subtru_wh_duplicate.secret" => Task.FromResult<WebhookKeyInfo?>(new WebhookKeyInfo(Guid.Parse("11111111-1111-1111-1111-111111111111"), SubscriptionEnvironment.Production, null)),
            "subtru_wh_missing-user.secret" => Task.FromResult<WebhookKeyInfo?>(new WebhookKeyInfo(Guid.Parse("11111111-1111-1111-1111-111111111111"), SubscriptionEnvironment.Production, null)),
            "subtru_wh_missing-config.secret" => Task.FromResult<WebhookKeyInfo?>(new WebhookKeyInfo(Guid.Parse("22222222-2222-2222-2222-222222222222"), SubscriptionEnvironment.Production, null)),
            "subtru_wh_config-error.secret" => Task.FromResult<WebhookKeyInfo?>(new WebhookKeyInfo(Guid.Parse("33333333-3333-3333-3333-333333333333"), SubscriptionEnvironment.Production, null)),
            _ => Task.FromResult<WebhookKeyInfo?>(null)
        };
    }
}

internal sealed class FakeAppleProviderConfigRepository : IProviderConfigRepository
{
    public Task<AppleProviderConfigReadModel?> GetAppleProviderConfigAsync(Guid projectId, CancellationToken ct = default)
    {
        if (projectId == Guid.Parse("22222222-2222-2222-2222-222222222222"))
        {
            return Task.FromResult<AppleProviderConfigReadModel?>(null);
        }

        if (projectId == Guid.Parse("33333333-3333-3333-3333-333333333333"))
        {
            throw new InvalidOperationException("Provider config lookup failed.");
        }

        return Task.FromResult<AppleProviderConfigReadModel?>(new AppleProviderConfigReadModel(
            BundleId: "com.example.app",
            AppAppleId: 1234567890,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow));
    }

    public Task<AppleProviderConfigReadModel> UpsertAppleProviderConfigAsync(Guid projectId, AppleProviderConfigWriteModel writeModel, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<GoogleProviderConfigReadModel?> GetGoogleProviderConfigAsync(Guid projectId, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<GoogleProviderCredentialReadModel?> GetGoogleProviderCredentialAsync(Guid projectId, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<GoogleProviderConfigReadModel> UpsertGoogleProviderConfigAsync(Guid projectId, GoogleProviderConfigWriteModel writeModel, ValidatedGoogleServiceAccount? validatedServiceAccount, CancellationToken ct = default)
        => throw new NotSupportedException();
}

internal sealed class FakeAppleSignedPayloadVerifier : IAppleSignedPayloadVerifier
{
    public Task<AppleTrustedNotification> VerifyAndDecode(string signedPayload, AppleProviderConfigReadModel config, CancellationToken ct = default)
    {
        if (signedPayload == "bad")
        {
            throw new AppleSignedPayloadVerificationException("invalid_apple_signature", "Apple signed payload signature validation failed.");
        }

        if (signedPayload == "retryable-chain")
        {
            throw new AppleSignedPayloadVerificationException(
                AppleSignedPayloadVerifier.RetryableCertificateChainErrorCode,
                "Apple signed payload certificate chain revocation status could not be determined.");
        }

        if (signedPayload == "bundle-mismatch")
        {
            throw new AppleSignedPayloadVerificationException(
                "bundle_id_mismatch",
                "Apple notification bundleId did not match the configured project bundleId.");
        }

        if (signedPayload == "app-apple-id-mismatch")
        {
            throw new AppleSignedPayloadVerificationException(
                "app_apple_id_mismatch",
                "Apple notification appAppleId did not match the configured project appAppleId.");
        }

        if (signedPayload == "duplicate")
        {
            return Task.FromResult(CreateNotification("123e4567-e89b-12d3-a456-426614174111"));
        }

        if (signedPayload == "missing-user")
        {
            return Task.FromResult(CreateNotification("123e4567-e89b-12d3-a456-426614174222", appAccountToken: null));
        }

        if (signedPayload == "missing-period-end")
        {
            var now = new DateTimeOffset(2026, 3, 6, 12, 0, 0, TimeSpan.Zero);

            return Task.FromResult(CreateNotification(
                "123e4567-e89b-12d3-a456-426614174223",
                transactionInfo: new AppleTrustedTransactionInfo(
                    TransactionId: "2000000123456789",
                    OriginalTransactionId: "1000000123456789",
                    ProductId: "com.example.premium.monthly",
                    AppAccountToken: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    BundleId: "com.example.app",
                    Environment: "production",
                    Type: "Auto-Renewable Subscription",
                    ExpiresDate: null,
                    RevocationDate: null,
                    SignedDate: now),
                renewalInfoOverride: new AppleTrustedRenewalInfo(
                    OriginalTransactionId: "1000000123456789",
                    ProductId: "com.example.premium.monthly",
                    AutoRenewProductId: "com.example.premium.monthly",
                    AppAccountToken: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    Environment: "production",
                    AutoRenewStatus: true,
                    GracePeriodExpiresDate: null,
                    RenewalDate: null,
                    IsInBillingRetryPeriod: false,
                    ExpirationIntent: null,
                    SignedDate: now)));
        }

        if (signedPayload == "test-notification")
        {
            return Task.FromResult(CreateNotification("123e4567-e89b-12d3-a456-426614174444", notificationType: "TEST"));
        }

        if (signedPayload == "renewal-extension-summary")
        {
            return Task.FromResult(new AppleTrustedNotification(
                NotificationType: "RENEWAL_EXTENSION",
                Subtype: "SUMMARY",
                NotificationUuid: "123e4567-e89b-12d3-a456-426614174445",
                SignedDate: new DateTimeOffset(2026, 3, 6, 12, 0, 0, TimeSpan.Zero),
                Data: null));
        }

        if (signedPayload == "external-purchase-token")
        {
            return Task.FromResult(new AppleTrustedNotification(
                NotificationType: "EXTERNAL_PURCHASE_TOKEN",
                Subtype: "CREATED",
                NotificationUuid: "123e4567-e89b-12d3-a456-426614174446",
                SignedDate: new DateTimeOffset(2026, 3, 6, 12, 0, 0, TimeSpan.Zero),
                Data: null));
        }

        if (signedPayload == "consumable-refund")
        {
            var now = new DateTimeOffset(2026, 3, 6, 12, 0, 0, TimeSpan.Zero);
            return Task.FromResult(new AppleTrustedNotification(
                NotificationType: "REFUND",
                Subtype: null,
                NotificationUuid: "123e4567-e89b-12d3-a456-426614174447",
                SignedDate: now,
                Data: new AppleTrustedNotificationData(
                    BundleId: "com.example.app",
                    AppAppleId: 1234567890,
                    Environment: "production",
                    Status: null,
                    TransactionInfo: new AppleTrustedTransactionInfo(
                        TransactionId: "2000000123456790",
                        OriginalTransactionId: "1000000123456790",
                        ProductId: "com.example.coins",
                        AppAccountToken: null,
                        BundleId: "com.example.app",
                        Environment: "production",
                        Type: "Consumable",
                        ExpiresDate: null,
                        RevocationDate: now,
                        SignedDate: now),
                    RenewalInfo: null)));
        }

        if (signedPayload == "unsupported")
        {
            return Task.FromResult(CreateNotification("123e4567-e89b-12d3-a456-426614174555", notificationType: "SOMETHING_NEW"));
        }

        if (signedPayload == "missing-transaction")
        {
            return Task.FromResult(CreateNotification("123e4567-e89b-12d3-a456-426614174666", transactionInfo: null));
        }

        if (signedPayload == "missing-environment")
        {
            return Task.FromResult(CreateNotification(
                "123e4567-e89b-12d3-a456-426614174777",
                dataEnvironment: null,
                transactionEnvironment: null,
                renewalEnvironment: null));
        }

        return Task.FromResult(CreateNotification("123e4567-e89b-12d3-a456-426614174000"));
    }

    private static AppleTrustedNotification CreateNotification(
        string notificationUuid,
        string? appAccountToken = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
        string notificationType = "DID_RENEW",
        AppleTrustedTransactionInfo? transactionInfo = null,
        AppleTrustedRenewalInfo? renewalInfoOverride = null,
        string? dataEnvironment = "production",
        string? transactionEnvironment = "production",
        string? renewalEnvironment = "production")
    {
        var now = new DateTimeOffset(2026, 3, 6, 12, 0, 0, TimeSpan.Zero);
        return new AppleTrustedNotification(
            NotificationType: notificationType,
            Subtype: null,
            NotificationUuid: notificationUuid,
            SignedDate: now,
            Data: new AppleTrustedNotificationData(
                BundleId: "com.example.app",
                AppAppleId: 1234567890,
                Environment: dataEnvironment!,
                Status: 1,
                TransactionInfo: transactionInfo ?? new AppleTrustedTransactionInfo(
                    TransactionId: "2000000123456789",
                    OriginalTransactionId: "1000000123456789",
                    ProductId: "com.example.premium.monthly",
                    AppAccountToken: appAccountToken,
                    BundleId: "com.example.app",
                    Environment: transactionEnvironment!,
                    Type: "Auto-Renewable Subscription",
                    ExpiresDate: now.AddDays(30),
                    RevocationDate: null,
                    SignedDate: now),
                RenewalInfo: renewalInfoOverride ?? new AppleTrustedRenewalInfo(
                    OriginalTransactionId: "1000000123456789",
                    ProductId: "com.example.premium.monthly",
                    AutoRenewProductId: "com.example.premium.monthly",
                    AppAccountToken: appAccountToken,
                    Environment: renewalEnvironment!,
                    AutoRenewStatus: true,
                    GracePeriodExpiresDate: null,
                    RenewalDate: now.AddDays(30),
                    IsInBillingRetryPeriod: false,
                    ExpirationIntent: null,
                    SignedDate: now)));
    }
}

internal sealed class FakeAppleProcessedEventRepository : IProcessedEventRepository
{
    public Task<bool> IsProcessedAsync(Guid projectId, string provider, string environment, string eventId, CancellationToken ct = default)
        => Task.FromResult(eventId == "123e4567-e89b-12d3-a456-426614174111");
}

internal sealed class FakeAppleWebhookEventLogRepository : IWebhookEventLogRepository
{
    public List<WebhookEventLogWriteModel> Models { get; } = [];

    public void Reset()
    {
        Models.Clear();
    }

    public Task UpsertAsync(WebhookEventLogWriteModel writeModel, CancellationToken ct = default)
    {
        Models.Add(writeModel);
        return Task.CompletedTask;
    }

    public Task<WebhookEventListPageReadModel> ListRecentAsync(Guid projectId, string environment, int limit, WebhookEventCursorReadModel? cursor, string? provider = null, WebhookEventStatusFilter? statusFilter = null, CancellationToken ct = default)
        => throw new NotSupportedException();
}

internal sealed class FakeSubscriptionWriteRepository : ISubscriptionWriteRepository
{
    public Task<SubscriptionWriteOutcome> UpsertWithEventAsync(
        NormalizedSubscription sub,
        string eventId,
        CancellationToken ct)
    {
        if (eventId == "123e4567-e89b-12d3-a456-426614174777")
        {
            throw new InvalidOperationException("Missing environment notifications should not be persisted.");
        }

        return Task.FromResult(eventId == "123e4567-e89b-12d3-a456-426614174333"
            ? SubscriptionWriteOutcome.Duplicate
            : SubscriptionWriteOutcome.Applied);
    }
}
