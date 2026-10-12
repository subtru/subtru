using System.Net;
using Microsoft.AspNetCore.Http.HttpResults;
using SubTru.Api.Platform.Authentication;
using SubTru.Api.Platform.Http;
using SubTru.Api.Platform.Observability;
using SubTru.Domain.Subscriptions;
using SubTru.Infrastructure.Providers.Google;
using SubTru.Infrastructure.Persistence.Postgres.Subscriptions;
using SubTru.Infrastructure.Persistence.Postgres.Google;
using SubTru.Infrastructure.Persistence.Postgres.ProviderConfiguration;

namespace SubTru.Api.Features.Webhooks.Google;

public static class GooglePurchaseRemediationEndpoints
{
    private const string ProviderName = "google";
    public static void MapGooglePurchaseRemediationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(
            "/v1/projects/{projectId}/google/purchases/{purchaseToken}:bind-and-acknowledge",
            BindAndAcknowledgeAsync);
    }

    private static async Task<Results<Ok<BindGooglePurchaseResponse>, BadRequest<ApiErrorResponse>, NotFound<ApiErrorResponse>, JsonHttpResult<ApiErrorResponse>>> BindAndAcknowledgeAsync(
        Guid projectId,
        string purchaseToken,
        string? environment,
        BindGooglePurchaseRequest? request,
        HttpContext ctx,
        IProviderConfigRepository providerConfigRepository,
        IGooglePlaySubscriptionClient googlePlaySubscriptionClient,
        IGoogleSubscriptionWriteRepository googleSubscriptionWriteRepository,
        IGooglePurchaseRemediationAuditRepository remediationAuditRepository,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("SubTru.GooglePurchaseRemediation");

        var permissionError = ManagementAuthorization.RequireAdmin(
            ctx,
            "Google purchase remediation");
        if (permissionError is not null)
        {
            return permissionError;
        }

        if (!RequestEnvironment.TryResolve(ctx, environment, out var resolvedEnvironment, out var environmentError))
        {
            return TypedResults.BadRequest(environmentError!);
        }

        const string actor = "installation_admin";
        if (request is null)
        {
            return TypedResults.BadRequest(new ApiErrorResponse(
                Error: "missing_request_body",
                Message: "Request body is required."
            ));
        }

        var externalUserId = request.ExternalUserId?.Trim();
        if (string.IsNullOrWhiteSpace(externalUserId))
        {
            return TypedResults.BadRequest(new ApiErrorResponse(
                Error: "missing_external_user_id",
                Message: "Field 'externalUserId' is required."
            ));
        }

        var requestedPurchaseToken = purchaseToken?.Trim();
        if (string.IsNullOrWhiteSpace(requestedPurchaseToken))
        {
            return TypedResults.BadRequest(new ApiErrorResponse(
                Error: "missing_purchase_token",
                Message: "Path parameter 'purchaseToken' is required."
            ));
        }

        GoogleProviderConfigReadModel? config;
        GoogleProviderCredentialReadModel? credential;
        try
        {
            config = await providerConfigRepository.GetGoogleProviderConfigAsync(projectId, ct);
            credential = await providerConfigRepository.GetGoogleProviderCredentialAsync(projectId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Google remediation provider config lookup failed. project_id={ProjectId}", projectId);
            return TypedResults.Json(
                new ApiErrorResponse("google_provider_config_lookup_failed", "Google provider config lookup failed."),
                statusCode: StatusCodes.Status500InternalServerError);
        }

        if (config is null)
        {
            return TypedResults.Json(
                new ApiErrorResponse("missing_google_provider_config", "Google provider config is missing for this project."),
                statusCode: StatusCodes.Status409Conflict);
        }

        if (credential is null)
        {
            return TypedResults.Json(
                new ApiErrorResponse("missing_google_provider_credentials", "Google provider credentials are missing for this project."),
                statusCode: StatusCodes.Status409Conflict);
        }

        GooglePlaySubscriptionPurchase purchase;
        string effectivePurchaseToken = requestedPurchaseToken;

        try
        {
            purchase = await googlePlaySubscriptionClient.GetSubscriptionAsync(
                config.PackageName,
                requestedPurchaseToken,
                credential.ServiceAccountJson,
                ct);

            if (purchase.SubscriptionState == GoogleSubscriptionMapper.SubscriptionStatePendingPurchaseCanceled
                && !string.IsNullOrWhiteSpace(purchase.LinkedPurchaseToken))
            {
                effectivePurchaseToken = purchase.LinkedPurchaseToken.Trim();
                purchase = await googlePlaySubscriptionClient.GetSubscriptionAsync(
                    config.PackageName,
                    effectivePurchaseToken,
                    credential.ServiceAccountJson,
                    ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (GooglePlayApiException ex)
        {
            logger.LogWarning(ex,
                "Google remediation subscription fetch failed. project_id={ProjectId} requested_purchase_token_fingerprint={RequestedPurchaseTokenFingerprint} error_code={ErrorCode}",
                projectId,
                LogSanitizer.Fingerprint(requestedPurchaseToken),
                ex.ErrorCode);

            return ClassifyGoogleApiFailure(ex, fetchOperation: true);
        }

        var purchaseEnvironment = purchase.TestPurchase is null
            ? SubscriptionEnvironment.Production
            : SubscriptionEnvironment.Test;
        if (!string.Equals(purchaseEnvironment, resolvedEnvironment, StringComparison.Ordinal))
        {
            return TypedResults.BadRequest(new ApiErrorResponse(
                "environment_mismatch",
                "Google purchase testPurchase state does not match the requested environment."));
        }

        var mapping = GoogleSubscriptionMapper.Map(effectivePurchaseToken, purchase);
        if (mapping.Kind == GoogleSubscriptionMappingKind.Unsupported || mapping.Data is null)
        {
            return TypedResults.Json(
                new ApiErrorResponse(
                    mapping.ErrorCode ?? "google_purchase_not_remediable",
                    mapping.ErrorMessage ?? "Google purchase is not remediable by this endpoint."),
                statusCode: StatusCodes.Status409Conflict);
        }

        if (!mapping.Data.ShouldAcknowledge)
        {
            return TypedResults.Json(
                new ApiErrorResponse(
                    "google_purchase_not_remediable",
                    "Google purchase is not eligible for bind-and-acknowledge remediation."),
                statusCode: StatusCodes.Status409Conflict);
        }

        if (purchase.AcknowledgementState != GoogleSubscriptionMapper.AcknowledgementStatePending)
        {
            return TypedResults.Json(
                new ApiErrorResponse(
                    "google_purchase_already_acknowledged",
                    "Google purchase has already been acknowledged."),
                statusCode: StatusCodes.Status409Conflict);
        }

        var normalized = new NormalizedSubscription(
            ProjectId: projectId,
            Environment: resolvedEnvironment,
            ExternalUserId: externalUserId,
            Provider: ProviderName,
            ProviderSubscriptionId: mapping.Data.ProviderSubscriptionId,
            ProductId: mapping.Data.ProductId,
            PriceId: mapping.Data.PriceId,
            Status: mapping.Data.Status,
            ProviderStatus: mapping.Data.ProviderStatus,
            CurrentPeriodEnd: mapping.Data.CurrentPeriodEnd,
            AccessEndsAt: mapping.Data.AccessEndsAt,
            CancelAtPeriodEnd: mapping.Data.CancelAtPeriodEnd);

        try
        {
            await googleSubscriptionWriteRepository.UpsertGoogleManualAsync(
                normalized,
                mapping.Data.LinkedPurchaseToken,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Google remediation durable write failed. project_id={ProjectId} effective_purchase_token_fingerprint={EffectivePurchaseTokenFingerprint}",
                projectId,
                LogSanitizer.Fingerprint(effectivePurchaseToken));

            return TypedResults.Json(
                new ApiErrorResponse("processing_failed", "Failed to persist remediated Google subscription state."),
                statusCode: StatusCodes.Status500InternalServerError);
        }

        try
        {
            await googlePlaySubscriptionClient.AcknowledgeAsync(
                config.PackageName,
                effectivePurchaseToken,
                credential.ServiceAccountJson,
                mapping.Data.AcknowledgeSubscriptionId,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (GooglePlayApiException ex)
        {
            logger.LogWarning(ex,
                "Google remediation acknowledgement failed. project_id={ProjectId} effective_purchase_token_fingerprint={EffectivePurchaseTokenFingerprint} error_code={ErrorCode}",
                projectId,
                LogSanitizer.Fingerprint(effectivePurchaseToken),
                ex.ErrorCode);

            return ClassifyGoogleApiFailure(ex, fetchOperation: false);
        }

        var acknowledgedAt = DateTimeOffset.UtcNow;

        try
        {
            await remediationAuditRepository.InsertAsync(
                new GooglePurchaseRemediationAuditWriteModel(
                    ProjectId: projectId,
                    Environment: resolvedEnvironment,
                    RequestedPurchaseToken: requestedPurchaseToken,
                    EffectivePurchaseToken: effectivePurchaseToken,
                    LinkedPurchaseToken: mapping.Data.LinkedPurchaseToken,
                    ExternalUserId: externalUserId,
                    Actor: actor,
                    Status: mapping.Data.Status,
                    ProviderStatus: mapping.Data.ProviderStatus,
                    CurrentPeriodEnd: mapping.Data.CurrentPeriodEnd,
                    AcknowledgedAt: acknowledgedAt),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Google remediation audit write failed after acknowledgement. project_id={ProjectId} effective_purchase_token_fingerprint={EffectivePurchaseTokenFingerprint}",
                projectId,
                LogSanitizer.Fingerprint(effectivePurchaseToken));

            return TypedResults.Json(
                new ApiErrorResponse("google_purchase_remediation_audit_failed", "Google purchase was acknowledged but the audit record could not be persisted."),
                statusCode: StatusCodes.Status500InternalServerError);
        }

        return TypedResults.Ok(new BindGooglePurchaseResponse(
            RequestedPurchaseToken: requestedPurchaseToken,
            EffectivePurchaseToken: effectivePurchaseToken,
            Environment: resolvedEnvironment,
            ExternalUserId: externalUserId,
            Status: mapping.Data.Status,
            ProviderStatus: mapping.Data.ProviderStatus,
            CurrentPeriodEnd: mapping.Data.CurrentPeriodEnd,
            AcknowledgedAt: acknowledgedAt
        ));
    }

    private static JsonHttpResult<ApiErrorResponse> ClassifyGoogleApiFailure(
        GooglePlayApiException exception,
        bool fetchOperation)
    {
        if (exception.StatusCode == HttpStatusCode.NotFound || (int?)exception.StatusCode == 410)
        {
            return TypedResults.Json(
                new ApiErrorResponse(
                    fetchOperation ? "google_subscription_not_found" : "google_subscription_acknowledge_not_found",
                    fetchOperation
                        ? "Google Play subscription token was not found."
                        : "Google Play subscription token could not be acknowledged because it was not found."),
                statusCode: StatusCodes.Status404NotFound);
        }

        if (exception.IsTransient)
        {
            return TypedResults.Json(
                new ApiErrorResponse(exception.ErrorCode, exception.Message),
                statusCode: StatusCodes.Status500InternalServerError);
        }

        return TypedResults.Json(
            new ApiErrorResponse(
                exception.ErrorCode,
                exception.Message),
            statusCode: StatusCodes.Status409Conflict);
    }

    private sealed record BindGooglePurchaseRequest(
        string? ExternalUserId
    );

    private sealed record BindGooglePurchaseResponse(
        string RequestedPurchaseToken,
        string EffectivePurchaseToken,
        string Environment,
        string ExternalUserId,
        string Status,
        string? ProviderStatus,
        DateTimeOffset CurrentPeriodEnd,
        DateTimeOffset AcknowledgedAt
    );
}
