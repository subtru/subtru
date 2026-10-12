namespace SubTru.Infrastructure.Providers.Google;

public interface IGooglePlaySubscriptionClient
{
    Task<GooglePlaySubscriptionPurchase> GetSubscriptionAsync(
        string packageName,
        string purchaseToken,
        string serviceAccountJson,
        CancellationToken ct = default);

    Task AcknowledgeAsync(
        string packageName,
        string purchaseToken,
        string serviceAccountJson,
        string? subscriptionId = null,
        CancellationToken ct = default);
}
