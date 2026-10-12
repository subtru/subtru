using SubTru.Domain.Subscriptions;

namespace SubTru.Infrastructure.Persistence.Postgres.Subscriptions;

public interface IGoogleSubscriptionWriteRepository
{
    Task<SubscriptionWriteOutcome> UpsertGoogleWithEventAsync(
        NormalizedSubscription sub,
        string eventId,
        string? linkedPurchaseToken,
        CancellationToken ct);

    Task UpsertGoogleManualAsync(
        NormalizedSubscription sub,
        string? linkedPurchaseToken,
        CancellationToken ct);
}
