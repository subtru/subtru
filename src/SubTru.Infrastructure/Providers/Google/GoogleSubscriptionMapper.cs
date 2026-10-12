using System.Globalization;

namespace SubTru.Infrastructure.Providers.Google;

public static class GoogleSubscriptionMapper
{
    public const string ProviderName = "google";
    public const string SubscriptionStateActive = "SUBSCRIPTION_STATE_ACTIVE";
    public const string SubscriptionStateInGracePeriod = "SUBSCRIPTION_STATE_IN_GRACE_PERIOD";
    public const string SubscriptionStateCanceled = "SUBSCRIPTION_STATE_CANCELED";
    public const string SubscriptionStateOnHold = "SUBSCRIPTION_STATE_ON_HOLD";
    public const string SubscriptionStatePaused = "SUBSCRIPTION_STATE_PAUSED";
    public const string SubscriptionStateExpired = "SUBSCRIPTION_STATE_EXPIRED";
    public const string SubscriptionStatePending = "SUBSCRIPTION_STATE_PENDING";
    public const string SubscriptionStatePendingPurchaseCanceled = "SUBSCRIPTION_STATE_PENDING_PURCHASE_CANCELED";
    public const string AcknowledgementStatePending = "ACKNOWLEDGEMENT_STATE_PENDING";
    private const string DeferredReplacementMode = "DEFERRED";

    public static GoogleSubscriptionMappingResult Map(
        string purchaseToken,
        GooglePlaySubscriptionPurchase purchase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purchaseToken);
        ArgumentNullException.ThrowIfNull(purchase);

        if (purchase.SubscriptionState == SubscriptionStatePending)
        {
            return Unsupported(
                "unsupported_google_pending_subscription_state",
                "Google pending subscriptions are currently not entitlement-bearing in SubTru.");
        }

        if (purchase.SubscriptionState == SubscriptionStatePendingPurchaseCanceled)
        {
            return Unsupported(
                "google_pending_purchase_canceled",
                "Google pending purchase was canceled before becoming an active subscription.");
        }

        var lineItem = SelectEntitlementLineItem(purchase);
        if (lineItem is null)
        {
            return Unsupported(
                "unsupported_google_subscription_shape",
                "SubTru currently supports one Google subscription line item or a recognized deferred replacement.");
        }

        if (lineItem.PrepaidPlan is not null)
        {
            return Unsupported(
                "unsupported_google_prepaid_subscription",
                "SubTru currently does not support Google prepaid subscriptions.");
        }

        if (lineItem.AutoRenewingPlan is null)
        {
            return Unsupported(
                "unsupported_google_subscription_shape",
                "SubTru currently supports only auto-renewing Google subscriptions.");
        }

        if (lineItem.AutoRenewingPlan.InstallmentDetails is not null)
        {
            return Unsupported(
                "unsupported_google_installment_subscription",
                "SubTru does not currently support Google installment subscriptions.");
        }

        if (string.IsNullOrWhiteSpace(lineItem.ProductId))
        {
            return Unsupported(
                "missing_google_product_id",
                "Google subscription line item is missing productId.");
        }

        if (string.IsNullOrWhiteSpace(lineItem.ExpiryTime)
            || !DateTimeOffset.TryParse(
                lineItem.ExpiryTime,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var currentPeriodEnd))
        {
            return Unsupported(
                "invalid_google_expiry_time",
                "Google subscription line item is missing a valid expiryTime.");
        }

        var providerStatus = purchase.SubscriptionState?.Trim();
        if (string.IsNullOrWhiteSpace(providerStatus))
        {
            return Unsupported(
                "missing_google_subscription_state",
                "Google subscription response is missing subscriptionState.");
        }

        var normalizedStatus = providerStatus switch
        {
            SubscriptionStateActive when lineItem.OfferPhase?.FreeTrial is not null => "trialing",
            SubscriptionStateActive => "active",
            SubscriptionStateInGracePeriod => "billing_grace",
            SubscriptionStateCanceled => "canceled",
            SubscriptionStateOnHold => "billing_suspended",
            SubscriptionStatePaused => "paused",
            SubscriptionStateExpired => "expired",
            _ => null
        };

        if (normalizedStatus is null)
        {
            return Unsupported(
                "unsupported_google_subscription_state",
                $"Google subscription state '{providerStatus}' is not currently supported by SubTru.");
        }

        string? priceId = null;
        var basePlanId = lineItem.OfferDetails?.BasePlanId;
        if (!string.IsNullOrWhiteSpace(basePlanId))
        {
            priceId = string.IsNullOrWhiteSpace(lineItem.OfferDetails?.OfferId)
                ? basePlanId
                : $"{basePlanId}:{lineItem.OfferDetails.OfferId}";
        }

        return new GoogleSubscriptionMappingResult(
            Kind: GoogleSubscriptionMappingKind.Normalized,
            Data: new GoogleNormalizedSubscriptionData(
                ProviderSubscriptionId: purchaseToken,
                ProductId: lineItem.ProductId,
                PriceId: priceId,
                Status: normalizedStatus,
                ProviderStatus: providerStatus,
                CurrentPeriodEnd: currentPeriodEnd,
                AccessEndsAt: currentPeriodEnd,
                CancelAtPeriodEnd: normalizedStatus == "canceled",
                ShouldAcknowledge: providerStatus is not SubscriptionStatePending and not SubscriptionStatePendingPurchaseCanceled,
                LinkedPurchaseToken: purchase.LinkedPurchaseToken,
                AcknowledgeSubscriptionId: lineItem.ProductId),
            ErrorCode: null,
            ErrorMessage: null);
    }

    private static GooglePlaySubscriptionLineItem? SelectEntitlementLineItem(
        GooglePlaySubscriptionPurchase purchase)
    {
        var lineItems = purchase.LineItems;
        if (lineItems is null || lineItems.Count == 0)
        {
            return null;
        }

        if (lineItems.Count == 1)
        {
            return lineItems[0];
        }

        if (lineItems.Count != 2 || string.IsNullOrWhiteSpace(purchase.LinkedPurchaseToken))
        {
            return null;
        }

        var firstLineItem = lineItems[0];
        var secondLineItem = lineItems[1];

        var entitlementBeforeReplacement = SelectEntitlementBeforeDeferredReplacement(
                firstLineItem,
                secondLineItem)
            ?? SelectEntitlementBeforeDeferredReplacement(
                secondLineItem,
                firstLineItem);
        if (entitlementBeforeReplacement is not null)
        {
            return entitlementBeforeReplacement;
        }

        var expiredReplacementEntitlement = SelectExpiredDeferredReplacementEntitlement(
                purchase,
                firstLineItem,
                secondLineItem)
            ?? SelectExpiredDeferredReplacementEntitlement(
                purchase,
                secondLineItem,
                firstLineItem);
        if (expiredReplacementEntitlement is not null)
        {
            return expiredReplacementEntitlement;
        }

        var firstExpiry = ParseExpiry(firstLineItem);
        var secondExpiry = ParseExpiry(secondLineItem);
        if (firstExpiry is null || secondExpiry is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (firstExpiry <= now && secondExpiry > now)
        {
            return secondLineItem;
        }

        if (secondExpiry <= now && firstExpiry > now)
        {
            return firstLineItem;
        }

        return null;
    }

    private static GooglePlaySubscriptionLineItem? SelectExpiredDeferredReplacementEntitlement(
        GooglePlaySubscriptionPurchase purchase,
        GooglePlaySubscriptionLineItem replacementItem,
        GooglePlaySubscriptionLineItem replacedItem)
    {
        var replacementDetails = replacementItem.ItemReplacement;
        var terminalExpiry = ParseExpiry(replacedItem);

        if (purchase.SubscriptionState != SubscriptionStateExpired
            || purchase.CanceledStateContext?.ReplacementCancellation is null
            || replacementDetails is null
            || !string.Equals(
                replacementDetails.ReplacementMode?.Trim(),
                DeferredReplacementMode,
                StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(replacementItem.ProductId)
            || !string.Equals(
                replacementDetails.ProductId?.Trim(),
                replacedItem.ProductId?.Trim(),
                StringComparison.Ordinal)
            || string.Equals(
                replacementItem.ProductId.Trim(),
                replacedItem.ProductId?.Trim(),
                StringComparison.Ordinal)
            || ParseExpiry(replacementItem) is not null
            || terminalExpiry is null)
        {
            return null;
        }

        // Google can omit the replacement item's expiry after this deferred
        // purchase token is itself replaced. The other line item's expiry is
        // then the terminal time for the purchase.
        return replacementItem with { ExpiryTime = replacedItem.ExpiryTime };
    }

    private static GooglePlaySubscriptionLineItem? SelectEntitlementBeforeDeferredReplacement(
        GooglePlaySubscriptionLineItem existingEntitlementItem,
        GooglePlaySubscriptionLineItem futureEntitlementItem)
    {
        var futureProductId = existingEntitlementItem.DeferredItemReplacement?.ProductId?.Trim();
        var replacementDetails = futureEntitlementItem.ItemReplacement;
        var replacementDetailsMatchExistingEntitlement = replacementDetails is null
            || (string.Equals(
                    replacementDetails.ProductId?.Trim(),
                    existingEntitlementItem.ProductId?.Trim(),
                    StringComparison.Ordinal)
                && string.Equals(
                    replacementDetails.ReplacementMode?.Trim(),
                    DeferredReplacementMode,
                    StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(futureProductId)
            || !string.Equals(futureProductId, futureEntitlementItem.ProductId?.Trim(), StringComparison.Ordinal)
            || !replacementDetailsMatchExistingEntitlement
            || ParseExpiry(existingEntitlementItem) is null
            || !string.IsNullOrWhiteSpace(futureEntitlementItem.ExpiryTime))
        {
            return null;
        }

        return existingEntitlementItem;
    }

    private static DateTimeOffset? ParseExpiry(GooglePlaySubscriptionLineItem lineItem)
        => DateTimeOffset.TryParse(
            lineItem.ExpiryTime,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var expiry)
            ? expiry
            : null;

    private static GoogleSubscriptionMappingResult Unsupported(
        string errorCode,
        string errorMessage) =>
        new(
            Kind: GoogleSubscriptionMappingKind.Unsupported,
            Data: null,
            ErrorCode: errorCode,
            ErrorMessage: errorMessage);
}

public enum GoogleSubscriptionMappingKind
{
    Normalized,
    Unsupported
}

public sealed record GoogleNormalizedSubscriptionData(
    string ProviderSubscriptionId,
    string ProductId,
    string? PriceId,
    string Status,
    string ProviderStatus,
    DateTimeOffset CurrentPeriodEnd,
    DateTimeOffset? AccessEndsAt,
    bool CancelAtPeriodEnd,
    bool ShouldAcknowledge,
    string? LinkedPurchaseToken,
    string? AcknowledgeSubscriptionId
);

public sealed record GoogleSubscriptionMappingResult(
    GoogleSubscriptionMappingKind Kind,
    GoogleNormalizedSubscriptionData? Data,
    string? ErrorCode,
    string? ErrorMessage
);
