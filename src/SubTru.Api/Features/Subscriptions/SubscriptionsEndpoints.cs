using SubTru.Api.Platform.Http;
using SubTru.Api.Platform.Authentication;
using SubTru.Infrastructure.Persistence.Postgres.Subscriptions;

namespace SubTru.Api.Features.Subscriptions;

public static class SubscriptionsEndpoints
{
    public static void MapSubscriptionEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/projects/{projectId}/users/{externalUserId}/subscriptions", async (
            Guid projectId,
            string externalUserId,
            string? environment,
            HttpContext ctx,
            ISubscriptionReadRepository repo,
            CancellationToken ct) => await GetSubscriptionsAsync(projectId, externalUserId, environment, ctx, repo, ct));
    }

    private static async Task<IResult> GetSubscriptionsAsync(
        Guid projectId,
        string? externalUserId,
        string? requestedEnvironment,
        HttpContext ctx,
        ISubscriptionReadRepository repo,
        CancellationToken ct)
    {
        var permissionError = ManagementAuthorization.RequireAdmin(ctx, "Subscription detail access");
        if (permissionError is not null)
        {
            return permissionError;
        }

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

        var response = new GetSubscriptionsResponse(
            Items: subs.Select(s => new SubscriptionDto(
                Provider: s.Provider,
                ProviderSubscriptionId: s.ProviderSubscriptionId,
                Environment: s.Environment,
                ProductId: s.ProductId,
                PriceId: s.PriceId,
                Status: s.Status,
                ProviderStatus: s.ProviderStatus,
                CurrentPeriodEnd: s.CurrentPeriodEnd,
                AccessEndsAt: s.AccessEndsAt
            )).ToList(),
            NextCursor: null
        );

        return Results.Ok(response);
    }
}
