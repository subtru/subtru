using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubTru.Api.Platform.Authentication;
using SubTru.Infrastructure.Persistence.Postgres.ApiKeys;
using SubTru.Infrastructure.Persistence.Postgres.Authentication;
using SubTru.Infrastructure.Persistence.Postgres.Subscriptions;

namespace SubTru.Tests.Features.Access;

public sealed class UsersEndpointsContractTests : IClassFixture<UsersEndpointsWebApplicationFactory>
{
    private const string TestUserId = "test-user";
    private const string UnknownUserId = "unknown-user";
    private const string MultipleProductsUserId = "multiple-products-user";
    private const string DuplicateProductUserId = "duplicate-product-user";
    private const string GraceUserId = "grace-user";
    private const string PausedUserId = "paused-user";
    private const string BlankProductUserId = "blank-product-user";
    private const string MixedProviderGraceUserId = "mixed-provider-grace-user";
    private const string AppleGraceUserId = "apple-grace-user";
    private const string StripeGraceUserId = "stripe-grace-user";
    private const string GoogleRetryUserId = "google-retry-user";
    private const string ProjectId = "11111111-1111-1111-1111-111111111111";

    private readonly HttpClient _client;

    public UsersEndpointsContractTests(UsersEndpointsWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "subtru_live_test.secret");
    }

    [Fact]
    public async Task GetUserAccess_ResourceRoute_ReturnsEntitlementsPayload()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/users/{TestUserId}/access");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<GetAccessResponseDto>();
        Assert.NotNull(payload);
        var entitlement = Assert.Single(payload.Entitlements);
        Assert.Equal("prod_pro", entitlement.ProductId);
        Assert.True(entitlement.HasAccess);
        Assert.Equal("active_subscription", entitlement.AccessReason);
        Assert.True(entitlement.AccessEndsAt.HasValue);
    }

    [Fact]
    public async Task GetUserSubscriptions_WithApiKeyAuth_ReturnsForbidden()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/users/{TestUserId}/subscriptions");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ApiErrorResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("forbidden", payload.Error);
        Assert.Equal(
            "Subscription detail access requires the installation admin API key.",
            payload.Message);
    }

    [Fact]
    public async Task GetUserAccess_MissingAuth_ReturnsUnauthorized()
    {
        using var unauthenticatedClient = new UsersEndpointsWebApplicationFactory().CreateClient();

        var response = await unauthenticatedClient.GetAsync($"/v1/projects/{ProjectId}/users/{TestUserId}/access");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetUserSubscriptions_MissingAuth_ReturnsUnauthorized()
    {
        using var unauthenticatedClient = new UsersEndpointsWebApplicationFactory().CreateClient();

        var response = await unauthenticatedClient.GetAsync($"/v1/projects/{ProjectId}/users/{TestUserId}/subscriptions");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetUserAccess_BlankExternalUserId_ReturnsBadRequest()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/users/%20/access");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ApiErrorResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("missing_external_user_id", payload.Error);
    }

    [Fact]
    public async Task GetUserAccess_UnknownUser_ReturnsEmptyEntitlements()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/users/{UnknownUserId}/access");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<GetAccessResponseDto>();
        Assert.NotNull(payload);
        Assert.Empty(payload.Entitlements);
    }

    [Fact]
    public async Task GetUserAccess_MultipleProducts_ReturnsSortedEntitlements()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/users/{MultipleProductsUserId}/access");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<GetAccessResponseDto>();
        Assert.NotNull(payload);

        Assert.Collection(
            payload.Entitlements,
            alpha =>
            {
                Assert.Equal("alpha_tier", alpha.ProductId);
                Assert.False(alpha.HasAccess);
                Assert.Equal("expired_subscription", alpha.AccessReason);
            },
            beta =>
            {
                Assert.Equal("beta_tier", beta.ProductId);
                Assert.False(beta.HasAccess);
                Assert.Equal("paused_subscription", beta.AccessReason);
            },
            zeta =>
            {
                Assert.Equal("zeta_tier", zeta.ProductId);
                Assert.True(zeta.HasAccess);
                Assert.Equal("active_subscription", zeta.AccessReason);
            });
    }

    [Fact]
    public async Task GetUserAccess_DuplicateProductRows_CollapsesToSingleEntitlement()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/users/{DuplicateProductUserId}/access");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<GetAccessResponseDto>();
        Assert.NotNull(payload);

        var entitlement = Assert.Single(payload.Entitlements);
        Assert.Equal("prod_pro", entitlement.ProductId);
        Assert.True(entitlement.HasAccess);
        Assert.Equal("active_subscription", entitlement.AccessReason);
        Assert.True(entitlement.AccessEndsAt.HasValue);
    }

    [Fact]
    public async Task GetUserAccess_CanceledButNotExpired_ReturnsGraceEntitlement()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/users/{GraceUserId}/access");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<GetAccessResponseDto>();
        Assert.NotNull(payload);

        var entitlement = Assert.Single(payload.Entitlements);
        Assert.True(entitlement.HasAccess);
        Assert.Equal("canceled_subscription", entitlement.AccessReason);
    }

    [Fact]
    public async Task GetUserAccess_PausedProduct_ReturnsNoAccessEntitlement()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/users/{PausedUserId}/access");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<GetAccessResponseDto>();
        Assert.NotNull(payload);

        var entitlement = Assert.Single(payload.Entitlements);
        Assert.False(entitlement.HasAccess);
        Assert.Equal("paused_subscription", entitlement.AccessReason);
    }

    [Fact]
    public async Task GetUserAccess_BlankProductRows_AreIgnored()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/users/{BlankProductUserId}/access");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<GetAccessResponseDto>();
        Assert.NotNull(payload);

        var entitlement = Assert.Single(payload.Entitlements);
        Assert.Equal("prod_clean", entitlement.ProductId);
    }

    [Fact]
    public async Task GetUserAccess_ExpiredHigherPriorityRow_DoesNotOverrideGraceFromAnotherProvider()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/users/{MixedProviderGraceUserId}/access");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<GetAccessResponseDto>();
        Assert.NotNull(payload);

        var entitlement = Assert.Single(payload.Entitlements);
        Assert.Equal("gold_annual", entitlement.ProductId);
        Assert.True(entitlement.HasAccess);
        Assert.Equal("canceled_subscription", entitlement.AccessReason);
        Assert.True(entitlement.AccessEndsAt.HasValue);
        Assert.True(entitlement.AccessEndsAt > DateTimeOffset.UtcNow.AddDays(6));
    }

    [Fact]
    public async Task GetUserAccess_AppleGraceWithoutEnd_ReturnsEventDrivenAccess()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/users/{AppleGraceUserId}/access");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GetAccessResponseDto>();
        Assert.NotNull(payload);

        var entitlement = Assert.Single(payload.Entitlements);
        Assert.True(entitlement.HasAccess);
        Assert.Equal("payment_failed_in_grace_period", entitlement.AccessReason);
        Assert.Null(entitlement.AccessEndsAt);
    }

    [Fact]
    public async Task GetUserAccess_StripePastDueWithoutEnd_ReturnsBillingGraceAccess()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/users/{StripeGraceUserId}/access");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GetAccessResponseDto>();
        Assert.NotNull(payload);

        var entitlement = Assert.Single(payload.Entitlements);
        Assert.True(entitlement.HasAccess);
        Assert.Equal("payment_failed_in_grace_period", entitlement.AccessReason);
        Assert.Null(entitlement.AccessEndsAt);
    }

    [Fact]
    public async Task GetUserAccess_GoogleOnHold_ReturnsBillingSuspendedWithoutAccess()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/users/{GoogleRetryUserId}/access");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GetAccessResponseDto>();
        Assert.NotNull(payload);

        var entitlement = Assert.Single(payload.Entitlements);
        Assert.False(entitlement.HasAccess);
        Assert.Equal("payment_failed_past_grace_period", entitlement.AccessReason);
    }

    [Fact]
    public async Task UsersReadEndpoints_WrongMethod_ReturnsMethodNotAllowed()
    {
        var accessResponse = await _client.PostAsync($"/v1/projects/{ProjectId}/users/{TestUserId}/access", content: null);
        var subscriptionsResponse = await _client.PostAsync($"/v1/projects/{ProjectId}/users/{TestUserId}/subscriptions", content: null);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, accessResponse.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, subscriptionsResponse.StatusCode);
    }

    private sealed record GetAccessResponseDto(
        IReadOnlyList<AccessEntitlementDto> Entitlements
    );

    private sealed record AccessEntitlementDto(
        string ProductId,
        bool HasAccess,
        string AccessReason,
        DateTimeOffset? AccessEndsAt
    );

    private sealed record SubscriptionDto(
        string Provider,
        string ProductId,
        string? PriceId,
        string Status,
        string? ProviderStatus,
        DateTimeOffset? CurrentPeriodEnd
    );

    private sealed record GetSubscriptionsEnvelopeDto(
        IReadOnlyList<SubscriptionDto> Items,
        string? NextCursor
    );

    private sealed record ApiErrorResponseDto(
        string Error,
        string Message
    );
}

public sealed class UserSubscriptionsAdminAuthTests : IClassFixture<UsersSubscriptionsAdminWebApplicationFactory>
{
    private const string TestUserId = "test-user";
    private const string MixedProviderGraceUserId = "mixed-provider-grace-user";
    private const string UnknownUserId = "unknown-user";
    private const string ProjectId = "11111111-1111-1111-1111-111111111111";

    private readonly HttpClient _client;

    public UserSubscriptionsAdminAuthTests(UsersSubscriptionsAdminWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "fake-admin-token");
    }

    [Fact]
    public async Task GetUserSubscriptions_WithAdminAuth_ReturnsListEnvelope()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/users/{TestUserId}/subscriptions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<GetSubscriptionsEnvelopeDto>();
        Assert.NotNull(payload);
        Assert.NotNull(payload.Items);
        Assert.Single(payload.Items);
        Assert.Null(payload.NextCursor);
        Assert.Equal("stripe", payload.Items[0].Provider);
        Assert.Equal("sub_default", payload.Items[0].ProviderSubscriptionId);
        Assert.Null(payload.Items[0].ProviderStatus);
    }

    [Fact]
    public async Task GetUserSubscriptions_WithAdminAuth_IncludesProviderStatusWhenPresent()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/users/{MixedProviderGraceUserId}/subscriptions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<GetSubscriptionsEnvelopeDto>();
        Assert.NotNull(payload);
        Assert.NotNull(payload.Items);

        var google = Assert.Single(payload.Items.Where(x => x.Provider == "google"));
        Assert.Equal("SUBSCRIPTION_STATE_IN_GRACE_PERIOD", google.ProviderStatus);

        var stripe = Assert.Single(payload.Items.Where(x => x.Provider == "stripe"));
        Assert.Null(stripe.ProviderStatus);
    }

    [Fact]
    public async Task GetUserSubscriptions_WithAdminAuth_BlankExternalUserId_ReturnsBadRequest()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/users/%20/subscriptions");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ApiErrorResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("missing_external_user_id", payload.Error);
    }

    [Fact]
    public async Task GetUserSubscriptions_WithAdminAuth_UnknownUser_ReturnsEmptyList()
    {
        var response = await _client.GetAsync($"/v1/projects/{ProjectId}/users/{UnknownUserId}/subscriptions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<GetSubscriptionsEnvelopeDto>();
        Assert.NotNull(payload);
        Assert.NotNull(payload.Items);
        Assert.Empty(payload.Items);
        Assert.Null(payload.NextCursor);
    }

    private sealed record SubscriptionDto(
        string Provider,
        string ProviderSubscriptionId,
        string ProductId,
        string? PriceId,
        string Status,
        string? ProviderStatus,
        DateTimeOffset? CurrentPeriodEnd
    );

    private sealed record GetSubscriptionsEnvelopeDto(
        IReadOnlyList<SubscriptionDto> Items,
        string? NextCursor
    );

    private sealed record ApiErrorResponseDto(
        string Error,
        string Message
    );
}

public sealed class UsersEndpointsWebApplicationFactory : WebApplicationFactory<Program>
{
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
                ["AppleVerification:RootCertificateDirectory"] = TestAppleTrustStoreDirectory.DirectoryPath
            };

            configBuilder.AddInMemoryCollection(config);
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IProjectAuthService>();
            services.RemoveAll<ISubscriptionReadRepository>();

            services.AddSingleton<IProjectAuthService>(new AllowAllProjectAuthServiceForUsersEndpoints());
            services.AddScoped<ISubscriptionReadRepository, FakeSubscriptionReadRepository>();
        });
    }
}

public sealed class UsersSubscriptionsAdminWebApplicationFactory : WebApplicationFactory<Program>
{
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
                ["AppleVerification:RootCertificateDirectory"] = TestAppleTrustStoreDirectory.DirectoryPath
            };

            configBuilder.AddInMemoryCollection(config);
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IProjectAuthService>();
            services.RemoveAll<IProjectResolver>();
            services.RemoveAll<ISubscriptionReadRepository>();

            services.AddScoped<IProjectResolver>(_ => new FakeAdminProjectResolverForUsersEndpoints(
                Guid.Parse("22222222-2222-2222-2222-222222222222")));
            services.AddScoped<ISubscriptionReadRepository, FakeSubscriptionReadRepository>();
        });
    }
}

internal sealed class AllowAllProjectAuthServiceForUsersEndpoints : IProjectAuthService
{
    public Task<ValidatedApiKey?> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
        => Task.FromResult<ValidatedApiKey?>(new ValidatedApiKey(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "production"));
}

internal sealed class FakeAdminProjectResolverForUsersEndpoints : IProjectResolver
{
    private readonly Guid _userId;

    public FakeAdminProjectResolverForUsersEndpoints(Guid userId) => _userId = userId;

    public Task<AuthResult?> ResolveAsync(string bearerToken, CancellationToken ct = default)
        => Task.FromResult<AuthResult?>(new AuthResult(
            UserId: _userId,
            ProjectId: null,
            IsApiKeyAuth: false,
            IsAdminAuth: true));
}

internal sealed class FakeSubscriptionReadRepository : ISubscriptionReadRepository
{
    public Task<IReadOnlyList<SubscriptionReadModel>> GetByUserAsync(Guid projectId, string environment, string externalUserId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var currentPeriodEnd = now.AddDays(30);
        IReadOnlyList<SubscriptionReadModel> results = externalUserId switch
        {
            "unknown-user" => Array.Empty<SubscriptionReadModel>(),
            "multiple-products-user" =>
            [
                new SubscriptionReadModel(
                    ProjectId: projectId,
                    Environment: environment,
                    ExternalUserId: externalUserId,
                    Provider: "stripe",
                    ProviderSubscriptionId: "sub_zeta",
                    ProductId: "zeta_tier",
                    PriceId: "price_zeta",
                    Status: "active",
                    ProviderStatus: null,
                    CurrentPeriodEnd: currentPeriodEnd,
                    AccessEndsAt: currentPeriodEnd,
                    CancelAtPeriodEnd: false),
                new SubscriptionReadModel(
                    ProjectId: projectId,
                    Environment: environment,
                    ExternalUserId: externalUserId,
                    Provider: "apple",
                    ProviderSubscriptionId: "apple_alpha",
                    ProductId: "alpha_tier",
                    PriceId: null,
                    Status: "expired",
                    ProviderStatus: null,
                    CurrentPeriodEnd: now.AddDays(-2),
                    AccessEndsAt: now.AddDays(-2),
                    CancelAtPeriodEnd: false),
                new SubscriptionReadModel(
                    ProjectId: projectId,
                    Environment: environment,
                    ExternalUserId: externalUserId,
                    Provider: "stripe",
                    ProviderSubscriptionId: "sub_beta",
                    ProductId: "beta_tier",
                    PriceId: "price_beta",
                    Status: "paused",
                    ProviderStatus: null,
                    CurrentPeriodEnd: now.AddDays(5),
                    AccessEndsAt: now.AddDays(5),
                    CancelAtPeriodEnd: false)
            ],
            "duplicate-product-user" =>
            [
                new SubscriptionReadModel(
                    ProjectId: projectId,
                    Environment: environment,
                    ExternalUserId: externalUserId,
                    Provider: "stripe",
                    ProviderSubscriptionId: "sub_monthly",
                    ProductId: "prod_pro",
                    PriceId: "price_monthly",
                    Status: "active",
                    ProviderStatus: null,
                    CurrentPeriodEnd: currentPeriodEnd,
                    AccessEndsAt: currentPeriodEnd,
                    CancelAtPeriodEnd: false),
                new SubscriptionReadModel(
                    ProjectId: projectId,
                    Environment: environment,
                    ExternalUserId: externalUserId,
                    Provider: "apple",
                    ProviderSubscriptionId: "apple_pro",
                    ProductId: "prod_pro",
                    PriceId: null,
                    Status: "canceled",
                    ProviderStatus: null,
                    CurrentPeriodEnd: now.AddDays(10),
                    AccessEndsAt: now.AddDays(10),
                    CancelAtPeriodEnd: true)
            ],
            "grace-user" =>
            [
                new SubscriptionReadModel(
                    ProjectId: projectId,
                    Environment: environment,
                    ExternalUserId: externalUserId,
                    Provider: "stripe",
                    ProviderSubscriptionId: "sub_grace",
                    ProductId: "prod_grace",
                    PriceId: "price_grace",
                    Status: "canceled",
                    ProviderStatus: null,
                    CurrentPeriodEnd: now.AddDays(3),
                    AccessEndsAt: now.AddDays(3),
                    CancelAtPeriodEnd: true)
            ],
            "paused-user" =>
            [
                new SubscriptionReadModel(
                    ProjectId: projectId,
                    Environment: environment,
                    ExternalUserId: externalUserId,
                    Provider: "stripe",
                    ProviderSubscriptionId: "sub_paused",
                    ProductId: "prod_paused",
                    PriceId: "price_paused",
                    Status: "paused",
                    ProviderStatus: null,
                    CurrentPeriodEnd: now.AddDays(10),
                    AccessEndsAt: now.AddDays(10),
                    CancelAtPeriodEnd: false)
            ],
            "blank-product-user" =>
            [
                new SubscriptionReadModel(
                    ProjectId: projectId,
                    Environment: environment,
                    ExternalUserId: externalUserId,
                    Provider: "stripe",
                    ProviderSubscriptionId: "sub_blank",
                    ProductId: "",
                    PriceId: "price_blank",
                    Status: "active",
                    ProviderStatus: null,
                    CurrentPeriodEnd: currentPeriodEnd,
                    AccessEndsAt: currentPeriodEnd,
                    CancelAtPeriodEnd: false),
                new SubscriptionReadModel(
                    ProjectId: projectId,
                    Environment: environment,
                    ExternalUserId: externalUserId,
                    Provider: "apple",
                    ProviderSubscriptionId: "apple_clean",
                    ProductId: "prod_clean",
                    PriceId: null,
                    Status: "expired",
                    ProviderStatus: null,
                    CurrentPeriodEnd: now.AddDays(-1),
                    AccessEndsAt: now.AddDays(-1),
                    CancelAtPeriodEnd: false)
            ],
            "mixed-provider-grace-user" =>
            [
                new SubscriptionReadModel(
                    ProjectId: projectId,
                    Environment: environment,
                    ExternalUserId: externalUserId,
                    Provider: "google",
                    ProviderSubscriptionId: "google_gold",
                    ProductId: "gold_annual",
                    PriceId: null,
                    Status: "billing_grace",
                    ProviderStatus: "SUBSCRIPTION_STATE_IN_GRACE_PERIOD",
                    CurrentPeriodEnd: now.AddDays(-1),
                    AccessEndsAt: now.AddDays(-1),
                    CancelAtPeriodEnd: false),
                new SubscriptionReadModel(
                    ProjectId: projectId,
                    Environment: environment,
                    ExternalUserId: externalUserId,
                    Provider: "stripe",
                    ProviderSubscriptionId: "sub_gold",
                    ProductId: "gold_annual",
                    PriceId: "price_gold",
                    Status: "canceled",
                    ProviderStatus: null,
                    CurrentPeriodEnd: now.AddDays(7),
                    AccessEndsAt: now.AddDays(7),
                    CancelAtPeriodEnd: true)
            ],
            "apple-grace-user" =>
            [
                new SubscriptionReadModel(
                    ProjectId: projectId,
                    Environment: environment,
                    ExternalUserId: externalUserId,
                    Provider: "apple",
                    ProviderSubscriptionId: "apple_grace",
                    ProductId: "prod_apple_grace",
                    PriceId: null,
                    Status: "billing_grace",
                    ProviderStatus: "4",
                    CurrentPeriodEnd: now.AddDays(-1),
                    AccessEndsAt: null,
                    CancelAtPeriodEnd: false)
            ],
            "stripe-grace-user" =>
            [
                new SubscriptionReadModel(
                    ProjectId: projectId,
                    Environment: environment,
                    ExternalUserId: externalUserId,
                    Provider: "stripe",
                    ProviderSubscriptionId: "stripe_grace",
                    ProductId: "prod_stripe_grace",
                    PriceId: "price_grace",
                    Status: "billing_grace",
                    ProviderStatus: "past_due",
                    CurrentPeriodEnd: now.AddDays(-1),
                    AccessEndsAt: null,
                    CancelAtPeriodEnd: false)
            ],
            "google-retry-user" =>
            [
                new SubscriptionReadModel(
                    ProjectId: projectId,
                    Environment: environment,
                    ExternalUserId: externalUserId,
                    Provider: "google",
                    ProviderSubscriptionId: "google_retry",
                    ProductId: "prod_google_retry",
                    PriceId: null,
                    Status: "billing_suspended",
                    ProviderStatus: "SUBSCRIPTION_STATE_ON_HOLD",
                    CurrentPeriodEnd: now.AddDays(5),
                    AccessEndsAt: now.AddDays(5),
                    CancelAtPeriodEnd: false)
            ],
            _ =>
            [
                new SubscriptionReadModel(
                    ProjectId: projectId,
                    Environment: environment,
                    ExternalUserId: externalUserId,
                    Provider: "stripe",
                    ProviderSubscriptionId: "sub_default",
                    ProductId: "prod_pro",
                    PriceId: "price_monthly",
                    Status: "active",
                    ProviderStatus: null,
                    CurrentPeriodEnd: currentPeriodEnd,
                    AccessEndsAt: currentPeriodEnd,
                    CancelAtPeriodEnd: false)
            ]
        };

        return Task.FromResult(results);
    }

    public Task<string?> GetExternalUserIdByProviderSubscriptionIdAsync(
        Guid projectId,
        string environment,
        string provider,
        string providerSubscriptionId,
        CancellationToken ct)
    {
        var userId = providerSubscriptionId switch
        {
            "expired-token-123" => "test-user",
            _ => null
        };

        return Task.FromResult<string?>(userId);
    }
}
