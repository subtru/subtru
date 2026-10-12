namespace SubTru.Domain.Subscriptions;

public enum SubscriptionStatus
{
    Active,
    Trialing,
    Canceled,
    BillingGrace,
    BillingSuspended,
    Paused,
    Expired
}
