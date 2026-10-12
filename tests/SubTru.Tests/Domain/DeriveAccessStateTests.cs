using SubTru.Domain.Access;
using SubTru.Domain.Subscriptions;
using Xunit;

namespace SubTru.Tests.Domain;

public class DeriveAccessStateTests
{
    private static readonly DateTimeOffset Now =
        new DateTimeOffset(2026, 01, 01, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NoSubscriptions_ReturnsNoAccess()
    {
        var result = AccessStateDeriver.DeriveAccessState(
            Array.Empty<Subscription>(),
            Now
        );

        Assert.False(result.HasAccess);
        Assert.Null(result.AccessReason);
        Assert.Null(result.AccessEndsAt);
    }

    [Fact]
    public void ActiveSubscriptions_UseFurthestPeriodEnd()
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
                CurrentPeriodEnd: Now.AddDays(40),
                AccessEndsAt: Now.AddDays(40),
                CancelAtPeriodEnd: false
            )
        };

        var result = AccessStateDeriver.DeriveAccessState(subs, Now);

        Assert.True(result.HasAccess);
        Assert.Equal(AccessReason.ActiveSubscription, result.AccessReason);
        Assert.Equal(Now.AddDays(40), result.AccessEndsAt);
    }

    [Fact]
    public void TrialingSubscription_GrantsTrialAccess()
    {
        var subs = new[]
        {
            new Subscription(
                Provider: SubscriptionProvider.Stripe,
                ProductId: "prod_trial",
                Status: SubscriptionStatus.Trialing,
                ProviderStatus: null,
                CurrentPeriodEnd: Now.AddDays(7),
                AccessEndsAt: Now.AddDays(7),
                CancelAtPeriodEnd: false
            )
        };

        var result = AccessStateDeriver.DeriveAccessState(subs, Now);

        Assert.True(result.HasAccess);
        Assert.Equal(AccessReason.TrialingSubscription, result.AccessReason);
    }

    [Fact]
    public void CanceledButNotExpired_GrantsCancelingAccess()
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

        var result = AccessStateDeriver.DeriveAccessState(subs, Now);

        Assert.True(result.HasAccess);
        Assert.Equal(AccessReason.CanceledSubscription, result.AccessReason);
    }

    [Fact]
    public void ExpiredSubscriptions_DenyAccess()
    {
        var subs = new[]
        {
            new Subscription(
                Provider: SubscriptionProvider.Stripe,
                ProductId: "prod_expired",
                Status: SubscriptionStatus.Expired,
                ProviderStatus: null,
                CurrentPeriodEnd: Now.AddDays(-1),
                AccessEndsAt: Now.AddDays(-1),
                CancelAtPeriodEnd: false
            )
        };

        var result = AccessStateDeriver.DeriveAccessState(subs, Now);

        Assert.False(result.HasAccess);
        Assert.Equal(AccessReason.ExpiredSubscription, result.AccessReason);
    }

    [Fact]
    public void PausedSubscription_DeniesAccessWithPausedReason()
    {
        var subs = new[]
        {
            new Subscription(
                Provider: SubscriptionProvider.Stripe,
                ProductId: "prod_paused",
                Status: SubscriptionStatus.Paused,
                ProviderStatus: null,
                CurrentPeriodEnd: Now.AddDays(30),
                AccessEndsAt: Now.AddDays(30),
                CancelAtPeriodEnd: false
            )
        };

        var result = AccessStateDeriver.DeriveAccessState(subs, Now);

        Assert.False(result.HasAccess);
        Assert.Equal(AccessReason.PausedSubscription, result.AccessReason);
    }

    [Fact]
    public void ExpiredActive_DoesNotOverrideFutureGraceAccess()
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
                Provider: SubscriptionProvider.Stripe,
                ProductId: "gold_annual",
                Status: SubscriptionStatus.Canceled,
                ProviderStatus: null,
                CurrentPeriodEnd: Now.AddDays(7),
                AccessEndsAt: Now.AddDays(7),
                CancelAtPeriodEnd: true
            )
        };

        var result = AccessStateDeriver.DeriveAccessState(subs, Now);

        Assert.True(result.HasAccess);
        Assert.Equal(AccessReason.CanceledSubscription, result.AccessReason);
        Assert.Equal(Now.AddDays(7), result.AccessEndsAt);
    }

    [Fact]
    public void ExpiredTrialing_DoesNotOverrideFutureGraceAccess()
    {
        var subs = new[]
        {
            new Subscription(
                Provider: SubscriptionProvider.Apple,
                ProductId: "gold_annual",
                Status: SubscriptionStatus.Trialing,
                ProviderStatus: null,
                CurrentPeriodEnd: Now.AddDays(-1),
                AccessEndsAt: Now.AddDays(-1),
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

        var result = AccessStateDeriver.DeriveAccessState(subs, Now);

        Assert.True(result.HasAccess);
        Assert.Equal(AccessReason.CanceledSubscription, result.AccessReason);
        Assert.Equal(Now.AddDays(7), result.AccessEndsAt);
    }

    [Fact]
    public void MixedAccessGrantingRows_UseStrongestReasonAndLongestAccessWindow()
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
            )
        };

        var result = AccessStateDeriver.DeriveAccessState(subs, Now);

        Assert.True(result.HasAccess);
        Assert.Equal(AccessReason.ActiveSubscription, result.AccessReason);
        Assert.Equal(Now.AddDays(30), result.AccessEndsAt);
    }

    [Fact]
    public void ActiveCancelAtPeriodEnd_UsesCancelingReason()
    {
        var subscription = new Subscription(
            Provider: SubscriptionProvider.Stripe,
            ProductId: "prod_canceling",
            Status: SubscriptionStatus.Active,
            ProviderStatus: "active",
            CurrentPeriodEnd: Now.AddDays(10),
            AccessEndsAt: Now.AddDays(10),
            CancelAtPeriodEnd: true);

        var result = AccessStateDeriver.DeriveAccessState([subscription], Now);

        Assert.True(result.HasAccess);
        Assert.Equal(AccessReason.CanceledSubscription, result.AccessReason);
        Assert.Equal(Now.AddDays(10), result.AccessEndsAt);
    }

    [Fact]
    public void BillingGraceWithKnownEnd_GrantsAccessUntilAccessEnd()
    {
        var subscription = new Subscription(
            Provider: SubscriptionProvider.Apple,
            ProductId: "prod_grace",
            Status: SubscriptionStatus.BillingGrace,
            ProviderStatus: "4",
            CurrentPeriodEnd: Now.AddDays(-1),
            AccessEndsAt: Now.AddDays(3),
            CancelAtPeriodEnd: false);

        var result = AccessStateDeriver.DeriveAccessState([subscription], Now);

        Assert.True(result.HasAccess);
        Assert.Equal(AccessReason.PaymentFailedInGracePeriod, result.AccessReason);
        Assert.Equal(Now.AddDays(3), result.AccessEndsAt);
    }

    [Fact]
    public void BillingGraceWithUnknownEnd_GrantsEventDrivenAccess()
    {
        var subscription = new Subscription(
            Provider: SubscriptionProvider.Apple,
            ProductId: "prod_grace",
            Status: SubscriptionStatus.BillingGrace,
            ProviderStatus: "4",
            CurrentPeriodEnd: Now.AddDays(-1),
            AccessEndsAt: null,
            CancelAtPeriodEnd: false);

        var result = AccessStateDeriver.DeriveAccessState([subscription], Now);

        Assert.True(result.HasAccess);
        Assert.Equal(AccessReason.PaymentFailedInGracePeriod, result.AccessReason);
        Assert.Null(result.AccessEndsAt);
    }

    [Fact]
    public void StripePastDueWithUnknownEnd_GrantsBillingGraceAccess()
    {
        var subscription = new Subscription(
            Provider: SubscriptionProvider.Stripe,
            ProductId: "prod_grace",
            Status: SubscriptionStatus.BillingGrace,
            ProviderStatus: "past_due",
            CurrentPeriodEnd: Now.AddDays(-1),
            AccessEndsAt: null,
            CancelAtPeriodEnd: false);

        var result = AccessStateDeriver.DeriveAccessState([subscription], Now);

        Assert.True(result.HasAccess);
        Assert.Equal(AccessReason.PaymentFailedInGracePeriod, result.AccessReason);
        Assert.Null(result.AccessEndsAt);
    }

    [Theory]
    [InlineData(SubscriptionProvider.Apple, "3")]
    [InlineData(SubscriptionProvider.Google, "SUBSCRIPTION_STATE_ON_HOLD")]
    public void StoreBillingSuspended_DeniesAccess(
        SubscriptionProvider provider,
        string providerStatus)
    {
        var subscription = new Subscription(
            Provider: provider,
            ProductId: "prod_retry",
            Status: SubscriptionStatus.BillingSuspended,
            ProviderStatus: providerStatus,
            CurrentPeriodEnd: Now.AddDays(5),
            AccessEndsAt: Now.AddDays(5),
            CancelAtPeriodEnd: false);

        var result = AccessStateDeriver.DeriveAccessState([subscription], Now);

        Assert.False(result.HasAccess);
        Assert.Equal(AccessReason.PaymentFailedPastGracePeriod, result.AccessReason);
        Assert.Equal(Now.AddDays(5), result.AccessEndsAt);
    }

    [Fact]
    public void UnknownEnd_DoesNotGrantAccessForNormalActiveState()
    {
        var subscription = new Subscription(
            Provider: SubscriptionProvider.Stripe,
            ProductId: "prod_active",
            Status: SubscriptionStatus.Active,
            ProviderStatus: "active",
            CurrentPeriodEnd: Now.AddDays(5),
            AccessEndsAt: null,
            CancelAtPeriodEnd: false);

        var result = AccessStateDeriver.DeriveAccessState([subscription], Now);

        Assert.False(result.HasAccess);
        Assert.Equal(AccessReason.ExpiredSubscription, result.AccessReason);
        Assert.Null(result.AccessEndsAt);
    }

    [Fact]
    public void ActiveAndUnknownGrace_UseActiveReasonAndUnknownAccessEnd()
    {
        var subscriptions = new[]
        {
            new Subscription(
                Provider: SubscriptionProvider.Stripe,
                ProductId: "prod_multi",
                Status: SubscriptionStatus.Active,
                ProviderStatus: "active",
                CurrentPeriodEnd: Now.AddDays(5),
                AccessEndsAt: Now.AddDays(5),
                CancelAtPeriodEnd: false),
            new Subscription(
                Provider: SubscriptionProvider.Apple,
                ProductId: "prod_multi",
                Status: SubscriptionStatus.BillingGrace,
                ProviderStatus: "4",
                CurrentPeriodEnd: Now.AddDays(-1),
                AccessEndsAt: null,
                CancelAtPeriodEnd: false)
        };

        var result = AccessStateDeriver.DeriveAccessState(subscriptions, Now);

        Assert.True(result.HasAccess);
        Assert.Equal(AccessReason.ActiveSubscription, result.AccessReason);
        Assert.Null(result.AccessEndsAt);
    }
}
