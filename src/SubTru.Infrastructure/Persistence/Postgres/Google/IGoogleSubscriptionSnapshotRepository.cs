namespace SubTru.Infrastructure.Persistence.Postgres.Google;

public interface IGoogleSubscriptionSnapshotRepository
{
    Task SaveAsync(
        GoogleSubscriptionSnapshotWriteModel snapshot,
        CancellationToken ct = default);
}

public sealed record GoogleSubscriptionSnapshotWriteModel(
    Guid ProjectId,
    string Environment,
    string EventId,
    int FetchSequence,
    string PurchaseToken,
    DateTimeOffset FetchedAt,
    string RawResponseJson,
    string? SubscriptionState,
    IReadOnlyList<string> ProductIds
);
