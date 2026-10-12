using SubTru.Domain.Subscriptions;

namespace SubTru.Infrastructure.Providers.Apple;

public static class AppleSubscriptionMapper
{
    private const string FreeTrialOfferDiscountType = "FREE_TRIAL";
    private const string AutoRenewablePurchaseType = "Auto-Renewable Subscription";

    // Apple provides subscription state as numeric values in signed notification data.
    private enum AppleSubscriptionState
    {
        Active = 1,
        Expired = 2,
        BillingRetry = 3,
        GracePeriod = 4,
        Revoked = 5
    }

    private static readonly HashSet<string> SupportedNotificationTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUBSCRIBED",
        "DID_RECOVER",
        "DID_RENEW",
        "DID_CHANGE_RENEWAL_PREF",
        "DID_CHANGE_RENEWAL_STATUS",
        "DID_FAIL_TO_RENEW",
        "EXPIRED",
        "GRACE_PERIOD_EXPIRED",
        "OFFER_REDEEMED",
        "PRICE_INCREASE",
        "REFUND",
        "REFUND_REVERSED",
        "RENEWAL_EXTENDED",
    };

    private static readonly HashSet<string> IgnoredNotificationTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "CONSUMPTION_REQUEST",
        "EXTERNAL_PURCHASE_TOKEN",
        "METADATA_UPDATE",
        "MIGRATION",
        "ONE_TIME_CHARGE",
        "PRICE_CHANGE",
        "REFUND_DECLINED",
        "RENEWAL_EXTENSION",
        "RESCIND_CONSENT",
        "TEST"
    };

    private static readonly HashSet<string> NonAutoRenewablePurchaseTypes = new(StringComparer.Ordinal)
    {
        "Consumable",
        "Non-Consumable",
        "Non-Renewing Subscription"
    };

    public static bool TryMapAutoRenewableSubscription(
        AppleTrustedNotification notification,
        Guid projectId,
        string environment,
        out NormalizedSubscription? normalized,
        out string? error)
    {
        normalized = null;
        error = null;

        var data = notification.Data;
        if (data is null)
        {
            error = "missing_notification_data";
            return false;
        }

        var handling = ClassifyNotificationType(notification);
        if (handling.Kind != AppleNotificationHandlingKind.Supported)
        {
            error = handling.ErrorCode;
            return false;
        }

        var transaction = data.TransactionInfo;
        if (transaction is null)
        {
            error = "missing_signed_transaction_info";
            return false;
        }

        if (string.IsNullOrWhiteSpace(transaction.OriginalTransactionId))
        {
            error = "missing_original_transaction_id";
            return false;
        }

        var renewal = data.RenewalInfo;
        var isRefundNotification = IsRefundNotification(notification);
        string? externalUserId;
        string? productId;
        string providerSubscriptionId;
        string status;
        DateTimeOffset? currentPeriodEnd;

        if (isRefundNotification)
        {
            if (!data.Status.HasValue
                || !Enum.IsDefined(typeof(AppleSubscriptionState), data.Status.Value))
            {
                error = "missing_or_invalid_refund_subscription_status";
                return false;
            }

            if (renewal is null)
            {
                error = "missing_refund_renewal_info";
                return false;
            }

            if (string.IsNullOrWhiteSpace(renewal.OriginalTransactionId))
            {
                error = "missing_refund_original_transaction_id";
                return false;
            }

            if (!string.Equals(
                    transaction.OriginalTransactionId,
                    renewal.OriginalTransactionId,
                    StringComparison.Ordinal))
            {
                error = "refund_original_transaction_id_mismatch";
                return false;
            }

            // We use the renewal (current subscription state) data instead of the refund transaction data
            // since we don't want the potentially old refund transaction data to overwrite an existing subscription's data.
            externalUserId = renewal.AppAccountToken;
            productId = renewal.ProductId;
            providerSubscriptionId = renewal.OriginalTransactionId;
            status = DetermineStatus(
                notification,
                data.Status,
                transaction,
                renewal,
                isRefundNotification: true);
            currentPeriodEnd = renewal.RenewalDate;
        }
        else
        {
            externalUserId = transaction.AppAccountToken ?? renewal?.AppAccountToken;
            productId = transaction.ProductId;
            providerSubscriptionId = transaction.OriginalTransactionId;
            status = DetermineStatus(
                notification,
                data.Status,
                transaction,
                renewal,
                isRefundNotification: false);
            currentPeriodEnd = renewal?.RenewalDate ?? transaction.ExpiresDate;
        }

        if (!Guid.TryParse(externalUserId, out var appAccountToken))
        {
            error = "missing_or_invalid_app_account_token";
            return false;
        }

        if (string.IsNullOrWhiteSpace(productId))
        {
            error = "missing_product_id";
            return false;
        }

        if (!currentPeriodEnd.HasValue)
        {
            error = "missing_current_period_end";
            return false;
        }

        var accessEndsAt = IsInGracePeriod(notification, data.Status)
            ? renewal?.GracePeriodExpiresDate
            : currentPeriodEnd;
        var cancelAtPeriodEnd = renewal?.AutoRenewStatus == false;

        normalized = new NormalizedSubscription(
            ProjectId: projectId,
            Environment: environment,
            ExternalUserId: appAccountToken.ToString("D"),
            Provider: "apple",
            ProviderSubscriptionId: providerSubscriptionId,
            ProductId: productId,
            PriceId: null,
            Status: status,
            ProviderStatus: data.Status?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            CurrentPeriodEnd: currentPeriodEnd.Value,
            AccessEndsAt: accessEndsAt,
            CancelAtPeriodEnd: cancelAtPeriodEnd
        );

        return true;
    }

    public static AppleNotificationHandlingDecision ClassifyNotificationType(AppleTrustedNotification notification)
    {
        if (IgnoredNotificationTypes.Contains(notification.NotificationType))
        {
            return new AppleNotificationHandlingDecision(AppleNotificationHandlingKind.Ignored, null);
        }

        if (IsRefundNotification(notification))
        {
            var transactionType = notification.Data?.TransactionInfo?.Type;
            if (string.IsNullOrWhiteSpace(transactionType))
            {
                return new AppleNotificationHandlingDecision(
                    AppleNotificationHandlingKind.Unsupported,
                    "missing_apple_purchase_type");
            }

            if (NonAutoRenewablePurchaseTypes.Contains(transactionType))
            {
                return new AppleNotificationHandlingDecision(AppleNotificationHandlingKind.Ignored, null);
            }

            if (!string.Equals(transactionType, AutoRenewablePurchaseType, StringComparison.Ordinal))
            {
                return new AppleNotificationHandlingDecision(
                    AppleNotificationHandlingKind.Unsupported,
                    "unsupported_apple_purchase_type");
            }
        }

        if (SupportedNotificationTypes.Contains(notification.NotificationType))
        {
            return new AppleNotificationHandlingDecision(AppleNotificationHandlingKind.Supported, null);
        }

        return new AppleNotificationHandlingDecision(AppleNotificationHandlingKind.Unsupported, "unsupported_apple_notification_type");
    }

    private static bool IsInGracePeriod(
        AppleTrustedNotification notification,
        int? subscriptionState)
    {
        if (subscriptionState.HasValue
            && Enum.IsDefined(typeof(AppleSubscriptionState), subscriptionState.Value))
        {
            return subscriptionState.Value == (int)AppleSubscriptionState.GracePeriod;
        }

        return notification.NotificationType == "DID_FAIL_TO_RENEW"
            && string.Equals(notification.Subtype, "GRACE_PERIOD", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRefundNotification(AppleTrustedNotification notification)
        => string.Equals(notification.NotificationType, "REFUND", StringComparison.OrdinalIgnoreCase)
            || string.Equals(notification.NotificationType, "REFUND_REVERSED", StringComparison.OrdinalIgnoreCase);

    private static string DetermineStatus(
        AppleTrustedNotification notification,
        int? subscriptionState,
        AppleTrustedTransactionInfo transaction,
        AppleTrustedRenewalInfo? renewal,
        bool isRefundNotification)
    {
        if (subscriptionState.HasValue
            && Enum.IsDefined(typeof(AppleSubscriptionState), subscriptionState.Value))
        {
            return (AppleSubscriptionState)subscriptionState.Value switch
            {
                AppleSubscriptionState.Active => isRefundNotification
                    ? DetermineStatusForRefundNotification(renewal)
                    : DetermineActiveStatus(transaction, renewal),
                AppleSubscriptionState.BillingRetry => "billing_suspended",
                AppleSubscriptionState.GracePeriod => "billing_grace",
                AppleSubscriptionState.Expired => "expired",
                AppleSubscriptionState.Revoked => "expired",
                _ => DetermineStatusFromNotification(notification, transaction, renewal)
            };
        }

        return DetermineStatusFromNotification(notification, transaction, renewal);
    }

    private static string DetermineStatusFromNotification(
        AppleTrustedNotification notification,
        AppleTrustedTransactionInfo transaction,
        AppleTrustedRenewalInfo? renewal)
    {
        if (transaction.RevocationDate.HasValue)
        {
            return "expired";
        }

        if (notification.NotificationType is "REFUND" or "REFUND_REVERSED")
        {
            return notification.NotificationType == "REFUND_REVERSED" ? "active" : "expired";
        }

        if (notification.NotificationType == "EXPIRED")
        {
            return "expired";
        }

        if (notification.NotificationType == "GRACE_PERIOD_EXPIRED")
        {
            return "billing_suspended";
        }

        if (notification.NotificationType == "DID_FAIL_TO_RENEW")
        {
            return string.Equals(notification.Subtype, "GRACE_PERIOD", StringComparison.OrdinalIgnoreCase)
                ? "billing_grace"
                : "billing_suspended";
        }

        return DetermineActiveStatus(transaction, renewal);
    }

    private static string DetermineActiveStatus(
        AppleTrustedTransactionInfo transaction,
        AppleTrustedRenewalInfo? renewal)
    {
        if (IsFreeTrialTransaction(transaction))
        {
            return "trialing";
        }

        return renewal?.AutoRenewStatus == false ? "canceled" : "active";
    }

    private static string DetermineStatusForRefundNotification(AppleTrustedRenewalInfo? renewal)
        => renewal?.AutoRenewStatus == false ? "canceled" : "active";

    private static bool IsFreeTrialTransaction(AppleTrustedTransactionInfo transaction)
        => string.Equals(
            transaction.OfferDiscountType,
            FreeTrialOfferDiscountType,
            StringComparison.OrdinalIgnoreCase);
}

public enum AppleNotificationHandlingKind
{
    Supported,
    Ignored,
    Unsupported
}

public sealed record AppleNotificationHandlingDecision(
    AppleNotificationHandlingKind Kind,
    string? ErrorCode);
