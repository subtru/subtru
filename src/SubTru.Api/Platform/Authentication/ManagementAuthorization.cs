using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using SubTru.Api.Platform.Http;

namespace SubTru.Api.Platform.Authentication;

internal static class ManagementAuthorization
{
    public static JsonHttpResult<ApiErrorResponse>? RequireAdmin(
        HttpContext context,
        string capability)
    {
        if (context.GetRequiredProjectContext().IsAdminAuth)
        {
            return null;
        }

        return TypedResults.Json(
            new ApiErrorResponse(
                "forbidden",
                $"{capability} requires the installation admin API key."),
            statusCode: StatusCodes.Status403Forbidden);
    }
}
