using SubTru.Domain.Subscriptions;
using SubTru.Infrastructure.Providers.Apple;

namespace SubTru.Tests.Infrastructure.Providers.Apple;

public sealed class AppleSubscriptionMapperTests
{
    private static readonly Guid TestProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TestAppAccountToken = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public void TryMapAutoRenewableSubscription_ActiveNotification_ReturnsTrue()
    {
        var notification = CreateNotification();

        var result = AppleSubscriptionMapper.TryMapAutoRenewableSubscription(notification, TestProjectId, SubscriptionEnvironment.Production, out var normalized, out var error);

        Assert.True(result);
        Assert.NotNull(normalized);
        Assert.Null(error);
        Assert.Equal("apple", normalized.Provider);
        Assert.Equal("1000000123456789", normalized.ProviderSubscriptionId);
        Assert.Equal(TestAppAccountToken.ToString("D"), normalized.ExternalUserId);
        Assert.Equal("com.example.premium.monthly", normalized.ProductId);
        Assert.Equal("active", normalized.Status);
        Assert.Equal("1", normalized.ProviderStatus);
        Assert.Equal(normalized.CurrentPeriodEnd, normalized.AccessEndsAt);
        Assert.False(normalized.CancelAtPeriodEnd);
    }

    [Fact]
    public void TryMapAutoRenewableSubscription_AutoRenewDisabled_MapsCanceled()
    {
        var notification = CreateNotification(
            autoRenewStatus: false,
            transactionExpiresInDays: -5,
            renewalDateInDays: 20);

        var result = AppleSubscriptionMapper.TryMapAutoRenewableSubscription(notification, TestProjectId, SubscriptionEnvironment.Production, out var normalized, out _);

        Assert.True(result);
        Assert.NotNull(normalized);
        Assert.Equal("canceled", normalized.Status);
        Assert.True(normalized.CancelAtPeriodEnd);
        Assert.Equal(new DateTimeOffset(2026, 3, 26, 12, 0, 0, TimeSpan.Zero), normalized.CurrentPeriodEnd);
    }

    [Fact]
    public void TryMapAutoRenewableSubscription_FreeTrialOffer_MapsTrialing()
    {
        var notification = CreateNotification(offerDiscountType: "FREE_TRIAL");

        var result = AppleSubscriptionMapper.TryMapAutoRenewableSubscription(notification, TestProjectId, SubscriptionEnvironment.Production, out var normalized, out _);

        Assert.True(result);
        Assert.NotNull(normalized);
        Assert.Equal("trialing", normalized.Status);
        Assert.False(normalized.CancelAtPeriodEnd);
    }

    [Fact]
    public void TryMapAutoRenewableSubscription_FreeTrialWithAutoRenewDisabled_MapsTrialingAndCancelAtPeriodEnd()
    {
        var notification = CreateNotification(autoRenewStatus: false, offerDiscountType: "free_trial");

        var result = AppleSubscriptionMapper.TryMapAutoRenewableSubscription(notification, TestProjectId, SubscriptionEnvironment.Production, out var normalized, out _);

        Assert.True(result);
        Assert.NotNull(normalized);
        Assert.Equal("trialing", normalized.Status);
        Assert.True(normalized.CancelAtPeriodEnd);
    }

    [Fact]
    public void TryMapAutoRenewableSubscription_GracePeriodFailure_MapsBillingGrace()
    {
        var notification = CreateNotification(
            notificationType: "DID_FAIL_TO_RENEW",
            subtype: "GRACE_PERIOD",
            status: null,
            gracePeriodExpiresInDays: 3);

        var result = AppleSubscriptionMapper.TryMapAutoRenewableSubscription(notification, TestProjectId, SubscriptionEnvironment.Production, out var normalized, out _);

        Assert.True(result);
        Assert.NotNull(normalized);
        Assert.Equal("billing_grace", normalized.Status);
        Assert.Null(normalized.ProviderStatus);
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 12, 0, 0, TimeSpan.Zero), normalized.AccessEndsAt);
    }

    [Fact]
    public void TryMapAutoRenewableSubscription_RefundWithRevokedStatus_MapsExpired()
    {
        var notification = CreateNotification(
            notificationType: "REFUND",
            status: 5);

        var result = AppleSubscriptionMapper.TryMapAutoRenewableSubscription(notification, TestProjectId, SubscriptionEnvironment.Production, out var normalized, out _);

        Assert.True(result);
        Assert.NotNull(normalized);
        Assert.Equal("expired", normalized.Status);
    }

    [Fact]
    public void TryMapAutoRenewableSubscription_OldRefundWithActiveStatus_UsesCurrentRenewalSnapshot()
    {
        var currentAppAccountToken = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var notification = CreateNotification(
            notificationType: "REFUND",
            status: 1,
            transactionProductId: "com.example.basic.monthly",
            renewalProductId: "com.example.premium.monthly",
            transactionAppAccountToken: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            renewalAppAccountToken: currentAppAccountToken.ToString("D"),
            transactionExpiresInDays: -5,
            renewalDateInDays: 20);

        var result = AppleSubscriptionMapper.TryMapAutoRenewableSubscription(notification, TestProjectId, SubscriptionEnvironment.Production, out var normalized, out _);

        Assert.True(result);
        Assert.NotNull(normalized);
        Assert.Equal("active", normalized.Status);
        Assert.Equal("com.example.premium.monthly", normalized.ProductId);
        Assert.Equal(currentAppAccountToken.ToString("D"), normalized.ExternalUserId);
        Assert.Equal(new DateTimeOffset(2026, 3, 26, 12, 0, 0, TimeSpan.Zero), normalized.CurrentPeriodEnd);
    }

    [Theory]
    [InlineData("REFUND")]
    [InlineData("REFUND_REVERSED")]
    public void TryMapAutoRenewableSubscription_HistoricalTransactionWithoutRenewalInfo_IsRejected(
        string notificationType)
    {
        var notification = CreateNotification(
            notificationType: notificationType,
            status: 1,
            omitRenewalInfo: true);

        var result = AppleSubscriptionMapper.TryMapAutoRenewableSubscription(
            notification,
            TestProjectId,
            SubscriptionEnvironment.Production,
            out var normalized,
            out var error);

        Assert.False(result);
        Assert.Null(normalized);
        Assert.Equal("missing_refund_renewal_info", error);
    }

    [Fact]
    public void TryMapAutoRenewableSubscription_RefundWithoutCurrentStatus_IsRejected()
    {
        var notification = CreateNotification(
            notificationType: "REFUND",
            status: null);

        var result = AppleSubscriptionMapper.TryMapAutoRenewableSubscription(
            notification,
            TestProjectId,
            SubscriptionEnvironment.Production,
            out var normalized,
            out var error);

        Assert.False(result);
        Assert.Null(normalized);
        Assert.Equal("missing_or_invalid_refund_subscription_status", error);
    }

    [Fact]
    public void TryMapAutoRenewableSubscription_RefundWithMismatchedOriginalTransactionIds_IsRejected()
    {
        var notification = CreateNotification(
            notificationType: "REFUND",
            status: 1,
            renewalOriginalTransactionId: "different-original-transaction-id");

        var result = AppleSubscriptionMapper.TryMapAutoRenewableSubscription(
            notification,
            TestProjectId,
            SubscriptionEnvironment.Production,
            out var normalized,
            out var error);

        Assert.False(result);
        Assert.Null(normalized);
        Assert.Equal("refund_original_transaction_id_mismatch", error);
    }

    [Fact]
    public void TryMapAutoRenewableSubscription_RefundWithoutCurrentRenewalProduct_DoesNotUseFutureProduct()
    {
        var notification = CreateNotification(
            notificationType: "REFUND",
            status: 1,
            renewalProductId: "",
            autoRenewProductId: "com.example.premium.future");

        var result = AppleSubscriptionMapper.TryMapAutoRenewableSubscription(
            notification,
            TestProjectId,
            SubscriptionEnvironment.Production,
            out var normalized,
            out var error);

        Assert.False(result);
        Assert.Null(normalized);
        Assert.Equal("missing_product_id", error);
    }

    [Fact]
    public void TryMapAutoRenewableSubscription_GracePeriod_UsesGraceExpirationForAccessEnd()
    {
        var notification = CreateNotification(
            notificationType: "DID_FAIL_TO_RENEW",
            subtype: "GRACE_PERIOD",
            status: 4,
            transactionExpiresInDays: -5,
            renewalDateInDays: -5,
            gracePeriodExpiresInDays: 3);

        var result = AppleSubscriptionMapper.TryMapAutoRenewableSubscription(notification, TestProjectId, SubscriptionEnvironment.Production, out var normalized, out _);

        Assert.True(result);
        Assert.NotNull(normalized);
        Assert.Equal("billing_grace", normalized.Status);
        Assert.Equal("4", normalized.ProviderStatus);
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero), normalized.CurrentPeriodEnd);
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 12, 0, 0, TimeSpan.Zero), normalized.AccessEndsAt);
    }

    [Fact]
    public void TryMapAutoRenewableSubscription_GracePeriodWithoutExpiration_LeavesAccessEndUnknown()
    {
        var notification = CreateNotification(
            notificationType: "DID_FAIL_TO_RENEW",
            subtype: "GRACE_PERIOD",
            status: 4,
            transactionExpiresInDays: -5,
            renewalDateInDays: -2,
            gracePeriodExpiresInDays: null);

        var result = AppleSubscriptionMapper.TryMapAutoRenewableSubscription(notification, TestProjectId, SubscriptionEnvironment.Production, out var normalized, out _);

        Assert.True(result);
        Assert.NotNull(normalized);
        Assert.Equal("billing_grace", normalized.Status);
        Assert.Equal("4", normalized.ProviderStatus);
        Assert.Equal(new DateTimeOffset(2026, 3, 4, 12, 0, 0, TimeSpan.Zero), normalized.CurrentPeriodEnd);
        Assert.Null(normalized.AccessEndsAt);
    }

    [Fact]
    public void TryMapAutoRenewableSubscription_BillingRetryStatus_MapsBillingSuspended()
    {
        var notification = CreateNotification(
            notificationType: "DID_FAIL_TO_RENEW",
            status: 3);

        var result = AppleSubscriptionMapper.TryMapAutoRenewableSubscription(notification, TestProjectId, SubscriptionEnvironment.Production, out var normalized, out _);

        Assert.True(result);
        Assert.NotNull(normalized);
        Assert.Equal("billing_suspended", normalized.Status);
        Assert.Equal("3", normalized.ProviderStatus);
        Assert.False(normalized.CancelAtPeriodEnd);
    }

    [Fact]
    public void TryMapAutoRenewableSubscription_DidFailToRenewWithoutGrace_MapsBillingSuspended()
    {
        var notification = CreateNotification(
            notificationType: "DID_FAIL_TO_RENEW",
            subtype: null,
            status: null);

        var result = AppleSubscriptionMapper.TryMapAutoRenewableSubscription(notification, TestProjectId, SubscriptionEnvironment.Production, out var normalized, out _);

        Assert.True(result);
        Assert.NotNull(normalized);
        Assert.Equal("billing_suspended", normalized.Status);
        Assert.Null(normalized.ProviderStatus);
        Assert.False(normalized.CancelAtPeriodEnd);
    }

    [Fact]
    public void TryMapAutoRenewableSubscription_GracePeriodExpired_MapsBillingSuspended()
    {
        var notification = CreateNotification(
            notificationType: "GRACE_PERIOD_EXPIRED",
            status: null,
            transactionExpiresInDays: -5,
            renewalDateInDays: -5);

        var result = AppleSubscriptionMapper.TryMapAutoRenewableSubscription(notification, TestProjectId, SubscriptionEnvironment.Production, out var normalized, out _);

        Assert.True(result);
        Assert.NotNull(normalized);
        Assert.Equal("billing_suspended", normalized.Status);
        Assert.Equal(normalized.CurrentPeriodEnd, normalized.AccessEndsAt);
    }

    [Fact]
    public void TryMapAutoRenewableSubscription_DidRecover_MapsActiveWithFiniteAccessEnd()
    {
        var notification = CreateNotification(
            notificationType: "DID_RECOVER",
            status: 1,
            renewalDateInDays: 30);

        var result = AppleSubscriptionMapper.TryMapAutoRenewableSubscription(notification, TestProjectId, SubscriptionEnvironment.Production, out var normalized, out _);

        Assert.True(result);
        Assert.NotNull(normalized);
        Assert.Equal("active", normalized.Status);
        Assert.Equal("1", normalized.ProviderStatus);
        Assert.Equal(normalized.CurrentPeriodEnd, normalized.AccessEndsAt);
    }

    [Fact]
    public void TryMapAutoRenewableSubscription_InvalidAppAccountToken_ReturnsFalse()
    {
        var notification = CreateNotification(appAccountToken: "not-a-guid");

        var result = AppleSubscriptionMapper.TryMapAutoRenewableSubscription(notification, TestProjectId, SubscriptionEnvironment.Production, out var normalized, out var error);

        Assert.False(result);
        Assert.Null(normalized);
        Assert.Equal("missing_or_invalid_app_account_token", error);
    }

    [Fact]
    public void TryMapAutoRenewableSubscription_DowngradePref_UsesCurrentTransactionProduct()
    {
        var notification = CreateNotification(
            notificationType: "DID_CHANGE_RENEWAL_PREF",
            subtype: "DOWNGRADE",
            transactionProductId: "com.example.premium.current",
            renewalProductId: "com.example.premium.future");

        var result = AppleSubscriptionMapper.TryMapAutoRenewableSubscription(notification, TestProjectId, SubscriptionEnvironment.Production, out var normalized, out _);

        Assert.True(result);
        Assert.NotNull(normalized);
        Assert.Equal("com.example.premium.current", normalized.ProductId);
    }

    [Fact]
    public void TryMapAutoRenewableSubscription_MissingCurrentPeriodEnd_ReturnsFalse()
    {
        var notification = CreateNotification(
            omitCurrentPeriodEnd: true);

        var result = AppleSubscriptionMapper.TryMapAutoRenewableSubscription(notification, TestProjectId, SubscriptionEnvironment.Production, out var normalized, out var error);

        Assert.False(result);
        Assert.Null(normalized);
        Assert.Equal("missing_current_period_end", error);
    }

    [Theory]
    [InlineData("SUBSCRIBED", AppleNotificationHandlingKind.Supported)]
    [InlineData("DID_RECOVER", AppleNotificationHandlingKind.Supported)]
    [InlineData("TEST", AppleNotificationHandlingKind.Ignored)]
    [InlineData("ONE_TIME_CHARGE", AppleNotificationHandlingKind.Ignored)]
    [InlineData("REVOKE", AppleNotificationHandlingKind.Unsupported)]
    [InlineData("SOMETHING_NEW", AppleNotificationHandlingKind.Unsupported)]
    public void ClassifyNotificationType_ReturnsExpectedHandling(string notificationType, AppleNotificationHandlingKind expectedKind)
    {
        var decision = AppleSubscriptionMapper.ClassifyNotificationType(CreateNotification(notificationType: notificationType));

        Assert.Equal(expectedKind, decision.Kind);
    }

    [Theory]
    [InlineData("REFUND", "Consumable")]
    [InlineData("REFUND", "Non-Consumable")]
    [InlineData("REFUND", "Non-Renewing Subscription")]
    [InlineData("REFUND_REVERSED", "Consumable")]
    [InlineData("REFUND_REVERSED", "Non-Consumable")]
    [InlineData("REFUND_REVERSED", "Non-Renewing Subscription")]
    public void ClassifyNotificationType_NonAutoRenewableRefund_IsIgnored(
        string notificationType,
        string transactionType)
    {
        var notification = CreateNotification(
            notificationType: notificationType,
            transactionType: transactionType);

        var decision = AppleSubscriptionMapper.ClassifyNotificationType(notification);

        Assert.Equal(AppleNotificationHandlingKind.Ignored, decision.Kind);
    }

    [Fact]
    public void ClassifyNotificationType_AutoRenewableRefund_RemainsSupported()
    {
        var notification = CreateNotification(
            notificationType: "REFUND",
            transactionType: "Auto-Renewable Subscription");

        var decision = AppleSubscriptionMapper.ClassifyNotificationType(notification);

        Assert.Equal(AppleNotificationHandlingKind.Supported, decision.Kind);
    }

    [Theory]
    [InlineData(null, "missing_apple_purchase_type")]
    [InlineData("", "missing_apple_purchase_type")]
    [InlineData("Future Purchase Type", "unsupported_apple_purchase_type")]
    public void ClassifyNotificationType_RefundWithMissingOrUnknownPurchaseType_IsUnsupported(
        string? transactionType,
        string expectedErrorCode)
    {
        var notification = CreateNotification(
            notificationType: "REFUND",
            transactionType: transactionType);

        var decision = AppleSubscriptionMapper.ClassifyNotificationType(notification);

        Assert.Equal(AppleNotificationHandlingKind.Unsupported, decision.Kind);
        Assert.Equal(expectedErrorCode, decision.ErrorCode);
    }

    private static AppleTrustedNotification CreateNotification(
        string notificationType = "DID_RENEW",
        string? subtype = null,
        bool? autoRenewStatus = true,
        string? appAccountToken = null,
        int? status = 1,
        string transactionProductId = "com.example.premium.monthly",
        string renewalProductId = "com.example.premium.monthly",
        string? autoRenewProductId = "com.example.premium.monthly",
        string? transactionAppAccountToken = null,
        string? renewalAppAccountToken = null,
        string transactionOriginalTransactionId = "1000000123456789",
        string renewalOriginalTransactionId = "1000000123456789",
        string? transactionType = "Auto-Renewable Subscription",
        string? offerDiscountType = null,
        bool omitCurrentPeriodEnd = false,
        int transactionExpiresInDays = 30,
        int renewalDateInDays = 30,
        int? gracePeriodExpiresInDays = null,
        bool omitRenewalInfo = false)
    {
        var now = new DateTimeOffset(2026, 3, 6, 12, 0, 0, TimeSpan.Zero);

        return new AppleTrustedNotification(
            NotificationType: notificationType,
            Subtype: subtype,
            NotificationUuid: "123e4567-e89b-12d3-a456-426614174000",
            SignedDate: now,
            Data: new AppleTrustedNotificationData(
                BundleId: "com.example.app",
                AppAppleId: 1234567890,
                Environment: "production",
                Status: status,
                TransactionInfo: new AppleTrustedTransactionInfo(
                    TransactionId: "2000000123456789",
                    OriginalTransactionId: transactionOriginalTransactionId,
                    ProductId: transactionProductId,
                    AppAccountToken: transactionAppAccountToken
                        ?? appAccountToken
                        ?? TestAppAccountToken.ToString("D"),
                    BundleId: "com.example.app",
                    Environment: "production",
                    Type: transactionType,
                    ExpiresDate: omitCurrentPeriodEnd ? null : now.AddDays(transactionExpiresInDays),
                    RevocationDate: null,
                    SignedDate: now,
                    OfferDiscountType: offerDiscountType),
                RenewalInfo: omitRenewalInfo ? null : new AppleTrustedRenewalInfo(
                    OriginalTransactionId: renewalOriginalTransactionId,
                    ProductId: renewalProductId,
                    AutoRenewProductId: autoRenewProductId,
                    AppAccountToken: renewalAppAccountToken
                        ?? appAccountToken
                        ?? TestAppAccountToken.ToString("D"),
                    Environment: "production",
                    AutoRenewStatus: autoRenewStatus,
                    GracePeriodExpiresDate: omitCurrentPeriodEnd || !gracePeriodExpiresInDays.HasValue
                        ? null
                        : now.AddDays(gracePeriodExpiresInDays.Value),
                    RenewalDate: omitCurrentPeriodEnd ? null : now.AddDays(renewalDateInDays),
                    IsInBillingRetryPeriod: false,
                    ExpirationIntent: null,
                    SignedDate: now))
        );
    }
}
