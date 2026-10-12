namespace SubTru.Infrastructure.Persistence.Postgres.WebhookEvents;

public interface IProcessedEventRepository
{
    Task<bool> IsProcessedAsync(
        Guid projectId,
        string provider,
        string environment,
        string eventId,
        CancellationToken ct = default);
}
