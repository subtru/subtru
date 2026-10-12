namespace SubTru.Infrastructure.Persistence.Postgres.Subscriptions;

public enum SubscriptionWriteOutcome
{
    Applied,
    Stale,
    Duplicate
}
