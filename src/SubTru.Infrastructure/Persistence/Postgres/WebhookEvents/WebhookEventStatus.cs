namespace SubTru.Infrastructure.Persistence.Postgres.WebhookEvents;

/// <summary>
/// Well-known webhook event processing statuses.
/// </summary>
public static class WebhookEventStatus
{
    public const string Success = "success";
    public const string Failed = "failed";
    public const string Ignored = "ignored";
    public const string Duplicate = "duplicate";
}
