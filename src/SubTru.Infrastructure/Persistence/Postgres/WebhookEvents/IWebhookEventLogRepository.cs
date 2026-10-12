namespace SubTru.Infrastructure.Persistence.Postgres.WebhookEvents;

public enum WebhookEventStatusFilter
{
    Failed,
    Succeeded
}

public interface IWebhookEventLogRepository
{
    Task UpsertAsync(
        WebhookEventLogWriteModel writeModel,
        CancellationToken ct = default);

    Task<WebhookEventListPageReadModel> ListRecentAsync(
        Guid projectId,
        string environment,
        int limit,
        WebhookEventCursorReadModel? cursor,
        string? provider = null,
        WebhookEventStatusFilter? statusFilter = null,
        CancellationToken ct = default);
}
