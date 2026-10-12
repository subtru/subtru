using SubTru.Domain.Access;
using SubTru.Domain.Subscriptions;
using Xunit;

namespace SubTru.Tests.Domain;

public sealed class DeriveEntitlementsTests
{
    private static readonly DateTimeOffset Now =
        new DateTimeOffset(2026, 01, 01, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GroupsByProductId_SortsAscending_AndIncludesHistoricalProducts()
    {
        var subs = new[]
        {
            new Subscription(
                Provider: SubscriptionProvider.Stripe,
                ProductId: "zeta_tier",
                Status: SubscriptionStatus.Active,
                ProviderStatus: null,
                CurrentPeriodEnd: Now.AddDays(30),
                AccessEndsAt: Now.AddDays(30),
                CancelAtPeriodEnd: false
            ),
            new Subscription(
                Provider: SubscriptionProvider.Apple,
                ProductId: "alpha_tier",
                Status: SubscriptionStatus.Expired,
                ProviderStatus: null,
                CurrentPeriodEnd: Now.AddDays(-2),
                AccessEndsAt: Now.AddDays(-2),
                CancelAtPeriodEnd: false
            ),
            new Subscription(
                Provider: SubscriptionProvider.Stripe,
                ProductId: "beta_tier",
                Status: SubscriptionStatus.Paused,
                ProviderStatus: null,
                CurrentPeriodEnd: Now.AddDays(5),
                AccessEndsAt: Now.AddDays(5),
                CancelAtPeriodEnd: false
            )
        };

        var result = AccessStateDeriver.DeriveEntitlements(subs, Now);

        Assert.Collection(
            result,
            alpha =>
            {
                Assert.Equal("alpha_tier", alpha.ProductId);
                Assert.False(alpha.HasAccess);
                Assert.Equal(AccessReason.ExpiredSubscription, alpha.AccessReason);
            },
            beta =>
            {
                Assert.Equal("beta_tier", beta.ProductId);
                Assert.False(beta.HasAccess);
                Assert.Equal(AccessReason.PausedSubscription, beta.AccessReason);
            },
            zeta =>
            {
                Assert.Equal("zeta_tier", zeta.ProductId);
                Assert.True(zeta.HasAccess);
                Assert.Equal(AccessReason.ActiveSubscription, zeta.AccessReason);
                Assert.Equal(Now.AddDays(30), zeta.AccessEndsAt);
            });
    }

    [Fact]
    public void DuplicateProductRows_CollapseToSingleEntitlement()
    {
        var subs = new[]
        {
            new Subscription(
                Provider: SubscriptionProvider.Stripe,
                ProductId: "prod_pro",
                Status: SubscriptionStatus.Active,
                ProviderStatus: null,
                CurrentPeriodEnd: Now.AddDays(10),
                AccessEndsAt: Now.AddDays(10),
                CancelAtPeriodEnd: false
            ),
            new Subscription(
                Provider: SubscriptionProvider.Apple,
                ProductId: "prod_pro",
                Status: SubscriptionStatus.Active,
                ProviderStatus: null,
                CurrentPeriodEnd: Now.AddDays(20),
                AccessEndsAt: Now.AddDays(20),
                CancelAtPeriodEnd: false
            )
        };

        var result = AccessStateDeriver.DeriveEntitlements(subs, Now);

        var entitlement = Assert.Single(result);
        Assert.Equal("prod_pro", entitlement.ProductId);
        Assert.True(entitlement.HasAccess);
        Assert.Equal(AccessReason.ActiveSubscription, entitlement.AccessReason);
        Assert.Equal(Now.AddDays(20), entitlement.AccessEndsAt);
    }

    [Fact]
    public void CanceledButNotExpired_UsesCancelingReason()
    {
        var subs = new[]
        {
            new Subscription(
                Provider: SubscriptionProvider.Stripe,
                ProductId: "prod_grace",
                Status: SubscriptionStatus.Canceled,
                ProviderStatus: null,
                CurrentPeriodEnd: Now.AddDays(3),
                AccessEndsAt: Now.AddDays(3),
                CancelAtPeriodEnd: true
            )
        };

        var result = AccessStateDeriver.DeriveEntitlements(subs, Now);

        var entitlement = Assert.Single(result);
        Assert.True(entitlement.HasAccess);
        Assert.Equal(AccessReason.CanceledSubscription, entitlement.AccessReason);
        Assert.Equal(Now.AddDays(3), entitlement.AccessEndsAt);
    }

    [Fact]
    public void BlankProductIds_AreIgnored()
    {
        var subs = new[]
        {
            new Subscription(
                Provider: SubscriptionProvider.Stripe,
                ProductId: "",
                Status: SubscriptionStatus.Active,
                ProviderStatus: null,
                CurrentPeriodEnd: Now.AddDays(10),
                AccessEndsAt: Now.AddDays(10),
                CancelAtPeriodEnd: false
            ),
            new Subscription(
                Provider: SubscriptionProvider.Apple,
                ProductId: "   ",
                Status: SubscriptionStatus.Active,
                ProviderStatus: null,
                CurrentPeriodEnd: Now.AddDays(15),
                AccessEndsAt: Now.AddDays(15),
                CancelAtPeriodEnd: false
            ),
            new Subscription(
                Provider: SubscriptionProvider.Stripe,
                ProductId: "prod_clean",
                Status: SubscriptionStatus.Active,
                ProviderStatus: null,
                CurrentPeriodEnd: Now.AddDays(20),
                AccessEndsAt: Now.AddDays(20),
                CancelAtPeriodEnd: false
            )
        };

        var result = AccessStateDeriver.DeriveEntitlements(subs, Now);

        var entitlement = Assert.Single(result);
        Assert.Equal("prod_clean", entitlement.ProductId);
    }

    [Fact]
    public void SameProductAcrossProviders_UsesLongestCurrentAccessWindow()
    {
        var subs = new[]
        {
            new Subscription(
                Provider: SubscriptionProvider.Apple,
                ProductId: "gold_annual",
                Status: SubscriptionStatus.Active,
                ProviderStatus: null,
                CurrentPeriodEnd: Now.AddDays(1),
                AccessEndsAt: Now.AddDays(1),
                CancelAtPeriodEnd: false
            ),
            new Subscription(
                Provider: SubscriptionProvider.Stripe,
                ProductId: "gold_annual",
                Status: SubscriptionStatus.Canceled,
                ProviderStatus: null,
                CurrentPeriodEnd: Now.AddDays(30),
                AccessEndsAt: Now.AddDays(30),
                CancelAtPeriodEnd: true
            ),
            new Subscription(
                Provider: SubscriptionProvider.Google,
                ProductId: "gold_annual",
                Status: SubscriptionStatus.Paused,
                ProviderStatus: null,
                CurrentPeriodEnd: Now.AddDays(60),
                AccessEndsAt: Now.AddDays(60),
                CancelAtPeriodEnd: false
            )
        };

        var result = AccessStateDeriver.DeriveEntitlements(subs, Now);

        var entitlement = Assert.Single(result);
        Assert.Equal("gold_annual", entitlement.ProductId);
        Assert.True(entitlement.HasAccess);
        Assert.Equal(AccessReason.ActiveSubscription, entitlement.AccessReason);
        Assert.Equal(Now.AddDays(30), entitlement.AccessEndsAt);
    }

    [Fact]
    public void ExpiredHigherPriorityRows_DoNotBlockCancelingAccessForSameProduct()
    {
        var subs = new[]
        {
            new Subscription(
                Provider: SubscriptionProvider.Google,
                ProductId: "gold_annual",
                Status: SubscriptionStatus.Active,
                ProviderStatus: null,
                CurrentPeriodEnd: Now.AddDays(-1),
                AccessEndsAt: Now.AddDays(-1),
                CancelAtPeriodEnd: false
            ),
            new Subscription(
                Provider: SubscriptionProvider.Apple,
                ProductId: "gold_annual",
                Status: SubscriptionStatus.Trialing,
                ProviderStatus: null,
                CurrentPeriodEnd: Now.AddDays(-2),
                AccessEndsAt: Now.AddDays(-2),
                CancelAtPeriodEnd: false
            ),
            new Subscription(
                Provider: SubscriptionProvider.Stripe,
                ProductId: "gold_annual",
                Status: SubscriptionStatus.Canceled,
                ProviderStatus: null,
                CurrentPeriodEnd: Now.AddDays(7),
                AccessEndsAt: Now.AddDays(7),
                CancelAtPeriodEnd: true
            )
        };

        var result = AccessStateDeriver.DeriveEntitlements(subs, Now);

        var entitlement = Assert.Single(result);
        Assert.True(entitlement.HasAccess);
        Assert.Equal(AccessReason.CanceledSubscription, entitlement.AccessReason);
        Assert.Equal(Now.AddDays(7), entitlement.AccessEndsAt);
    }
}
