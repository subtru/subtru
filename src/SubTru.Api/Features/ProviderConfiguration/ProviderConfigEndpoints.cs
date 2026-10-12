using Microsoft.AspNetCore.Http.HttpResults;
using System.Net.Mail;
using System.Text.RegularExpressions;
using SubTru.Api.Platform.Authentication;
using SubTru.Api.Platform.Http;
using SubTru.Api.Platform.Observability;
using SubTru.Infrastructure.Providers.Google;
using SubTru.Infrastructure.Persistence.Postgres.ProviderConfiguration;

namespace SubTru.Api.Features.ProviderConfiguration;

public static class ProviderConfigEndpoints
{
    private static readonly Regex AppleBundleIdRegex = new(
        "^[A-Za-z0-9][A-Za-z0-9_-]*(\\.[A-Za-z0-9][A-Za-z0-9_-]*)+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GooglePackageNameRegex = new(
        "^[A-Za-z][A-Za-z0-9_]*(\\.[A-Za-z][A-Za-z0-9_]*)+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static void MapProviderConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/projects/{projectId}/provider-configs");

        group.MapGet("/apple", GetAppleProviderConfigAsync);
        group.MapPut("/apple", UpsertAppleProviderConfigAsync);
        group.MapGet("/google", GetGoogleProviderConfigAsync);
        group.MapPut("/google", UpsertGoogleProviderConfigAsync);
    }

    private static async Task<Results<Ok<AppleProviderConfigDto>, JsonHttpResult<ApiErrorResponse>, NotFound<ApiErrorResponse>>> GetAppleProviderConfigAsync(
        Guid projectId,
        HttpContext ctx,
        IProviderConfigRepository repository,
        CancellationToken ct)
    {
        var permissionError = ManagementAuthorization.RequireAdmin(ctx, "Provider config management");
        if (permissionError is not null)
        {
            return permissionError;
        }

        var config = await repository.GetAppleProviderConfigAsync(projectId, ct);
        if (config is null)
        {
            return TypedResults.NotFound(new ApiErrorResponse(
                Error: "provider_config_not_found",
                Message: "Apple provider config was not found for this project."
            ));
        }

        return TypedResults.Ok(ToDto(config));
    }

    private static async Task<Results<Ok<AppleProviderConfigDto>, JsonHttpResult<ApiErrorResponse>, BadRequest<ApiErrorResponse>>> UpsertAppleProviderConfigAsync(
        Guid projectId,
        UpsertAppleProviderConfigRequest? request,
        HttpContext ctx,
        IProviderConfigRepository repository,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var permissionError = ManagementAuthorization.RequireAdmin(ctx, "Provider config management");
        if (permissionError is not null)
        {
            return permissionError;
        }

        if (request is null)
        {
            return TypedResults.BadRequest(new ApiErrorResponse(
                Error: "missing_request_body",
                Message: "Request body is required."
            ));
        }

        var bundleId = request.BundleId?.Trim();
        if (string.IsNullOrWhiteSpace(bundleId))
        {
            return TypedResults.BadRequest(new ApiErrorResponse(
                Error: "missing_bundle_id",
                Message: "Field 'bundleId' is required."
            ));
        }

        if (!AppleBundleIdRegex.IsMatch(bundleId))
        {
            return TypedResults.BadRequest(new ApiErrorResponse(
                Error: "invalid_bundle_id",
                Message: "Field 'bundleId' must be a dot-separated identifier like 'com.example.app'."
            ));
        }

        if (request.AppAppleId <= 0)
        {
            return TypedResults.BadRequest(new ApiErrorResponse(
                Error: "invalid_app_apple_id",
                Message: "Field 'appAppleId' must be a positive integer."
            ));
        }

        var saved = await repository.UpsertAppleProviderConfigAsync(
            projectId,
            new AppleProviderConfigWriteModel(
                BundleId: bundleId,
                AppAppleId: request.AppAppleId),
            ct);

        ProductEventLogging.LogProviderConfigUpserted(
            loggerFactory,
            projectId,
            "apple");

        return TypedResults.Ok(ToDto(saved));
    }

    private static AppleProviderConfigDto ToDto(AppleProviderConfigReadModel config) =>
        new(
            Provider: "apple",
            BundleId: config.BundleId,
            AppAppleId: config.AppAppleId,
            CreatedAt: config.CreatedAt,
            UpdatedAt: config.UpdatedAt
        );

    private static async Task<Results<Ok<GoogleProviderConfigDto>, JsonHttpResult<ApiErrorResponse>, NotFound<ApiErrorResponse>>> GetGoogleProviderConfigAsync(
        Guid projectId,
        HttpContext ctx,
        IProviderConfigRepository repository,
        CancellationToken ct)
    {
        var permissionError = ManagementAuthorization.RequireAdmin(ctx, "Provider config management");
        if (permissionError is not null)
        {
            return permissionError;
        }

        var config = await repository.GetGoogleProviderConfigAsync(projectId, ct);
        if (config is null)
        {
            return TypedResults.NotFound(new ApiErrorResponse(
                Error: "provider_config_not_found",
                Message: "Google provider config was not found for this project."
            ));
        }

        return TypedResults.Ok(ToDto(config));
    }

    private static async Task<Results<Ok<GoogleProviderConfigDto>, JsonHttpResult<ApiErrorResponse>, BadRequest<ApiErrorResponse>>> UpsertGoogleProviderConfigAsync(
        Guid projectId,
        UpsertGoogleProviderConfigRequest? request,
        HttpContext ctx,
        IProviderConfigRepository repository,
        IGoogleServiceAccountValidator serviceAccountValidator,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var permissionError = ManagementAuthorization.RequireAdmin(ctx, "Provider config management");
        if (permissionError is not null)
        {
            return permissionError;
        }

        if (request is null)
        {
            return TypedResults.BadRequest(new ApiErrorResponse(
                Error: "missing_request_body",
                Message: "Request body is required."
            ));
        }

        var packageName = request.PackageName?.Trim();
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return TypedResults.BadRequest(new ApiErrorResponse(
                Error: "missing_package_name",
                Message: "Field 'packageName' is required."
            ));
        }

        if (!GooglePackageNameRegex.IsMatch(packageName))
        {
            return TypedResults.BadRequest(new ApiErrorResponse(
                Error: "invalid_package_name",
                Message: "Field 'packageName' must be a dot-separated Android package name like 'com.example.app'."
            ));
        }

        var pubSubAuthServiceAccountEmail = request.PubSubAuthServiceAccountEmail?.Trim();
        if (string.IsNullOrWhiteSpace(pubSubAuthServiceAccountEmail))
        {
            return TypedResults.BadRequest(new ApiErrorResponse(
                Error: "missing_pubsub_auth_service_account_email",
                Message: "Field 'pubSubAuthServiceAccountEmail' is required."
            ));
        }

        if (!IsValidEmail(pubSubAuthServiceAccountEmail))
        {
            return TypedResults.BadRequest(new ApiErrorResponse(
                Error: "invalid_pubsub_auth_service_account_email",
                Message: "Field 'pubSubAuthServiceAccountEmail' must be a valid email address."
            ));
        }

        var serviceAccountJson = request.ServiceAccountJson?.Trim();
        ValidatedGoogleServiceAccount? validatedServiceAccount = null;

        if (!string.IsNullOrWhiteSpace(serviceAccountJson))
        {
            try
            {
                validatedServiceAccount = await serviceAccountValidator.ValidateAsync(serviceAccountJson, ct);
            }
            catch (GoogleServiceAccountValidationException ex)
            {
                if (ex.IsTransient)
                {
                    return TypedResults.Json(
                        new ApiErrorResponse(
                            Error: ex.ErrorCode,
                            Message: ex.Message),
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                return TypedResults.BadRequest(new ApiErrorResponse(
                    Error: ex.ErrorCode,
                    Message: ex.Message
                ));
            }
        }

        var saved = await repository.UpsertGoogleProviderConfigAsync(
            projectId,
            new GoogleProviderConfigWriteModel(
                PackageName: packageName,
                PubSubAuthServiceAccountEmail: pubSubAuthServiceAccountEmail,
                ServiceAccountJson: serviceAccountJson),
            validatedServiceAccount,
            ct);

        ProductEventLogging.LogGoogleProviderConfigUpserted(
            loggerFactory,
            projectId,
            !string.IsNullOrWhiteSpace(serviceAccountJson));

        return TypedResults.Ok(ToDto(saved));
    }

    private static GoogleProviderConfigDto ToDto(GoogleProviderConfigReadModel config) =>
        new(
            Provider: "google",
            PackageName: config.PackageName,
            PubSubAuthServiceAccountEmail: config.PubSubAuthServiceAccountEmail,
            HasServiceAccountCredentials: config.HasServiceAccountCredentials,
            ServiceAccountClientEmail: config.ServiceAccountClientEmail,
            ServiceAccountKeyId: config.ServiceAccountKeyId,
            ServiceAccountCreatedAt: config.ServiceAccountCreatedAt,
            ServiceAccountLastValidatedAt: config.ServiceAccountLastValidatedAt,
            CreatedAt: config.CreatedAt,
            UpdatedAt: config.UpdatedAt
        );

    private static bool IsValidEmail(string value)
    {
        try
        {
            _ = new MailAddress(value);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
