namespace SubTru.Infrastructure.Providers.Apple;

public sealed record AppleTrustedNotification(
    string NotificationType,
    string? Subtype,
    string NotificationUuid,
    DateTimeOffset SignedDate,
    AppleTrustedNotificationData? Data
);

public sealed record AppleTrustedNotificationData(
    string BundleId,
    long? AppAppleId,
    string Environment,
    int? Status,
    AppleTrustedTransactionInfo? TransactionInfo,
    AppleTrustedRenewalInfo? RenewalInfo
);

public sealed record AppleTrustedTransactionInfo(
    string TransactionId,
    string OriginalTransactionId,
    string ProductId,
    string? AppAccountToken,
    string BundleId,
    string Environment,
    string? Type,
    DateTimeOffset? ExpiresDate,
    DateTimeOffset? RevocationDate,
    DateTimeOffset SignedDate,
    string? OfferDiscountType = null
);

public sealed record AppleTrustedRenewalInfo(
    string OriginalTransactionId,
    string? ProductId,
    string? AutoRenewProductId,
    string? AppAccountToken,
    string Environment,
    bool? AutoRenewStatus,
    DateTimeOffset? GracePeriodExpiresDate,
    DateTimeOffset? RenewalDate,
    bool? IsInBillingRetryPeriod,
    int? ExpirationIntent,
    DateTimeOffset SignedDate
);
