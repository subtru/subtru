namespace SubTru.Infrastructure.Persistence.Postgres.Subscriptions;

public interface ISubscriptionReadRepository
{
    Task<IReadOnlyList<SubscriptionReadModel>> GetByUserAsync(
        Guid projectId,
        string environment,
        string externalUserId,
        CancellationToken ct
    );

    Task<string?> GetExternalUserIdByProviderSubscriptionIdAsync(
        Guid projectId,
        string environment,
        string provider,
        string providerSubscriptionId,
        CancellationToken ct
    );
}
