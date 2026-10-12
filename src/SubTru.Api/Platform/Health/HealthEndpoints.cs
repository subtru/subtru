using SubTru.Api.Platform.Health;

namespace SubTru.Api.Platform.Health;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/health/ready", async (
            IApiReadinessCheck readinessCheck,
            CancellationToken ct) =>
        {
            var result = await readinessCheck.CheckAsync(ct);

            return result.IsReady
                ? Results.Ok(new { status = "ready" })
                : Results.Json(
                    new { status = "not_ready" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        });
    }
}
