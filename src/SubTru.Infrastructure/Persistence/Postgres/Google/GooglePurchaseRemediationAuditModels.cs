namespace SubTru.Infrastructure.Persistence.Postgres.Google;

public sealed record GooglePurchaseRemediationAuditWriteModel(
    Guid ProjectId,
    string Environment,
    string RequestedPurchaseToken,
    string EffectivePurchaseToken,
    string? LinkedPurchaseToken,
    string ExternalUserId,
    string Actor,
    string Status,
    string? ProviderStatus,
    DateTimeOffset CurrentPeriodEnd,
    DateTimeOffset AcknowledgedAt
);
