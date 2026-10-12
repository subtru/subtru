namespace SubTru.Infrastructure.Persistence.Postgres.Projects;

public sealed record ProjectSummaryReadModel(
    int UsersWithAccess,
    int ActiveSubscriptions,
    int ExpiredSubscriptions,
    DateTimeOffset? LastWebhookReceivedAt
);
