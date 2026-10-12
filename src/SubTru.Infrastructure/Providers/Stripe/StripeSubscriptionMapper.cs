using StripeSubscription = Stripe.Subscription;
using StripeSubscriptionItem = Stripe.SubscriptionItem;
using SubTru.Domain.Subscriptions;

namespace SubTru.Infrastructure.Providers.Stripe;

public static class StripeSubscriptionMapper
{
    private static string NormalizeStatus(string? stripeStatus)
    {
        if (string.IsNullOrWhiteSpace(stripeStatus))
        {
            return "expired";
        }

        return stripeStatus.Trim().ToLowerInvariant() switch
        {
            "active" => "active",
            "trialing" => "trialing",
            "canceled" => "canceled",
            "paused" => "paused",
            "incomplete" => "expired",
            "incomplete_expired" => "expired",
            "past_due" => "billing_grace",
            "unpaid" => "expired",
            _ => "expired"
        };
    }

    public static bool TryMapSingleItem(
        StripeSubscription stripeSub,
        Guid projectId,
        string environment,
        string externalUserId,
        out NormalizedSubscription? normalized,
        out string? error)
    {
        normalized = null;
        error = null;

        if (string.IsNullOrWhiteSpace(stripeSub.Id))
        {
            error = "missing_provider_subscription_id";
            return false;
        }

        var items = stripeSub.Items?.Data;
        if (items is null || items.Count == 0)
        {
            error = "missing_subscription_items";
            return false;
        }

        if (items.Count != 1)
        {
            error = "unsupported_multiple_subscription_items";
            return false;
        }

        StripeSubscriptionItem item = items[0];

        string? priceId = item.Price?.Id;
        if (string.IsNullOrWhiteSpace(priceId))
        {
            error = "missing_price_id";
            return false;
        }

        string? productId = item.Price?.ProductId;
        if (string.IsNullOrWhiteSpace(productId))
        {
            error = "missing_product_id";
            return false;
        }

        if (item.CurrentPeriodEnd == default)
        {
            error = "missing_current_period_end";
            return false;
        }

        var currentPeriodEnd = new DateTimeOffset(item.CurrentPeriodEnd, TimeSpan.Zero);

        var providerStatus = string.IsNullOrWhiteSpace(stripeSub.Status)
            ? null
            : stripeSub.Status.Trim();

        // Pausing payment collection does not pause the subscription itself.
        // Only Stripe's provider status determines the normalized access state.
        string status = NormalizeStatus(providerStatus);

        DateTimeOffset? accessEndsAt;
        if (string.Equals(providerStatus, "past_due", StringComparison.OrdinalIgnoreCase))
        {
            // Stripe doesn't provide a "grace period ends at" value, so access remains
            // unknown until a later provider event updates the subscription state.
            accessEndsAt = null;
        }
        else if (string.Equals(providerStatus, "canceled", StringComparison.OrdinalIgnoreCase))
        {
            if (!stripeSub.EndedAt.HasValue)
            {
                error = "missing_ended_at";
                return false;
            }

            accessEndsAt = new DateTimeOffset(stripeSub.EndedAt.Value, TimeSpan.Zero);
        }
        else
        {
            accessEndsAt = currentPeriodEnd;
        }

        // Classic billing uses cancel_at_period_end, while flexible billing uses
        // an explicit cancel_at timestamp for Customer Portal cancellations.
        var cancellationScheduled = stripeSub.CancelAtPeriodEnd || stripeSub.CancelAt.HasValue;

        normalized = new NormalizedSubscription(
            ProjectId: projectId,
            Environment: environment,
            ExternalUserId: externalUserId,
            Provider: "stripe",
            ProviderSubscriptionId: stripeSub.Id,
            ProductId: productId,
            PriceId: priceId,
            Status: status,
            ProviderStatus: providerStatus,
            CurrentPeriodEnd: currentPeriodEnd,
            AccessEndsAt: accessEndsAt,
            CancelAtPeriodEnd: cancellationScheduled
        );

        return true;
    }
}
