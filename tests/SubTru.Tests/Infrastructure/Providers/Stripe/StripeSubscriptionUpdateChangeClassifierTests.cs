using System.Text.Json;
using SubTru.Infrastructure.Providers.Stripe;

namespace SubTru.Tests.Infrastructure.Providers.Stripe;

public sealed class StripeSubscriptionUpdateChangeClassifierTests
{
    [Theory]
    [InlineData(false, true, StripeSubscriptionUpdateChanges.CancellationScheduled)]
    [InlineData(true, false, StripeSubscriptionUpdateChanges.CancellationReversed)]
    public void Classify_CancelAtPeriodEndChanges_ReturnsCancellationChange(
        bool previousValue,
        bool currentValue,
        string expectedChange)
    {
        var payload = Payload(
            Current(cancelAtPeriodEnd: currentValue),
            new { cancel_at_period_end = previousValue });

        var change = StripeSubscriptionUpdateChangeClassifier.Classify(payload);

        Assert.Equal(expectedChange, change);
    }

    [Fact]
    public void Classify_FlexibleBillingCancelAtAdded_ReturnsCancellationScheduled()
    {
        var payload = Payload(
            Current(cancelAt: 1790186854),
            new { cancel_at = (long?)null });

        var change = StripeSubscriptionUpdateChangeClassifier.Classify(payload);

        Assert.Equal(StripeSubscriptionUpdateChanges.CancellationScheduled, change);
    }

    [Fact]
    public void Classify_FlexibleBillingCancelAtRemoved_ReturnsCancellationReversed()
    {
        var payload = Payload(
            Current(cancelAt: null),
            new { cancel_at = 1790186854 });

        var change = StripeSubscriptionUpdateChangeClassifier.Classify(payload);

        Assert.Equal(StripeSubscriptionUpdateChanges.CancellationReversed, change);
    }

    [Fact]
    public void Classify_FlexibleBillingCancelAtMoved_ReturnsNull()
    {
        var payload = Payload(
            Current(cancelAt: 1790273254),
            new { cancel_at = 1790186854 });

        var change = StripeSubscriptionUpdateChangeClassifier.Classify(payload);

        Assert.Null(change);
    }

    [Theory]
    [InlineData("active", "paused", StripeSubscriptionUpdateChanges.SubscriptionPaused)]
    [InlineData("paused", "active", StripeSubscriptionUpdateChanges.SubscriptionResumed)]
    public void Classify_PausedStatusChanges_ReturnsSubscriptionChange(
        string previousStatus,
        string currentStatus,
        string expectedChange)
    {
        var payload = Payload(
            Current(status: currentStatus),
            new { status = previousStatus });

        var change = StripeSubscriptionUpdateChangeClassifier.Classify(payload);

        Assert.Equal(expectedChange, change);
    }

    [Fact]
    public void Classify_PriceChanges_ReturnsPlanChanged()
    {
        var payload = Payload(
            Current(items: Items("price_current")),
            new { items = Items("price_previous") });

        var change = StripeSubscriptionUpdateChangeClassifier.Classify(payload);

        Assert.Equal(StripeSubscriptionUpdateChanges.SubscriptionPlanChanged, change);
    }

    [Fact]
    public void Classify_TrialBecomesActive_ReturnsTrialEnded()
    {
        var payload = Payload(
            Current(
                status: "active",
                items: Items("price_current", periodStart: 200, periodEnd: 300)),
            new
            {
                status = "trialing",
                items = Items("price_current", periodStart: 100, periodEnd: 200)
            });

        var change = StripeSubscriptionUpdateChangeClassifier.Classify(payload);

        Assert.Equal(StripeSubscriptionUpdateChanges.TrialEnded, change);
    }

    [Fact]
    public void Classify_BillingPeriodAdvances_ReturnsBillingPeriodStarted()
    {
        var payload = Payload(
            Current(items: Items("price_current", periodStart: 200, periodEnd: 300)),
            new { items = Items("price_current", periodStart: 100, periodEnd: 200) });

        var change = StripeSubscriptionUpdateChangeClassifier.Classify(payload);

        Assert.Equal(StripeSubscriptionUpdateChanges.BillingPeriodStarted, change);
    }

    [Fact]
    public void Classify_NoncontiguousBillingPeriodChange_ReturnsNull()
    {
        var payload = Payload(
            Current(items: Items("price_current", periodStart: 150, periodEnd: 250)),
            new { items = Items("price_current", periodStart: 100, periodEnd: 200) });

        var change = StripeSubscriptionUpdateChangeClassifier.Classify(payload);

        Assert.Null(change);
    }

    [Fact]
    public void Classify_PriceAndBillingPeriodChange_ReturnsPlanChanged()
    {
        var payload = Payload(
            Current(items: Items("price_current", periodStart: 200, periodEnd: 300)),
            new { items = Items("price_previous", periodStart: 100, periodEnd: 200) });

        var change = StripeSubscriptionUpdateChangeClassifier.Classify(payload);

        Assert.Equal(StripeSubscriptionUpdateChanges.SubscriptionPlanChanged, change);
    }

    [Fact]
    public void Classify_WithoutPreviousAttributes_ReturnsNull()
    {
        var payload = Payload(Current(), previousAttributes: null);

        var change = StripeSubscriptionUpdateChangeClassifier.Classify(payload);

        Assert.Null(change);
    }

    private static string Payload(object current, object? previousAttributes)
        => JsonSerializer.Serialize(new
        {
            data = new
            {
                @object = current,
                previous_attributes = previousAttributes
            }
        });

    private static object Current(
        long? cancelAt = null,
        bool cancelAtPeriodEnd = false,
        string status = "active",
        object? items = null)
        => new
        {
            cancel_at = cancelAt,
            cancel_at_period_end = cancelAtPeriodEnd,
            status,
            items = items ?? Items("price_current")
        };

    private static object Items(
        string priceId,
        long? periodStart = null,
        long? periodEnd = null)
        => new
        {
            data = new[]
            {
                new
                {
                    current_period_start = periodStart,
                    current_period_end = periodEnd,
                    price = new { id = priceId }
                }
            }
        };
}
