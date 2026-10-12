namespace SubTru.Api.Features.Projects;

public sealed record GetProjectSummaryResponse(
    int UsersWithAccess,
    int ActiveSubscriptions,
    int ExpiredSubscriptions,
    DateTimeOffset? LastWebhookReceivedAt
);
