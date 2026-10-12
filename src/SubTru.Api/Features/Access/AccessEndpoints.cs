using SubTru.Api.Platform.Http;
using SubTru.Api.Platform.Observability;
using SubTru.Domain.Access;
using SubTru.Domain.Subscriptions;
using SubTru.Infrastructure.Persistence.Postgres.Subscriptions;

namespace SubTru.Api.Features.Access;

public static class AccessEndpoints
{
    public static void MapAccessEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/projects/{projectId}/users/{externalUserId}/access", async (
            Guid projectId,
            string externalUserId,
            string? environment,
            HttpContext ctx,
            ISubscriptionReadRepository repo,
            IBusinessMetrics businessMetrics,
            CancellationToken ct) => await GetAccessAsync(projectId, externalUserId, environment, ctx, repo, businessMetrics, ct));
    }

    private static async Task<IResult> GetAccessAsync(
        Guid projectId,
        string? externalUserId,
        string? requestedEnvironment,
        HttpContext ctx,
        ISubscriptionReadRepository repo,
        IBusinessMetrics businessMetrics,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(externalUserId))
        {
            return Results.BadRequest(new
            {
                error = "missing_external_user_id",
                message = "Path parameter 'externalUserId' is required."
            });
        }

        if (!RequestEnvironment.TryResolve(ctx, requestedEnvironment, out var environment, out var environmentError))
        {
            return Results.BadRequest(environmentError);
        }

        var subs = await repo.GetByUserAsync(projectId, environment, externalUserId, ct);

        static SubscriptionProvider? MapProvider(string? provider)
        {
            if (string.IsNullOrWhiteSpace(provider))
            {
                return null;
            }

            return provider.Trim().ToLowerInvariant() switch
            {
                "stripe" => SubscriptionProvider.Stripe,
                "apple" => SubscriptionProvider.Apple,
                "google" => SubscriptionProvider.Google,
                _ => null
            };
        }

        static SubscriptionStatus MapStatus(string? status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return SubscriptionStatus.Expired;
            }

            return status.Trim().ToLowerInvariant() switch
            {
                "active" => SubscriptionStatus.Active,
                "trialing" => SubscriptionStatus.Trialing,
                "canceled" => SubscriptionStatus.Canceled,
                "billing_grace" => SubscriptionStatus.BillingGrace,
                "billing_suspended" => SubscriptionStatus.BillingSuspended,
                "paused" => SubscriptionStatus.Paused,
                "expired" => SubscriptionStatus.Expired,
                "incomplete" => SubscriptionStatus.Expired,
                "incomplete_expired" => SubscriptionStatus.Expired,
                "unpaid" => SubscriptionStatus.Expired,
                _ => SubscriptionStatus.Expired
            };
        }

        // Convert read models -> domain subscriptions (tolerant of legacy/unknown values).
        var domainSubs = new List<Subscription>(subs.Count);

        foreach (var sub in subs)
        {
            var provider = MapProvider(sub.Provider);
            if (!provider.HasValue)
            {
                continue;
            }

            domainSubs.Add(new Subscription(
                Provider: provider.Value,
                ProductId: sub.ProductId,
                Status: MapStatus(sub.Status),
                ProviderStatus: sub.ProviderStatus,
                CurrentPeriodEnd: sub.CurrentPeriodEnd,
                AccessEndsAt: sub.AccessEndsAt,
                CancelAtPeriodEnd: sub.CancelAtPeriodEnd
            ));
        }

        var entitlements = AccessStateDeriver.DeriveEntitlements(
            domainSubs,
            DateTimeOffset.UtcNow
        );

        businessMetrics.RecordAccessCheck(
            environment,
            GetAccessMetricStatus(entitlements),
            GetAccessMetricReason(entitlements));

        var response = new GetAccessResponse(
            Entitlements: entitlements
                .Select(entitlement => new AccessEntitlementDto(
                    ProductId: entitlement.ProductId,
                    HasAccess: entitlement.HasAccess,
                    AccessReason: MapAccessReason(entitlement.AccessReason),
                    AccessEndsAt: entitlement.AccessEndsAt
                ))
                .ToList()
        );

        return Results.Ok(response);
    }

    private static string GetAccessMetricStatus(IReadOnlyList<ProductAccessState> entitlements)
    {
        for (var i = 0; i < entitlements.Count; i++)
        {
            if (entitlements[i].HasAccess)
            {
                return "granted";
            }
        }

        return "denied";
    }

    private static string GetAccessMetricReason(IReadOnlyList<ProductAccessState> entitlements)
    {
        for (var i = 0; i < entitlements.Count; i++)
        {
            if (entitlements[i].HasAccess)
            {
                return MapAccessReason(entitlements[i].AccessReason);
            }
        }

        return entitlements.Count == 0
            ? "no_entitlements"
            : MapAccessReason(entitlements[0].AccessReason);
    }

    private static string MapAccessReason(AccessReason reason) => reason switch
    {
        AccessReason.ActiveSubscription => "active_subscription",
        AccessReason.TrialingSubscription => "trialing_subscription",
        AccessReason.CanceledSubscription => "canceled_subscription",
        AccessReason.PaymentFailedInGracePeriod => "payment_failed_in_grace_period",
        AccessReason.PaymentFailedPastGracePeriod => "payment_failed_past_grace_period",
        AccessReason.PausedSubscription => "paused_subscription",
        AccessReason.ExpiredSubscription => "expired_subscription",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unsupported access reason.")
    };
}
