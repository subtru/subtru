using System.Text.Json;
using System.Text.Json.Serialization;

namespace SubTru.Infrastructure.Providers.Stripe;

public static class StripeSubscriptionUpdateChanges
{
    public const string CancellationScheduled = "cancellation_scheduled";
    public const string CancellationReversed = "cancellation_reversed";
    public const string SubscriptionPaused = "subscription_paused";
    public const string SubscriptionResumed = "subscription_resumed";
    public const string SubscriptionPlanChanged = "subscription_plan_changed";
    public const string TrialEnded = "trial_ended";
    public const string BillingPeriodStarted = "billing_period_started";
}

public static class StripeSubscriptionUpdateChangeClassifier
{
    public static string? Classify(string verifiedPayload)
    {
        try
        {
            using var document = JsonDocument.Parse(verifiedPayload);
            if (!TryGetSubscriptionSnapshots(
                    document.RootElement,
                    out var currentJson,
                    out var previousJson))
            {
                return null;
            }

            var current = currentJson.Deserialize<StripeSubscriptionSnapshot>();
            var previous = previousJson.Deserialize<StripeSubscriptionSnapshot>();
            if (current is null || previous is null)
            {
                return null;
            }

            // Stripe only includes fields that changed in previous_attributes.
            if (previous.CancelAtPeriodEnd is bool wasCancellationScheduled
                && current.CancelAtPeriodEnd is bool isCancellationScheduled
                && wasCancellationScheduled != isCancellationScheduled)
            {
                return isCancellationScheduled
                    ? StripeSubscriptionUpdateChanges.CancellationScheduled
                    : StripeSubscriptionUpdateChanges.CancellationReversed;
            }

            if (previousJson.TryGetProperty("cancel_at", out var previousCancelAt))
            {
                var previousCancellationDateWasSet =
                    previousCancelAt.ValueKind == JsonValueKind.Number;
                var currentCancellationDateIsSet = current.CancelAt.HasValue;
                if (previousCancellationDateWasSet != currentCancellationDateIsSet)
                {
                    return currentCancellationDateIsSet
                        ? StripeSubscriptionUpdateChanges.CancellationScheduled
                        : StripeSubscriptionUpdateChanges.CancellationReversed;
                }
            }

            if (previous.Status is string previousStatus
                && current.Status is string currentStatus
                && previousStatus != currentStatus)
            {
                if (currentStatus == "paused")
                {
                    return StripeSubscriptionUpdateChanges.SubscriptionPaused;
                }

                if (previousStatus == "paused")
                {
                    return StripeSubscriptionUpdateChanges.SubscriptionResumed;
                }
            }

            var previousItem = SingleItem(previous);
            var currentItem = SingleItem(current);
            var previousPriceId = PriceId(previousItem);
            var currentPriceId = PriceId(currentItem);
            if (previousPriceId is not null
                && currentPriceId is not null
                && previousPriceId != currentPriceId)
            {
                return StripeSubscriptionUpdateChanges.SubscriptionPlanChanged;
            }

            if (previous.Status == "trialing" && current.Status == "active")
            {
                return StripeSubscriptionUpdateChanges.TrialEnded;
            }

            if (previousPriceId is not null
                && previousPriceId == currentPriceId
                && previousItem?.CurrentPeriodStart is long previousPeriodStart
                && previousItem.CurrentPeriodEnd is long previousPeriodEnd
                && currentItem?.CurrentPeriodStart is long currentPeriodStart
                && currentItem.CurrentPeriodEnd is long currentPeriodEnd
                && currentPeriodStart == previousPeriodEnd
                && currentPeriodStart > previousPeriodStart
                && currentPeriodEnd > previousPeriodEnd)
            {
                return StripeSubscriptionUpdateChanges.BillingPeriodStarted;
            }

            return null;
        }
        catch (JsonException)
        {
            // This label is display-only and must not affect webhook processing.
            return null;
        }
    }

    private static bool TryGetSubscriptionSnapshots(
        JsonElement root,
        out JsonElement current,
        out JsonElement previous)
    {
        current = default;
        previous = default;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return data.TryGetProperty("object", out current)
            && data.TryGetProperty("previous_attributes", out previous)
            && current.ValueKind == JsonValueKind.Object
            && previous.ValueKind == JsonValueKind.Object;
    }

    private static StripeSubscriptionItem? SingleItem(
        StripeSubscriptionSnapshot subscription)
    {
        var items = subscription.Items?.Data;
        if (items is null || items.Count != 1)
        {
            return null;
        }

        return items[0];
    }

    private static string? PriceId(StripeSubscriptionItem? item)
    {
        var priceId = item?.Price?.Id;
        return string.IsNullOrWhiteSpace(priceId) ? null : priceId;
    }

    private sealed record StripeSubscriptionSnapshot(
        [property: JsonPropertyName("cancel_at")] long? CancelAt,
        [property: JsonPropertyName("cancel_at_period_end")] bool? CancelAtPeriodEnd,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("items")] StripeSubscriptionItems? Items);

    private sealed record StripeSubscriptionItems(
        [property: JsonPropertyName("data")] List<StripeSubscriptionItem>? Data);

    private sealed record StripeSubscriptionItem(
        [property: JsonPropertyName("current_period_start")] long? CurrentPeriodStart,
        [property: JsonPropertyName("current_period_end")] long? CurrentPeriodEnd,
        [property: JsonPropertyName("price")] StripePrice? Price);

    private sealed record StripePrice(
        [property: JsonPropertyName("id")] string? Id);
}
