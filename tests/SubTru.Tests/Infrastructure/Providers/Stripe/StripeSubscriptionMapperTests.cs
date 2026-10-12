using SubTru.Domain.Subscriptions;
using SubTru.Infrastructure.Providers.Stripe;
using Stripe;

namespace SubTru.Tests.Infrastructure.Providers.Stripe;

public class StripeSubscriptionMapperTests
{
    private static readonly Guid TestProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string TestUserId = "test-user-123";
    private static readonly DateTime TestPeriodEnd = new DateTime(2026, 02, 01, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime TestEndedAt = new DateTime(2026, 01, 15, 12, 0, 0, DateTimeKind.Utc);

    private static Subscription CreateValidSubscription(string status = "active")
    {
        return new Subscription
        {
            Id = "sub_test123",
            Status = status,
            EndedAt = status == "canceled" ? TestEndedAt : null,
            CancelAtPeriodEnd = false,
            Items = new StripeList<SubscriptionItem>
            {
                Data = new List<SubscriptionItem>
                {
                    new SubscriptionItem
                    {
                        CurrentPeriodEnd = TestPeriodEnd,
                        Price = new Price
                        {
                            Id = "price_test123",
                            ProductId = "prod_test123"
                        }
                    }
                }
            }
        };
    }

    [Fact]
    public void TryMapSingleItem_ValidSubscription_ReturnsTrue()
    {
        var stripeSub = CreateValidSubscription();

        var result = StripeSubscriptionMapper.TryMapSingleItem(
            stripeSub,
            TestProjectId,
            SubscriptionEnvironment.Production,
            TestUserId,
            out var normalized,
            out var error);

        Assert.True(result);
        Assert.NotNull(normalized);
        Assert.Null(error);
        Assert.Equal(TestProjectId, normalized.ProjectId);
        Assert.Equal(TestUserId, normalized.ExternalUserId);
        Assert.Equal("stripe", normalized.Provider);
        Assert.Equal("sub_test123", normalized.ProviderSubscriptionId);
        Assert.Equal("prod_test123", normalized.ProductId);
        Assert.Equal("price_test123", normalized.PriceId);
        Assert.Equal("active", normalized.Status);
        Assert.Equal("active", normalized.ProviderStatus);
        Assert.Equal(normalized.CurrentPeriodEnd, normalized.AccessEndsAt);
        Assert.False(normalized.CancelAtPeriodEnd);
    }

    [Fact]
    public void TryMapSingleItem_MissingSubscriptionId_ReturnsFalse()
    {
        var stripeSub = CreateValidSubscription();
        stripeSub.Id = "";

        var result = StripeSubscriptionMapper.TryMapSingleItem(
            stripeSub,
            TestProjectId,
            SubscriptionEnvironment.Production,
            TestUserId,
            out var normalized,
            out var error);

        Assert.False(result);
        Assert.Null(normalized);
        Assert.Equal("missing_provider_subscription_id", error);
    }

    [Fact]
    public void TryMapSingleItem_NullItems_ReturnsFalse()
    {
        var stripeSub = CreateValidSubscription();
        stripeSub.Items = null;

        var result = StripeSubscriptionMapper.TryMapSingleItem(
            stripeSub,
            TestProjectId,
            SubscriptionEnvironment.Production,
            TestUserId,
            out var normalized,
            out var error);

        Assert.False(result);
        Assert.Null(normalized);
        Assert.Equal("missing_subscription_items", error);
    }

    [Fact]
    public void TryMapSingleItem_EmptyItems_ReturnsFalse()
    {
        var stripeSub = CreateValidSubscription();
        stripeSub.Items = new StripeList<SubscriptionItem> { Data = new List<SubscriptionItem>() };

        var result = StripeSubscriptionMapper.TryMapSingleItem(
            stripeSub,
            TestProjectId,
            SubscriptionEnvironment.Production,
            TestUserId,
            out var normalized,
            out var error);

        Assert.False(result);
        Assert.Null(normalized);
        Assert.Equal("missing_subscription_items", error);
    }

    [Fact]
    public void TryMapSingleItem_MultipleItems_ReturnsFalse()
    {
        var stripeSub = CreateValidSubscription();
        stripeSub.Items = new StripeList<SubscriptionItem>
        {
            Data = new List<SubscriptionItem>
            {
                new SubscriptionItem { Price = new Price { Id = "price_1", ProductId = "prod_1" }, CurrentPeriodEnd = TestPeriodEnd },
                new SubscriptionItem { Price = new Price { Id = "price_2", ProductId = "prod_2" }, CurrentPeriodEnd = TestPeriodEnd }
            }
        };

        var result = StripeSubscriptionMapper.TryMapSingleItem(
            stripeSub,
            TestProjectId,
            SubscriptionEnvironment.Production,
            TestUserId,
            out var normalized,
            out var error);

        Assert.False(result);
        Assert.Null(normalized);
        Assert.Equal("unsupported_multiple_subscription_items", error);
    }

    [Fact]
    public void TryMapSingleItem_MissingPriceId_ReturnsFalse()
    {
        var stripeSub = CreateValidSubscription();
        stripeSub.Items.Data[0].Price = new Price { Id = null, ProductId = "prod_test" };

        var result = StripeSubscriptionMapper.TryMapSingleItem(
            stripeSub,
            TestProjectId,
            SubscriptionEnvironment.Production,
            TestUserId,
            out var normalized,
            out var error);

        Assert.False(result);
        Assert.Null(normalized);
        Assert.Equal("missing_price_id", error);
    }

    [Fact]
    public void TryMapSingleItem_MissingProductId_ReturnsFalse()
    {
        var stripeSub = CreateValidSubscription();
        stripeSub.Items.Data[0].Price = new Price { Id = "price_test", ProductId = null };

        var result = StripeSubscriptionMapper.TryMapSingleItem(
            stripeSub,
            TestProjectId,
            SubscriptionEnvironment.Production,
            TestUserId,
            out var normalized,
            out var error);

        Assert.False(result);
        Assert.Null(normalized);
        Assert.Equal("missing_product_id", error);
    }

    [Fact]
    public void TryMapSingleItem_MissingCurrentPeriodEnd_ReturnsFalse()
    {
        var stripeSub = CreateValidSubscription();
        stripeSub.Items.Data[0].CurrentPeriodEnd = default;

        var result = StripeSubscriptionMapper.TryMapSingleItem(
            stripeSub,
            TestProjectId,
            SubscriptionEnvironment.Production,
            TestUserId,
            out var normalized,
            out var error);

        Assert.False(result);
        Assert.Null(normalized);
        Assert.Equal("missing_current_period_end", error);
    }

    [Theory]
    [InlineData("active", "active")]
    [InlineData("trialing", "trialing")]
    [InlineData("canceled", "canceled")]
    [InlineData("paused", "paused")]
    [InlineData("incomplete", "expired")]
    [InlineData("incomplete_expired", "expired")]
    [InlineData("past_due", "billing_grace")]
    [InlineData("unpaid", "expired")]
    [InlineData("unknown_status", "expired")]
    [InlineData(null, "expired")]
    [InlineData("", "expired")]
    [InlineData("  ", "expired")]
    public void TryMapSingleItem_StatusMapping_MapsCorrectly(string? stripeStatus, string expectedStatus)
    {
        var stripeSub = CreateValidSubscription();
        stripeSub.Status = stripeStatus;
        stripeSub.EndedAt = stripeStatus == "canceled" ? TestEndedAt : null;

        var result = StripeSubscriptionMapper.TryMapSingleItem(
            stripeSub,
            TestProjectId,
            SubscriptionEnvironment.Production,
            TestUserId,
            out var normalized,
            out var error);

        Assert.True(result);
        Assert.NotNull(normalized);
        Assert.Equal(expectedStatus, normalized.Status);
        Assert.Equal(
            string.IsNullOrWhiteSpace(stripeStatus) ? null : stripeStatus.Trim(),
            normalized.ProviderStatus);
    }

    [Fact]
    public void TryMapSingleItem_CancelAtPeriodEnd_MapsCorrectly()
    {
        var stripeSub = CreateValidSubscription();
        stripeSub.CancelAtPeriodEnd = true;

        var result = StripeSubscriptionMapper.TryMapSingleItem(
            stripeSub,
            TestProjectId,
            SubscriptionEnvironment.Production,
            TestUserId,
            out var normalized,
            out var error);

        Assert.True(result);
        Assert.NotNull(normalized);
        Assert.True(normalized.CancelAtPeriodEnd);
    }

    [Fact]
    public void TryMapSingleItem_FlexibleBillingCancelAt_MapsScheduledCancellation()
    {
        var stripeSub = CreateValidSubscription();
        stripeSub.CancelAt = TestPeriodEnd;
        stripeSub.CancelAtPeriodEnd = false;

        var result = StripeSubscriptionMapper.TryMapSingleItem(
            stripeSub,
            TestProjectId,
            SubscriptionEnvironment.Production,
            TestUserId,
            out var normalized,
            out var error);

        Assert.True(result);
        Assert.NotNull(normalized);
        Assert.Null(error);
        Assert.True(normalized.CancelAtPeriodEnd);
    }

    [Fact]
    public void TryMapSingleItem_CurrentPeriodEnd_MapsFromItem()
    {
        var stripeSub = CreateValidSubscription();

        var result = StripeSubscriptionMapper.TryMapSingleItem(
            stripeSub,
            TestProjectId,
            SubscriptionEnvironment.Production,
            TestUserId,
            out var normalized,
            out var error);

        Assert.True(result);
        Assert.NotNull(normalized);
        Assert.Equal(TestPeriodEnd, normalized.CurrentPeriodEnd.UtcDateTime);
    }

    [Fact]
    public void TryMapSingleItem_CanceledSubscription_MapsAccessEndFromEndedAt()
    {
        var stripeSub = CreateValidSubscription("canceled");

        var result = StripeSubscriptionMapper.TryMapSingleItem(
            stripeSub,
            TestProjectId,
            SubscriptionEnvironment.Production,
            TestUserId,
            out var normalized,
            out var error);

        Assert.True(result);
        Assert.NotNull(normalized);
        Assert.Null(error);
        Assert.Equal(TestPeriodEnd, normalized.CurrentPeriodEnd.UtcDateTime);
        Assert.Equal(TestEndedAt, normalized.AccessEndsAt?.UtcDateTime);
    }

    [Fact]
    public void TryMapSingleItem_CanceledSubscriptionWithoutEndedAt_ReturnsFalse()
    {
        var stripeSub = CreateValidSubscription("canceled");
        stripeSub.EndedAt = null;

        var result = StripeSubscriptionMapper.TryMapSingleItem(
            stripeSub,
            TestProjectId,
            SubscriptionEnvironment.Production,
            TestUserId,
            out var normalized,
            out var error);

        Assert.False(result);
        Assert.Null(normalized);
        Assert.Equal("missing_ended_at", error);
    }

    [Fact]
    public void TryMapSingleItem_PastDue_LeavesAccessEndUnknown()
    {
        var stripeSub = CreateValidSubscription("past_due");

        var result = StripeSubscriptionMapper.TryMapSingleItem(
            stripeSub,
            TestProjectId,
            SubscriptionEnvironment.Production,
            TestUserId,
            out var normalized,
            out _);

        Assert.True(result);
        Assert.NotNull(normalized);
        Assert.Equal("billing_grace", normalized.Status);
        Assert.Equal("past_due", normalized.ProviderStatus);
        Assert.Null(normalized.AccessEndsAt);
    }

    [Fact]
    public void TryMapSingleItem_UnpaidStatus_MapsExpiredWithFiniteAccessEnd()
    {
        var stripeSub = CreateValidSubscription("unpaid");

        var result = StripeSubscriptionMapper.TryMapSingleItem(
            stripeSub,
            TestProjectId,
            SubscriptionEnvironment.Production,
            TestUserId,
            out var normalized,
            out _);

        Assert.True(result);
        Assert.NotNull(normalized);
        Assert.Equal("expired", normalized.Status);
        Assert.Equal(normalized.CurrentPeriodEnd, normalized.AccessEndsAt);
    }

    [Fact]
    public void TryMapSingleItem_PauseCollectionSet_PreservesActiveStatus()
    {
        var stripeSub = CreateValidSubscription("active");
        stripeSub.PauseCollection = new SubscriptionPauseCollection
        {
            Behavior = "void"
        };

        var result = StripeSubscriptionMapper.TryMapSingleItem(
            stripeSub,
            TestProjectId,
            SubscriptionEnvironment.Production,
            TestUserId,
            out var normalized,
            out var error);

        Assert.True(result);
        Assert.NotNull(normalized);
        Assert.Equal("active", normalized.Status);
        Assert.Equal("active", normalized.ProviderStatus);
    }
}
