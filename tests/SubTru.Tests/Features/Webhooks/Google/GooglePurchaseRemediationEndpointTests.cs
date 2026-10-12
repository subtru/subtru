using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SubTru.Api.Platform.Authentication;
using SubTru.Domain.Subscriptions;
using SubTru.Infrastructure.Persistence.Postgres.ApiKeys;
using SubTru.Infrastructure.Persistence.Postgres.Authentication;
using SubTru.Infrastructure.Providers.Google;
using SubTru.Infrastructure.Persistence.Postgres.Subscriptions;
using SubTru.Infrastructure.Persistence.Postgres.ProviderConfiguration;
using SubTru.Infrastructure.Persistence.Postgres.Google;
using SubTru.Infrastructure.Persistence.Postgres.WebhookEvents;
using SubTru.Infrastructure.Persistence.Postgres.WebhookKeys;

namespace SubTru.Tests.Features.Webhooks.Google;

public sealed class GooglePurchaseRemediationEndpointTests
    : IClassFixture<GooglePurchaseRemediationWebApplicationFactory>
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly GooglePurchaseRemediationWebApplicationFactory _factory;

    public GooglePurchaseRemediationEndpointTests(
        GooglePurchaseRemediationWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.ResetState();
    }

    [Fact]
    public async Task PostBindAndAcknowledge_WithApiKeyAuth_ReturnsForbidden()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "subtru_live_test.secret");

        var response = await client.PostAsJsonAsync(
            $"/v1/projects/{ProjectId}/google/purchases/token-active:bind-and-acknowledge",
            new { externalUserId = "user-123" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ApiErrorResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("forbidden", payload.Error);
    }

    [Fact]
    public async Task PostBindAndAcknowledge_WithoutAuth_ReturnsUnauthorized()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/v1/projects/{ProjectId}/google/purchases/token-active:bind-and-acknowledge",
            new { externalUserId = "user-123" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostBindAndAcknowledge_BlankExternalUserId_ReturnsBadRequest()
    {
        using var client = CreateOwnerClient();

        var response = await client.PostAsJsonAsync(
            $"/v1/projects/{ProjectId}/google/purchases/token-active:bind-and-acknowledge",
            new { externalUserId = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ApiErrorResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("missing_external_user_id", payload.Error);
    }

    [Fact]
    public async Task PostBindAndAcknowledge_SuccessfulDirectPurchase_PersistsAcknowledgesAndAudits()
    {
        using var client = CreateOwnerClient();

        var response = await client.PostAsJsonAsync(
            $"/v1/projects/{ProjectId}/google/purchases/token-active:bind-and-acknowledge",
            new { externalUserId = "user-123" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<BindGooglePurchaseResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("token-active", payload.RequestedPurchaseToken);
        Assert.Equal("token-active", payload.EffectivePurchaseToken);
        Assert.Equal("user-123", payload.ExternalUserId);
        Assert.Equal("active", payload.Status);
        Assert.Equal(GoogleSubscriptionMapper.SubscriptionStateActive, payload.ProviderStatus);

        var write = Assert.Single(_factory.GoogleWriteRepository.ManualWrites);
        Assert.Equal("user-123", write.Subscription.ExternalUserId);
        Assert.Equal("token-active", write.Subscription.ProviderSubscriptionId);
        Assert.Null(write.LinkedPurchaseToken);

        var acknowledgeCall = Assert.Single(_factory.GooglePlayClient.AcknowledgeCalls);
        Assert.Equal("token-active", acknowledgeCall.PurchaseToken);
        Assert.Equal("premium_monthly", acknowledgeCall.SubscriptionId);

        var audit = Assert.Single(_factory.RemediationAuditRepository.Writes);
        Assert.Equal(ProjectId, audit.ProjectId);
        Assert.Equal("token-active", audit.RequestedPurchaseToken);
        Assert.Equal("token-active", audit.EffectivePurchaseToken);
        Assert.Equal("user-123", audit.ExternalUserId);
        Assert.Equal("installation_admin", audit.Actor);
        Assert.Equal("active", audit.Status);
        Assert.Equal(GoogleSubscriptionMapper.SubscriptionStateActive, audit.ProviderStatus);
    }

    [Fact]
    public async Task PostBindAndAcknowledge_LinkedPendingPurchaseCanceled_UsesEffectiveToken()
    {
        using var client = CreateOwnerClient();

        var response = await client.PostAsJsonAsync(
            $"/v1/projects/{ProjectId}/google/purchases/token-linked-request:bind-and-acknowledge",
            new { externalUserId = "user-linked" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<BindGooglePurchaseResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("token-linked-request", payload.RequestedPurchaseToken);
        Assert.Equal("token-linked-active", payload.EffectivePurchaseToken);

        Assert.Contains(_factory.GooglePlayClient.FetchCalls, x => x.PurchaseToken == "token-linked-request");
        Assert.Contains(_factory.GooglePlayClient.FetchCalls, x => x.PurchaseToken == "token-linked-active");

        var write = Assert.Single(_factory.GoogleWriteRepository.ManualWrites);
        Assert.Equal("token-linked-active", write.Subscription.ProviderSubscriptionId);
        Assert.Equal("token-legacy", write.LinkedPurchaseToken);

        var acknowledgeCall = Assert.Single(_factory.GooglePlayClient.AcknowledgeCalls);
        Assert.Equal("token-linked-active", acknowledgeCall.PurchaseToken);

        var audit = Assert.Single(_factory.RemediationAuditRepository.Writes);
        Assert.Equal("token-linked-request", audit.RequestedPurchaseToken);
        Assert.Equal("token-linked-active", audit.EffectivePurchaseToken);
        Assert.Equal("token-legacy", audit.LinkedPurchaseToken);
    }

    [Fact]
    public async Task PostBindAndAcknowledge_AlreadyAcknowledged_ReturnsConflict()
    {
        using var client = CreateOwnerClient();

        var response = await client.PostAsJsonAsync(
            $"/v1/projects/{ProjectId}/google/purchases/token-already-acked:bind-and-acknowledge",
            new { externalUserId = "user-123" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ApiErrorResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("google_purchase_already_acknowledged", payload.Error);

        Assert.Empty(_factory.GoogleWriteRepository.ManualWrites);
        Assert.Empty(_factory.RemediationAuditRepository.Writes);
    }

    [Fact]
    public async Task PostBindAndAcknowledge_NotFound_ReturnsNotFound()
    {
        using var client = CreateOwnerClient();

        var response = await client.PostAsJsonAsync(
            $"/v1/projects/{ProjectId}/google/purchases/token-not-found:bind-and-acknowledge",
            new { externalUserId = "user-123" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ApiErrorResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("google_subscription_not_found", payload.Error);
    }

    [Fact]
    public async Task PostBindAndAcknowledge_TransientFetchFailure_ReturnsInternalServerError()
    {
        using var client = CreateOwnerClient();

        var response = await client.PostAsJsonAsync(
            $"/v1/projects/{ProjectId}/google/purchases/token-transient-fetch:bind-and-acknowledge",
            new { externalUserId = "user-123" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ApiErrorResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("google_subscription_fetch_unavailable", payload.Error);
    }

    private HttpClient CreateOwnerClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "fake-admin-token");
        return client;
    }

    private sealed record BindGooglePurchaseResponseDto(
        string RequestedPurchaseToken,
        string EffectivePurchaseToken,
        string ExternalUserId,
        string Status,
        string? ProviderStatus,
        DateTimeOffset CurrentPeriodEnd,
        DateTimeOffset AcknowledgedAt
    );

    private sealed record ApiErrorResponseDto(
        string Error,
        string Message
    );
}

public sealed class GooglePurchaseRemediationWebApplicationFactory
    : WebApplicationFactory<Program>
{
    internal FakeRemediationProjectResolver ProjectResolver { get; } = new();
    internal FakeRemediationProviderConfigRepository ProviderConfigRepository { get; } = new();
    internal FakeRemediationGooglePlaySubscriptionClient GooglePlayClient { get; } = new();
    internal FakeRemediationGoogleSubscriptionWriteRepository GoogleWriteRepository { get; } = new();
    internal FakeGooglePurchaseRemediationAuditRepository RemediationAuditRepository { get; } = new();

    public void ResetState()
    {
        GooglePlayClient.Reset();
        GoogleWriteRepository.Reset();
        RemediationAuditRepository.Reset();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        TestWebHostConfiguration.UseAdminApiKey(builder);
        builder.UseEnvironment("Development");
        builder.UseSetting("Logging:EventLog:LogLevel:Default", "None");
        builder.ConfigureLogging(logging => logging.ClearProviders());

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            var config = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=localhost;Port=5432;Database=subtru_test;Username=test;Password=test",
                ["API_KEY_PEPPER"] = "test-pepper",
                ["SUBTRU_ENCRYPTION_KEY"] = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                ["WebhookUrls:PublicBaseUrl"] = "https://api.test.subtru.com",
                ["AppleVerification:RootCertificateDirectory"] = TestAppleTrustStoreDirectory.DirectoryPath
            };

            configBuilder.AddInMemoryCollection(config);
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IProjectResolver>();
            services.RemoveAll<IProviderConfigRepository>();
            services.RemoveAll<IGooglePlaySubscriptionClient>();
            services.RemoveAll<IGoogleSubscriptionWriteRepository>();
            services.RemoveAll<IGooglePurchaseRemediationAuditRepository>();

            services.AddSingleton<IProjectResolver>(ProjectResolver);
            services.AddSingleton<IProviderConfigRepository>(ProviderConfigRepository);
            services.AddSingleton<IGooglePlaySubscriptionClient>(GooglePlayClient);
            services.AddSingleton<IGoogleSubscriptionWriteRepository>(GoogleWriteRepository);
            services.AddSingleton<IGooglePurchaseRemediationAuditRepository>(RemediationAuditRepository);
        });
    }
}

internal sealed class FakeRemediationProjectResolver : IProjectResolver
{
    public Task<AuthResult?> ResolveAsync(
        string bearerToken,
        CancellationToken ct = default)
        => Task.FromResult<AuthResult?>(bearerToken switch
        {
            "fake-admin-token" => new AuthResult(
                UserId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
                ProjectId: null,
                IsApiKeyAuth: false,
                IsAdminAuth: true),
            "subtru_live_test.secret" => new AuthResult(
                UserId: null,
                ProjectId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
                IsApiKeyAuth: true),
            _ => null
        });
}

internal sealed class FakeRemediationProviderConfigRepository
    : IProviderConfigRepository
{
    public Task<AppleProviderConfigReadModel?> GetAppleProviderConfigAsync(
        Guid projectId,
        CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<AppleProviderConfigReadModel> UpsertAppleProviderConfigAsync(
        Guid projectId,
        AppleProviderConfigWriteModel writeModel,
        CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<GoogleProviderConfigReadModel?> GetGoogleProviderConfigAsync(
        Guid projectId,
        CancellationToken ct = default)
        => Task.FromResult<GoogleProviderConfigReadModel?>(new GoogleProviderConfigReadModel(
            PackageName: "com.example.app",
            PubSubAuthServiceAccountEmail: "push-auth@example.iam.gserviceaccount.com",
            HasServiceAccountCredentials: true,
            ServiceAccountClientEmail: "svc-play@example.iam.gserviceaccount.com",
            ServiceAccountKeyId: "key-123",
            ServiceAccountCreatedAt: DateTimeOffset.UtcNow.AddDays(-7),
            ServiceAccountLastValidatedAt: DateTimeOffset.UtcNow.AddHours(-2),
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-7),
            UpdatedAt: DateTimeOffset.UtcNow));

    public Task<GoogleProviderCredentialReadModel?> GetGoogleProviderCredentialAsync(
        Guid projectId,
        CancellationToken ct = default)
        => Task.FromResult<GoogleProviderCredentialReadModel?>(new GoogleProviderCredentialReadModel(
            ServiceAccountJson: FakeGoogleServiceAccountValidator.ValidJson,
            ClientEmail: "svc-play@example.iam.gserviceaccount.com",
            PrivateKeyId: "key-123",
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-7),
            LastValidatedAt: DateTimeOffset.UtcNow.AddHours(-2)));

    public Task<GoogleProviderConfigReadModel> UpsertGoogleProviderConfigAsync(
        Guid projectId,
        GoogleProviderConfigWriteModel writeModel,
        ValidatedGoogleServiceAccount? validatedServiceAccount,
        CancellationToken ct = default)
        => throw new NotSupportedException();
}

internal sealed class FakeRemediationGooglePlaySubscriptionClient
    : IGooglePlaySubscriptionClient
{
    public List<(string PackageName, string PurchaseToken)> FetchCalls { get; } = [];
    public List<(string PackageName, string PurchaseToken, string? SubscriptionId)> AcknowledgeCalls { get; } = [];

    public void Reset()
    {
        FetchCalls.Clear();
        AcknowledgeCalls.Clear();
    }

    public Task<GooglePlaySubscriptionPurchase> GetSubscriptionAsync(
        string packageName,
        string purchaseToken,
        string serviceAccountJson,
        CancellationToken ct = default)
    {
        FetchCalls.Add((packageName, purchaseToken));

        return purchaseToken switch
        {
            "token-active" => Task.FromResult(CreateActivePurchase()),
            "token-linked-request" => Task.FromResult(CreatePurchase(
                subscriptionState: GoogleSubscriptionMapper.SubscriptionStatePendingPurchaseCanceled,
                acknowledgementState: GoogleSubscriptionMapper.AcknowledgementStatePending,
                linkedPurchaseToken: "token-linked-active")),
            "token-linked-active" => Task.FromResult(CreateActivePurchase(linkedPurchaseToken: "token-legacy")),
            "token-already-acked" => Task.FromResult(CreatePurchase(
                subscriptionState: GoogleSubscriptionMapper.SubscriptionStateActive,
                acknowledgementState: "ACKNOWLEDGEMENT_STATE_ACKNOWLEDGED")),
            "token-transient-fetch" => Task.FromException<GooglePlaySubscriptionPurchase>(
                new GooglePlayApiException(
                    "google_subscription_fetch_unavailable",
                    "Google Play subscription fetch failed right now.",
                    (HttpStatusCode)429,
                    isTransient: true)),
            "token-not-found" => Task.FromException<GooglePlaySubscriptionPurchase>(
                new GooglePlayApiException(
                    "google_subscription_fetch_failed",
                    "Google Play subscription fetch failed.",
                    HttpStatusCode.NotFound)),
            _ => Task.FromResult(CreatePurchase(
                subscriptionState: GoogleSubscriptionMapper.SubscriptionStatePending,
                acknowledgementState: GoogleSubscriptionMapper.AcknowledgementStatePending))
        };
    }

    public Task AcknowledgeAsync(
        string packageName,
        string purchaseToken,
        string serviceAccountJson,
        string? subscriptionId = null,
        CancellationToken ct = default)
    {
        AcknowledgeCalls.Add((packageName, purchaseToken, subscriptionId));
        return Task.CompletedTask;
    }

    private static GooglePlaySubscriptionPurchase CreateActivePurchase(
        string? linkedPurchaseToken = null)
        => CreatePurchase(
            subscriptionState: GoogleSubscriptionMapper.SubscriptionStateActive,
            acknowledgementState: GoogleSubscriptionMapper.AcknowledgementStatePending,
            linkedPurchaseToken: linkedPurchaseToken);

    private static GooglePlaySubscriptionPurchase CreatePurchase(
        string subscriptionState,
        string acknowledgementState,
        string? linkedPurchaseToken = null)
        => new(
            SubscriptionState: subscriptionState,
            AcknowledgementState: acknowledgementState,
            LinkedPurchaseToken: linkedPurchaseToken,
            LineItems:
            [
                new GooglePlaySubscriptionLineItem(
                    ProductId: "premium_monthly",
                    ExpiryTime: DateTimeOffset.UtcNow.AddDays(30).ToString("O"),
                    AutoRenewingPlan: new GooglePlayAutoRenewingPlan(
                        AutoRenewEnabled: true,
                        InstallmentDetails: null),
                    PrepaidPlan: null,
                    OfferDetails: new GooglePlayOfferDetails(
                        BasePlanId: "monthly-base",
                        OfferId: null),
                    DeferredItemReplacement: null,
                    ItemReplacement: null,
                    OfferPhase: null)
            ],
            ExternalAccountIdentifiers: null,
            OutOfAppPurchaseContext: null);
}

internal sealed class FakeRemediationGoogleSubscriptionWriteRepository
    : IGoogleSubscriptionWriteRepository
{
    public List<(NormalizedSubscription Subscription, string? LinkedPurchaseToken)> ManualWrites { get; } = [];

    public void Reset()
    {
        ManualWrites.Clear();
    }

    public Task<SubscriptionWriteOutcome> UpsertGoogleWithEventAsync(
        NormalizedSubscription sub,
        string eventId,
        string? linkedPurchaseToken,
        CancellationToken ct)
        => throw new NotSupportedException();

    public Task UpsertGoogleManualAsync(
        NormalizedSubscription sub,
        string? linkedPurchaseToken,
        CancellationToken ct)
    {
        ManualWrites.Add((sub, linkedPurchaseToken));
        return Task.CompletedTask;
    }
}

internal sealed class FakeGooglePurchaseRemediationAuditRepository
    : IGooglePurchaseRemediationAuditRepository
{
    public List<GooglePurchaseRemediationAuditWriteModel> Writes { get; } = [];

    public void Reset()
    {
        Writes.Clear();
    }

    public Task InsertAsync(
        GooglePurchaseRemediationAuditWriteModel writeModel,
        CancellationToken ct = default)
    {
        Writes.Add(writeModel);
        return Task.CompletedTask;
    }
}
