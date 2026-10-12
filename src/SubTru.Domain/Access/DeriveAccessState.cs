using SubTru.Domain.Subscriptions;

namespace SubTru.Domain.Access;

public static class AccessStateDeriver
{
    public static IReadOnlyList<ProductAccessState> DeriveEntitlements(
        IReadOnlyList<Subscription> subscriptions,
        DateTimeOffset now
    )
    {
        return subscriptions
            .Where(subscription => !string.IsNullOrWhiteSpace(subscription.ProductId))
            .GroupBy(s => s.ProductId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var access = DeriveAccessState(group.ToList(), now);

                return new ProductAccessState(
                    ProductId: group.Key,
                    HasAccess: access.HasAccess,
                    AccessReason: access.AccessReason
                        ?? throw new InvalidOperationException("A subscription access state must have a reason."),
                    AccessEndsAt: access.AccessEndsAt
                );
            })
            .ToList();
    }

    public static AccessState DeriveAccessState(
        IReadOnlyList<Subscription> subscriptions,
        DateTimeOffset now
    )
    {
        if (subscriptions.Count == 0)
        {
            return new AccessState(false, null, null);
        }

        var currentAccessRows = subscriptions
            .Where(subscription => GrantsAccessNow(subscription, now))
            .ToList();

        if (currentAccessRows.Count > 0)
        {
            return new AccessState(
                HasAccess: true,
                AccessReason: GetCurrentAccessReason(currentAccessRows),
                AccessEndsAt: GetAccessEndsAt(currentAccessRows)
            );
        }

        var billingSuspendedRows = subscriptions
            .Where(subscription => subscription.Status == SubscriptionStatus.BillingSuspended)
            .ToList();

        if (billingSuspendedRows.Count > 0)
        {
            return new AccessState(
                HasAccess: false,
                AccessReason: AccessReason.PaymentFailedPastGracePeriod,
                AccessEndsAt: GetAccessEndsAt(billingSuspendedRows)
            );
        }

        var pausedRows = subscriptions
            .Where(subscription => subscription.Status == SubscriptionStatus.Paused)
            .ToList();

        if (pausedRows.Count > 0)
        {
            return new AccessState(
                HasAccess: false,
                AccessReason: AccessReason.PausedSubscription,
                AccessEndsAt: GetAccessEndsAt(pausedRows)
            );
        }

        return new AccessState(
            HasAccess: false,
            AccessReason: AccessReason.ExpiredSubscription,
            AccessEndsAt: GetAccessEndsAt(subscriptions)
        );
    }

    private static bool GrantsAccessNow(Subscription subscription, DateTimeOffset asOf)
    {
        return subscription.Status switch
        {
            SubscriptionStatus.Active => EndsAfter(subscription, asOf),
            SubscriptionStatus.Trialing => EndsAfter(subscription, asOf),
            SubscriptionStatus.Canceled => EndsAfter(subscription, asOf),
            SubscriptionStatus.BillingGrace => EndsAfterOrIsUnknown(subscription, asOf),
            _ => false
        };
    }

    private static AccessReason GetCurrentAccessReason(
        IReadOnlyList<Subscription> currentAccessRows
    )
    {
        if (currentAccessRows.Any(subscription =>
                subscription.Status == SubscriptionStatus.Active
                && !subscription.CancelAtPeriodEnd))
        {
            return AccessReason.ActiveSubscription;
        }

        if (currentAccessRows.Any(subscription => subscription.Status == SubscriptionStatus.Trialing))
        {
            return AccessReason.TrialingSubscription;
        }

        if (currentAccessRows.Any(subscription =>
                subscription.Status == SubscriptionStatus.Canceled
                || subscription.Status == SubscriptionStatus.Active
                    && subscription.CancelAtPeriodEnd))
        {
            return AccessReason.CanceledSubscription;
        }

        if (currentAccessRows.Any(subscription =>
                subscription.Status == SubscriptionStatus.BillingGrace))
        {
            return AccessReason.PaymentFailedInGracePeriod;
        }

        throw new InvalidOperationException("Unable to determine the reason for current access.");
    }

    private static DateTimeOffset? GetAccessEndsAt(IReadOnlyList<Subscription> subscriptions)
    {
        if (subscriptions.Count == 0
            || subscriptions.Any(subscription => !subscription.AccessEndsAt.HasValue))
        {
            return null;
        }

        return subscriptions.Max(subscription => subscription.AccessEndsAt);
    }

    private static bool EndsAfter(Subscription subscription, DateTimeOffset asOf)
        => subscription.AccessEndsAt > asOf;

    private static bool EndsAfterOrIsUnknown(Subscription subscription, DateTimeOffset asOf)
        => !subscription.AccessEndsAt.HasValue || subscription.AccessEndsAt > asOf;
}
