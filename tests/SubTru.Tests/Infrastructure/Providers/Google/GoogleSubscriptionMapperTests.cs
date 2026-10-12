using SubTru.Infrastructure.Providers.Google;

namespace SubTru.Tests.Infrastructure.Providers.Google;

public sealed class GoogleSubscriptionMapperTests
{
    [Fact]
    public void Map_ActiveFreeTrial_ReturnsTrialing()
    {
        var purchase = CreatePurchase(
            subscriptionState: GoogleSubscriptionMapper.SubscriptionStateActive,
            offerPhase: new GooglePlayOfferPhase(
                ProrationPeriod: null,
                FreeTrial: new GooglePlayFreeTrialOfferPhase(),
                IntroductoryPrice: null,
                BasePrice: null));

        var result = GoogleSubscriptionMapper.Map("token-trial", purchase);

        Assert.Equal(GoogleSubscriptionMappingKind.Normalized, result.Kind);
        Assert.NotNull(result.Data);
        Assert.Equal("trialing", result.Data.Status);
        Assert.Equal(GoogleSubscriptionMapper.SubscriptionStateActive, result.Data.ProviderStatus);
    }

    [Fact]
    public void Map_OnHold_ReturnsBillingSuspendedAndPreservesProviderStatus()
    {
        var purchase = CreatePurchase(subscriptionState: GoogleSubscriptionMapper.SubscriptionStateOnHold);

        var result = GoogleSubscriptionMapper.Map("token-on-hold", purchase);

        Assert.Equal(GoogleSubscriptionMappingKind.Normalized, result.Kind);
        Assert.NotNull(result.Data);
        Assert.Equal("billing_suspended", result.Data.Status);
        Assert.Equal(GoogleSubscriptionMapper.SubscriptionStateOnHold, result.Data.ProviderStatus);
        Assert.Equal(result.Data.CurrentPeriodEnd, result.Data.AccessEndsAt);
    }

    [Fact]
    public void Map_GracePeriod_ReturnsBillingGrace()
    {
        var purchase = CreatePurchase(subscriptionState: GoogleSubscriptionMapper.SubscriptionStateInGracePeriod);

        var result = GoogleSubscriptionMapper.Map("token-grace", purchase);

        Assert.Equal(GoogleSubscriptionMappingKind.Normalized, result.Kind);
        Assert.NotNull(result.Data);
        Assert.Equal("billing_grace", result.Data.Status);
        Assert.Equal(GoogleSubscriptionMapper.SubscriptionStateInGracePeriod, result.Data.ProviderStatus);
        Assert.Equal(result.Data.CurrentPeriodEnd, result.Data.AccessEndsAt);
    }

    [Fact]
    public void Map_ProviderPause_ReturnsPaused()
    {
        var purchase = CreatePurchase(subscriptionState: GoogleSubscriptionMapper.SubscriptionStatePaused);

        var result = GoogleSubscriptionMapper.Map("token-paused", purchase);

        Assert.Equal(GoogleSubscriptionMappingKind.Normalized, result.Kind);
        Assert.NotNull(result.Data);
        Assert.Equal("paused", result.Data.Status);
    }

    [Fact]
    public void Map_Pending_ReturnsUnsupported()
    {
        var purchase = CreatePurchase(subscriptionState: GoogleSubscriptionMapper.SubscriptionStatePending);

        var result = GoogleSubscriptionMapper.Map("token-pending", purchase);

        Assert.Equal(GoogleSubscriptionMappingKind.Unsupported, result.Kind);
        Assert.Equal("unsupported_google_pending_subscription_state", result.ErrorCode);
        Assert.Null(result.Data);
    }

    [Fact]
    public void Map_PendingPurchaseCanceled_ReturnsUnsupported()
    {
        var purchase = CreatePurchase(subscriptionState: GoogleSubscriptionMapper.SubscriptionStatePendingPurchaseCanceled);

        var result = GoogleSubscriptionMapper.Map("token-pending-canceled", purchase);

        Assert.Equal(GoogleSubscriptionMappingKind.Unsupported, result.Kind);
        Assert.Equal("google_pending_purchase_canceled", result.ErrorCode);
        Assert.Null(result.Data);
    }

    [Fact]
    public void Map_PrepaidPlan_ReturnsUnsupported()
    {
        var purchase = CreatePurchase(
            subscriptionState: GoogleSubscriptionMapper.SubscriptionStateActive,
            prepaidPlan: new GooglePlayPrepaidPlan(AllowExtendAfterTime: DateTimeOffset.UtcNow.ToString("O")));

        var result = GoogleSubscriptionMapper.Map("token-prepaid", purchase);

        Assert.Equal(GoogleSubscriptionMappingKind.Unsupported, result.Kind);
        Assert.Equal("unsupported_google_prepaid_subscription", result.ErrorCode);
        Assert.Null(result.Data);
    }

    [Fact]
    public void Map_Canceled_ReturnsCanceledAndSetsCancelAtPeriodEnd()
    {
        var purchase = CreatePurchase(subscriptionState: GoogleSubscriptionMapper.SubscriptionStateCanceled);

        var result = GoogleSubscriptionMapper.Map("token-canceled", purchase);

        Assert.Equal(GoogleSubscriptionMappingKind.Normalized, result.Kind);
        Assert.NotNull(result.Data);
        Assert.Equal("canceled", result.Data.Status);
        Assert.True(result.Data.CancelAtPeriodEnd);
    }

    [Fact]
    public void Map_Rfc3339ExpiryWithOffset_ParsesExpectedTime()
    {
        var expiry = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.FromHours(5.5));
        var purchase = CreatePurchase(
            subscriptionState: GoogleSubscriptionMapper.SubscriptionStateActive,
            lineItems:
            [
                CreateLineItem("premium_monthly", expiry) with
                {
                    ExpiryTime = "2030-01-02T03:04:05+05:30"
                }
            ]);

        var result = GoogleSubscriptionMapper.Map("token-rfc3339", purchase);

        Assert.Equal(GoogleSubscriptionMappingKind.Normalized, result.Kind);
        Assert.NotNull(result.Data);
        Assert.Equal(expiry, result.Data.CurrentPeriodEnd);
    }

    [Fact]
    public void Map_DeferredReplacementBeforeRenewal_UsesCurrentEntitlement()
    {
        var currentExpiry = DateTimeOffset.UtcNow.AddDays(30);
        var purchase = CreatePurchase(
            subscriptionState: GoogleSubscriptionMapper.SubscriptionStateActive,
            linkedPurchaseToken: "old-purchase-token",
            lineItems:
            [
                CreateLineItem(
                    productId: "premium_monthly",
                    expiryTime: currentExpiry,
                    autoRenewEnabled: false,
                    deferredReplacementProductId: "basic_monthly"),
                CreateLineItem(
                    productId: "basic_monthly",
                    expiryTime: null,
                    autoRenewEnabled: true)
            ]);

        var result = GoogleSubscriptionMapper.Map("new-purchase-token", purchase);

        Assert.Equal(GoogleSubscriptionMappingKind.Normalized, result.Kind);
        Assert.NotNull(result.Data);
        Assert.Equal("premium_monthly", result.Data.ProductId);
        Assert.Equal(currentExpiry, result.Data.CurrentPeriodEnd);
        Assert.Equal("premium_monthly", result.Data.AcknowledgeSubscriptionId);
    }

    [Fact]
    public void Map_DeferredReplacementAfterRenewal_UsesReplacementEntitlement()
    {
        var replacementExpiry = DateTimeOffset.UtcNow.AddDays(30);
        var purchase = CreatePurchase(
            subscriptionState: GoogleSubscriptionMapper.SubscriptionStateActive,
            linkedPurchaseToken: "old-purchase-token",
            lineItems:
            [
                CreateLineItem(
                    productId: "premium_monthly",
                    expiryTime: DateTimeOffset.UtcNow.AddDays(-1),
                    autoRenewEnabled: false),
                CreateLineItem(
                    productId: "basic_monthly",
                    expiryTime: replacementExpiry,
                    autoRenewEnabled: true)
            ]);

        var result = GoogleSubscriptionMapper.Map("new-purchase-token", purchase);

        Assert.Equal(GoogleSubscriptionMappingKind.Normalized, result.Kind);
        Assert.NotNull(result.Data);
        Assert.Equal("basic_monthly", result.Data.ProductId);
        Assert.Equal(replacementExpiry, result.Data.CurrentPeriodEnd);
    }

    [Fact]
    public void Map_ExpiredDeferredReplacementAfterSubsequentUpgrade_UsesReplacementEntitlement()
    {
        var terminalExpiry = DateTimeOffset.UtcNow.AddDays(-1);
        var purchase = CreatePurchase(
            subscriptionState: GoogleSubscriptionMapper.SubscriptionStateExpired,
            linkedPurchaseToken: "old-purchase-token",
            canceledStateContext: new GooglePlayCanceledStateContext(
                new GooglePlayReplacementCancellation()),
            lineItems:
            [
                CreateLineItem(
                    productId: "basic_monthly",
                    expiryTime: null,
                    autoRenewEnabled: false,
                    replacedProductId: "premium_monthly",
                    replacementMode: "DEFERRED"),
                CreateLineItem(
                    productId: "premium_monthly",
                    expiryTime: terminalExpiry,
                    autoRenewEnabled: false)
            ]);

        var result = GoogleSubscriptionMapper.Map("replaced-purchase-token", purchase);

        Assert.Equal(GoogleSubscriptionMappingKind.Normalized, result.Kind);
        Assert.NotNull(result.Data);
        Assert.Equal("expired", result.Data.Status);
        Assert.Equal("basic_monthly", result.Data.ProductId);
        Assert.Equal(terminalExpiry, result.Data.CurrentPeriodEnd);
    }

    [Fact]
    public void Map_SystemExpiredImmediateReplacement_UsesSingleEntitlement()
    {
        var terminalExpiry = new DateTimeOffset(2025, 6, 18, 9, 30, 0, TimeSpan.Zero);
        var purchase = CreatePurchase(
            subscriptionState: GoogleSubscriptionMapper.SubscriptionStateExpired,
            linkedPurchaseToken: "synthetic-replaced-token",
            canceledStateContext: new GooglePlayCanceledStateContext(
                SystemInitiatedCancellation: new GooglePlaySystemInitiatedCancellation()),
            lineItems:
            [
                CreateLineItem(
                    productId: "top_tier_monthly",
                    expiryTime: terminalExpiry,
                    replacedProductId: "starter_monthly",
                    replacementMode: "WITH_TIME_PRORATION",
                    offerPhase: new GooglePlayOfferPhase(
                        ProrationPeriod: null,
                        FreeTrial: null,
                        IntroductoryPrice: null,
                        BasePrice: new GooglePlayBasePriceOfferPhase()))
            ]);

        var result = GoogleSubscriptionMapper.Map("synthetic-current-token", purchase);

        Assert.Equal(GoogleSubscriptionMappingKind.Normalized, result.Kind);
        Assert.NotNull(result.Data);
        Assert.Equal("expired", result.Data.Status);
        Assert.Equal("top_tier_monthly", result.Data.ProductId);
        Assert.Equal("monthly-base", result.Data.PriceId);
        Assert.Equal(terminalExpiry, result.Data.CurrentPeriodEnd);
        Assert.Equal("synthetic-replaced-token", result.Data.LinkedPurchaseToken);
    }

    [Fact]
    public void Map_ExpiredUnrelatedMultipleLineItems_ReturnsUnsupported()
    {
        var purchase = CreatePurchase(
            subscriptionState: GoogleSubscriptionMapper.SubscriptionStateExpired,
            linkedPurchaseToken: "old-purchase-token",
            canceledStateContext: new GooglePlayCanceledStateContext(
                new GooglePlayReplacementCancellation()),
            lineItems:
            [
                CreateLineItem("first_product", null),
                CreateLineItem("second_product", DateTimeOffset.UtcNow.AddDays(-1))
            ]);

        var result = GoogleSubscriptionMapper.Map("replaced-purchase-token", purchase);

        Assert.Equal(GoogleSubscriptionMappingKind.Unsupported, result.Kind);
        Assert.Equal("unsupported_google_subscription_shape", result.ErrorCode);
        Assert.Null(result.Data);
    }

    [Fact]
    public void Map_ActivePurchaseWithOnlyExpiredLineItems_ReturnsUnsupported()
    {
        var purchase = CreatePurchase(
            subscriptionState: GoogleSubscriptionMapper.SubscriptionStateActive,
            linkedPurchaseToken: "old-purchase-token",
            lineItems:
            [
                CreateLineItem("first_product", DateTimeOffset.UtcNow.AddDays(-2)),
                CreateLineItem("second_product", DateTimeOffset.UtcNow.AddDays(-1))
            ]);

        var result = GoogleSubscriptionMapper.Map("invalid-active-token", purchase);

        Assert.Equal(GoogleSubscriptionMappingKind.Unsupported, result.Kind);
        Assert.Equal("unsupported_google_subscription_shape", result.ErrorCode);
        Assert.Null(result.Data);
    }

    [Fact]
    public void Map_DeferredReplacementWithConflictingReplacementDetails_ReturnsUnsupported()
    {
        var purchase = CreatePurchase(
            subscriptionState: GoogleSubscriptionMapper.SubscriptionStateActive,
            linkedPurchaseToken: "old-purchase-token",
            lineItems:
            [
                CreateLineItem(
                    productId: "premium_monthly",
                    expiryTime: DateTimeOffset.UtcNow.AddDays(30),
                    autoRenewEnabled: false,
                    deferredReplacementProductId: "basic_monthly"),
                CreateLineItem(
                    productId: "basic_monthly",
                    expiryTime: null,
                    replacedProductId: "different_product",
                    replacementMode: "DEFERRED")
            ]);

        var result = GoogleSubscriptionMapper.Map("new-purchase-token", purchase);

        Assert.Equal(GoogleSubscriptionMappingKind.Unsupported, result.Kind);
        Assert.Equal("unsupported_google_subscription_shape", result.ErrorCode);
        Assert.Null(result.Data);
    }

    [Fact]
    public void Map_MultipleCurrentEntitlements_ReturnsUnsupported()
    {
        var purchase = CreatePurchase(
            subscriptionState: GoogleSubscriptionMapper.SubscriptionStateActive,
            linkedPurchaseToken: "old-purchase-token",
            lineItems:
            [
                CreateLineItem("base_subscription", DateTimeOffset.UtcNow.AddDays(30)),
                CreateLineItem("subscription_add_on", DateTimeOffset.UtcNow.AddDays(15))
            ]);

        var result = GoogleSubscriptionMapper.Map("bundle-purchase-token", purchase);

        Assert.Equal(GoogleSubscriptionMappingKind.Unsupported, result.Kind);
        Assert.Equal("unsupported_google_subscription_shape", result.ErrorCode);
        Assert.Null(result.Data);
    }

    private static GooglePlaySubscriptionPurchase CreatePurchase(
        string subscriptionState,
        GooglePlayPrepaidPlan? prepaidPlan = null,
        GooglePlayOfferPhase? offerPhase = null,
        string? linkedPurchaseToken = null,
        GooglePlayCanceledStateContext? canceledStateContext = null,
        IReadOnlyList<GooglePlaySubscriptionLineItem>? lineItems = null)
        => new(
            SubscriptionState: subscriptionState,
            AcknowledgementState: GoogleSubscriptionMapper.AcknowledgementStatePending,
            LinkedPurchaseToken: linkedPurchaseToken,
            LineItems: lineItems ??
            [
                CreateLineItem(
                    productId: "premium_monthly",
                    expiryTime: DateTimeOffset.UtcNow.AddDays(30),
                    prepaidPlan: prepaidPlan,
                    offerPhase: offerPhase)
            ],
            ExternalAccountIdentifiers: null,
            OutOfAppPurchaseContext: null,
            CanceledStateContext: canceledStateContext);

    private static GooglePlaySubscriptionLineItem CreateLineItem(
        string productId,
        DateTimeOffset? expiryTime,
        bool autoRenewEnabled = true,
        GooglePlayPrepaidPlan? prepaidPlan = null,
        GooglePlayOfferPhase? offerPhase = null,
        string? deferredReplacementProductId = null,
        string? replacedProductId = null,
        string? replacementMode = null)
        => new(
            ProductId: productId,
            ExpiryTime: expiryTime?.ToString("O"),
            AutoRenewingPlan: new GooglePlayAutoRenewingPlan(
                AutoRenewEnabled: autoRenewEnabled,
                InstallmentDetails: null),
            PrepaidPlan: prepaidPlan,
            OfferDetails: new GooglePlayOfferDetails(
                BasePlanId: "monthly-base",
                OfferId: null),
            DeferredItemReplacement: deferredReplacementProductId is null
                ? null
                : new GooglePlayDeferredItemReplacement(deferredReplacementProductId),
            ItemReplacement: replacedProductId is null && replacementMode is null
                ? null
                : new GooglePlayItemReplacement(replacedProductId, replacementMode),
            OfferPhase: offerPhase);
}
