namespace SubTru.Api.Features.Webhooks.Events;

public sealed record GetWebhookEventsResponse(
    IReadOnlyList<WebhookEventDto> Items,
    string? NextCursor
);