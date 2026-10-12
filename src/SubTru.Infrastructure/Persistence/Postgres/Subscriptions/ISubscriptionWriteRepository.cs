using SubTru.Domain.Subscriptions;

namespace SubTru.Infrastructure.Persistence.Postgres.Subscriptions;

public interface ISubscriptionWriteRepository
{
    /// <summary>
    /// Atomically claims a provider event, then upserts the subscription if this
    /// call won first processing and the event is not older than stored state.
    /// </summary>
    Task<SubscriptionWriteOutcome> UpsertWithEventAsync(
        NormalizedSubscription sub,
        string eventId,
        CancellationToken ct);
}
