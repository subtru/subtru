using System.Text.Json.Serialization;

namespace SubTru.Infrastructure.Providers.Google;

public sealed record GooglePlaySubscriptionPurchase(
    [property: JsonPropertyName("subscriptionState")]
    string? SubscriptionState,
    [property: JsonPropertyName("acknowledgementState")]
    string? AcknowledgementState,
    [property: JsonPropertyName("linkedPurchaseToken")]
    string? LinkedPurchaseToken,
    [property: JsonPropertyName("lineItems")]
    IReadOnlyList<GooglePlaySubscriptionLineItem>? LineItems,
    [property: JsonPropertyName("externalAccountIdentifiers")]
    GooglePlayExternalAccountIdentifiers? ExternalAccountIdentifiers,
    [property: JsonPropertyName("outOfAppPurchaseContext")]
    GooglePlayOutOfAppPurchaseContext? OutOfAppPurchaseContext,
    [property: JsonPropertyName("testPurchase")]
    GooglePlayTestPurchase? TestPurchase = null,
    [property: JsonPropertyName("canceledStateContext")]
    GooglePlayCanceledStateContext? CanceledStateContext = null
)
{
    [JsonIgnore]
    public string? RawResponseJson { get; init; }
}

public sealed record GooglePlayTestPurchase();

public sealed record GooglePlayCanceledStateContext(
    [property: JsonPropertyName("replacementCancellation")]
    GooglePlayReplacementCancellation? ReplacementCancellation = null,
    [property: JsonPropertyName("systemInitiatedCancellation")]
    GooglePlaySystemInitiatedCancellation? SystemInitiatedCancellation = null
);

public sealed record GooglePlayReplacementCancellation();

public sealed record GooglePlaySystemInitiatedCancellation();

public sealed record GooglePlaySubscriptionLineItem(
    [property: JsonPropertyName("productId")]
    string? ProductId,
    [property: JsonPropertyName("expiryTime")]
    string? ExpiryTime,
    [property: JsonPropertyName("autoRenewingPlan")]
    GooglePlayAutoRenewingPlan? AutoRenewingPlan,
    [property: JsonPropertyName("prepaidPlan")]
    GooglePlayPrepaidPlan? PrepaidPlan,
    [property: JsonPropertyName("offerDetails")]
    GooglePlayOfferDetails? OfferDetails,
    [property: JsonPropertyName("deferredItemReplacement")]
    GooglePlayDeferredItemReplacement? DeferredItemReplacement,
    [property: JsonPropertyName("itemReplacement")]
    GooglePlayItemReplacement? ItemReplacement,
    [property: JsonPropertyName("offerPhase")]
    GooglePlayOfferPhase? OfferPhase
);

public sealed record GooglePlayAutoRenewingPlan(
    [property: JsonPropertyName("autoRenewEnabled")]
    bool? AutoRenewEnabled,
    [property: JsonPropertyName("installmentDetails")]
    GooglePlayInstallmentDetails? InstallmentDetails
);

public sealed record GooglePlayPrepaidPlan(
    [property: JsonPropertyName("allowExtendAfterTime")]
    string? AllowExtendAfterTime
);

public sealed record GooglePlayInstallmentDetails(
    [property: JsonPropertyName("remainingCommittedPaymentsCount")]
    int? RemainingCommittedPaymentsCount
);

public sealed record GooglePlayOfferDetails(
    [property: JsonPropertyName("basePlanId")]
    string? BasePlanId,
    [property: JsonPropertyName("offerId")]
    string? OfferId
);

public sealed record GooglePlayDeferredItemReplacement(
    [property: JsonPropertyName("productId")]
    string? ProductId
);

public sealed record GooglePlayItemReplacement(
    [property: JsonPropertyName("productId")]
    string? ProductId,
    [property: JsonPropertyName("replacementMode")]
    string? ReplacementMode
);

public sealed record GooglePlayOfferPhase(
    [property: JsonPropertyName("prorationPeriod")]
    GooglePlayProrationPeriodOfferPhase? ProrationPeriod,
    [property: JsonPropertyName("freeTrial")]
    GooglePlayFreeTrialOfferPhase? FreeTrial,
    [property: JsonPropertyName("introductoryPrice")]
    GooglePlayIntroductoryPriceOfferPhase? IntroductoryPrice,
    [property: JsonPropertyName("basePrice")]
    GooglePlayBasePriceOfferPhase? BasePrice
);

public sealed record GooglePlayProrationPeriodOfferPhase();

public sealed record GooglePlayFreeTrialOfferPhase();

public sealed record GooglePlayIntroductoryPriceOfferPhase();

public sealed record GooglePlayBasePriceOfferPhase();

public sealed record GooglePlayExternalAccountIdentifiers(
    [property: JsonPropertyName("externalAccountId")]
    string? ExternalAccountId,
    [property: JsonPropertyName("obfuscatedExternalAccountId")]
    string? ObfuscatedExternalAccountId,
    [property: JsonPropertyName("obfuscatedExternalProfileId")]
    string? ObfuscatedExternalProfileId
);

public sealed record GooglePlayOutOfAppPurchaseContext(
    [property: JsonPropertyName("expiredExternalAccountIdentifiers")]
    GooglePlayExternalAccountIdentifiers? ExpiredExternalAccountIdentifiers,
    [property: JsonPropertyName("expiredPurchaseToken")]
    string? ExpiredPurchaseToken
);
