namespace SubTru.Api.Platform.Observability;

internal interface IBusinessMetrics
{
    void RecordWebhookEvent(
        string provider,
        string environment,
        string status,
        string? errorCode,
        TimeSpan processingDuration);

    void RecordWebhookAcknowledgement(
        string provider,
        string environment,
        string status,
        string? errorCode);

    void RecordAccessCheck(
        string environment,
        string status,
        string reason);
}
