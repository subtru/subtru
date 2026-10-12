namespace SubTru.Domain.Access;

public enum AccessReason
{
    ActiveSubscription,
    TrialingSubscription,
    CanceledSubscription,
    PaymentFailedInGracePeriod,
    PaymentFailedPastGracePeriod,
    PausedSubscription,
    ExpiredSubscription
}
