using Microsoft.AspNetCore.Http.HttpResults;
using SubTru.Api.Platform.Http;
using SubTru.Api.Platform.Authentication;
using SubTru.Infrastructure.Persistence.Postgres.Projects;

namespace SubTru.Api.Features.Projects;

public static class ProjectSummaryEndpoints
{
    public static void MapProjectSummaryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/projects/{projectId}/summary", GetProjectSummaryAsync);
    }

    private static async Task<Results<Ok<GetProjectSummaryResponse>, JsonHttpResult<ApiErrorResponse>>> GetProjectSummaryAsync(
        Guid projectId,
        string? environment,
        HttpContext ctx,
        IProjectSummaryRepository repository,
        CancellationToken ct)
    {
        var permissionError = ManagementAuthorization.RequireAdmin(ctx, "Project summary access");
        if (permissionError is not null)
        {
            return permissionError;
        }

        if (!RequestEnvironment.TryResolve(ctx, environment, out var resolvedEnvironment, out var environmentError))
        {
            return TypedResults.Json(environmentError, statusCode: StatusCodes.Status400BadRequest);
        }

        var summary = await repository.GetSummaryAsync(projectId, resolvedEnvironment, ct);

        var response = new GetProjectSummaryResponse(
            UsersWithAccess: summary.UsersWithAccess,
            ActiveSubscriptions: summary.ActiveSubscriptions,
            ExpiredSubscriptions: summary.ExpiredSubscriptions,
            LastWebhookReceivedAt: summary.LastWebhookReceivedAt
        );

        return TypedResults.Ok(response);
    }
}
