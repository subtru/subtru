using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Options;
using SubTru.Api.Features.Webhooks.Shared;
using SubTru.Api.Platform.Http;
using SubTru.Api.Platform.Observability;
using SubTru.Infrastructure.Providers.Apple;

namespace SubTru.Api.Bootstrap;

internal static class ApplicationPipelineExtensions
{
    public static WebApplication ValidateSubTruStartup(this WebApplication app)
    {
        _ = app.Services.GetRequiredService<IOptions<WebhookUrlOptions>>().Value;

        var options = app.Services.GetRequiredService<IOptions<AppleVerificationOptions>>().Value;
        var trustStore = app.Services.GetRequiredService<AppleTrustStore>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("SubTru.AppleTrustStore");

        logger.LogInformation(
            "Loaded Apple trust store. root_count={RootCount} online_revocation_enabled={EnableOnlineRevocationChecks} online_revocation_timeout={OnlineRevocationTimeout} root_directory={RootCertificateDirectory}",
            trustStore.RootMetadata.Count,
            options.EnableOnlineRevocationChecks,
            options.OnlineRevocationTimeout,
            Path.GetFullPath(options.RootCertificateDirectory!));

        foreach (var root in trustStore.RootMetadata)
        {
            logger.LogInformation(
                "Loaded Apple trust root. subject={Subject} sha256_fingerprint={Sha256Fingerprint} not_after={NotAfter}",
                root.Subject,
                root.Sha256Fingerprint,
                root.NotAfterUtc);
        }

        return app;
    }

    public static WebApplication UseSubTruPipeline(this WebApplication app)
    {
        app.UseMiddleware<RequestCorrelationMiddleware>();

        app.UseExceptionHandler(exceptionApp =>
        {
            exceptionApp.Run(async context =>
            {
                var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
                context.Response.ContentType = "application/json";

                if (exception is BadHttpRequestException badRequestException)
                {
                    context.Response.StatusCode = badRequestException.StatusCode;
                    await context.Response.WriteAsJsonAsync(new ApiErrorResponse(
                        Error: "invalid_request",
                        Message: "Invalid request body or parameters. Ensure JSON payload is valid."));
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("GlobalExceptionHandler");

                if (exception is not null)
                {
                    logger.LogError(exception, "Unhandled exception while processing request.");
                }

                await context.Response.WriteAsJsonAsync(new ApiErrorResponse(
                    Error: "internal_server_error",
                    Message: "An unexpected error occurred."));
            });
        });

        app.UseStatusCodePages(async statusCodeContext =>
        {
            var response = statusCodeContext.HttpContext.Response;
            if (response.HasStarted || !string.IsNullOrWhiteSpace(response.ContentType))
            {
                return;
            }

            response.ContentType = "application/json";
            var payload = response.StatusCode switch
            {
                StatusCodes.Status400BadRequest => new ApiErrorResponse("bad_request", "The request is invalid."),
                StatusCodes.Status401Unauthorized => new ApiErrorResponse("unauthorized", "Authentication is required or failed."),
                StatusCodes.Status403Forbidden => new ApiErrorResponse("forbidden", "You do not have permission to perform this action."),
                StatusCodes.Status404NotFound => new ApiErrorResponse("not_found", "The requested resource was not found."),
                StatusCodes.Status405MethodNotAllowed => new ApiErrorResponse("method_not_allowed", "The HTTP method is not allowed for this endpoint."),
                StatusCodes.Status429TooManyRequests => new ApiErrorResponse("too_many_requests", "Rate limit exceeded."),
                _ when response.StatusCode >= 500 => new ApiErrorResponse("internal_server_error", "An unexpected error occurred."),
                _ => null
            };

            if (payload is not null)
            {
                await response.WriteAsJsonAsync(payload);
            }
        });

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        return app;
    }
}
