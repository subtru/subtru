using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using SubTru.Api.Features.Webhooks.Shared;
using SubTru.Api.Platform.Authentication;
using SubTru.Api.Platform.Http;
using SubTru.Api.Platform.Observability;
using SubTru.Domain.Subscriptions;
using SubTru.Infrastructure.Persistence.Postgres.WebhookKeys;

namespace SubTru.Api.Features.Webhooks.Keys;

public static class WebhookKeysEndpoints
{
    private static readonly HashSet<string> ValidProviders = ["stripe", "apple", "google"];

    public static void MapWebhookKeysEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/projects/{projectId}/webhook-keys");

        group.MapGet("", ListWebhookKeysAsync);
        group.MapPost("", CreateWebhookKeyAsync);
        group.MapDelete("/{id}", DeleteWebhookKeyAsync);
        group.MapPatch("/{id}", UpdateSigningSecretAsync);
    }

    private static async Task<Results<Ok<WebhookKeyListResponse>, JsonHttpResult<ApiErrorResponse>>> ListWebhookKeysAsync(
        Guid projectId,
        HttpContext ctx,
        IWebhookKeyManagementRepository repository,
        CancellationToken ct)
    {
        var permissionError = ManagementAuthorization.RequireAdmin(ctx, "Webhook key management");
        if (permissionError is not null)
        {
            return permissionError;
        }

        var items = await repository.ListByProjectAsync(projectId, ct);

        var response = new WebhookKeyListResponse(
            Items: items.Select(x => new WebhookKeyDto(
                Id: x.Id,
                KeyId: x.KeyId,
                Provider: x.Provider,
                Environment: x.Environment,
                HasSigningSecret: x.HasSigningSecret,
                IsActive: x.IsActive,
                CreatedAt: x.CreatedAt,
                UpdatedAt: x.UpdatedAt
            )).ToList()
        );

        return TypedResults.Ok(response);
    }

    private static async Task<Results<Created<CreatedWebhookKeyResponse>, JsonHttpResult<ApiErrorResponse>, BadRequest<ApiErrorResponse>>> CreateWebhookKeyAsync(
        Guid projectId,
        CreateWebhookKeyRequest? request,
        HttpContext ctx,
        IWebhookKeyManagementRepository repository,
        IOptions<WebhookUrlOptions> webhookUrlOptions,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var permissionError = ManagementAuthorization.RequireAdmin(ctx, "Webhook key management");
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

        if (string.IsNullOrWhiteSpace(request.Provider))
        {
            return TypedResults.BadRequest(new ApiErrorResponse(
                Error: "missing_provider",
                Message: "Field 'provider' is required."
            ));
        }

        var provider = request.Provider.Trim().ToLowerInvariant();

        if (!ValidProviders.Contains(provider))
        {
            return TypedResults.BadRequest(new ApiErrorResponse(
                Error: "invalid_provider",
                Message: "Field 'provider' must be one of: stripe, apple, google."
            ));
        }

        var environment = request.Environment?.Trim().ToLowerInvariant() ?? SubscriptionEnvironment.Production;
        if (!SubscriptionEnvironment.IsValid(environment))
        {
            return TypedResults.BadRequest(new ApiErrorResponse(
                Error: "invalid_environment",
                Message: "Field 'environment' must be one of: production, test."
            ));
        }

        var created = await repository.CreateAsync(
            projectId,
            provider,
            environment,
            request.SigningSecret?.Trim(),
            ct);

        var webhookUrl = WebhookUrlBuilder.Build(webhookUrlOptions.Value, provider, created.WebhookKey);

        var response = new CreatedWebhookKeyResponse(
            Id: created.Id,
            KeyId: created.KeyId,
            WebhookKey: created.WebhookKey,
            Provider: created.Provider,
            Environment: created.Environment,
            WebhookUrl: webhookUrl,
            CreatedAt: created.CreatedAt
        );

        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.Pragma = "no-cache";

        ProductEventLogging.LogWebhookKeyCreated(
            loggerFactory,
            projectId,
            created.Id,
            created.KeyId,
            created.Provider,
            created.Environment);

        return TypedResults.Created($"/v1/projects/{projectId}/webhook-keys/{created.Id}", response);
    }

    private static async Task<Results<Ok<DeleteWebhookKeyResponse>, JsonHttpResult<ApiErrorResponse>, NotFound<ApiErrorResponse>, BadRequest<ApiErrorResponse>>> DeleteWebhookKeyAsync(
        Guid projectId,
        string id,
        HttpContext ctx,
        IWebhookKeyManagementRepository repository,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var permissionError = ManagementAuthorization.RequireAdmin(ctx, "Webhook key management");
        if (permissionError is not null)
        {
            return permissionError;
        }

        if (!Guid.TryParse(id, out var webhookKeyId))
        {
            return TypedResults.BadRequest(new ApiErrorResponse(
                Error: "invalid_webhook_key_id",
                Message: "Webhook key id must be a valid UUID."
            ));
        }

        var deactivated = await repository.DeactivateAsync(projectId, webhookKeyId, ct);
        if (deactivated is null)
        {
            return TypedResults.NotFound(new ApiErrorResponse(
                Error: "webhook_key_not_found",
                Message: "Webhook key not found or already deactivated."
            ));
        }

        var response = new DeleteWebhookKeyResponse(
            Id: deactivated.Id,
            IsActive: deactivated.IsActive,
            Success: true
        );

        ProductEventLogging.LogWebhookKeyDeactivated(
            loggerFactory,
            projectId,
            deactivated.Id);

        return TypedResults.Ok(response);
    }

    private static async Task<Results<Ok<UpdateSigningSecretResponse>, JsonHttpResult<ApiErrorResponse>, NotFound<ApiErrorResponse>, BadRequest<ApiErrorResponse>>> UpdateSigningSecretAsync(
        Guid projectId,
        string id,
        UpdateSigningSecretRequest? request,
        HttpContext ctx,
        IWebhookKeyManagementRepository repository,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var permissionError = ManagementAuthorization.RequireAdmin(ctx, "Webhook key management");
        if (permissionError is not null)
        {
            return permissionError;
        }

        if (!Guid.TryParse(id, out var webhookKeyId))
        {
            return TypedResults.BadRequest(new ApiErrorResponse(
                Error: "invalid_webhook_key_id",
                Message: "Webhook key id must be a valid UUID."
            ));
        }

        if (request is null || string.IsNullOrWhiteSpace(request.SigningSecret))
        {
            return TypedResults.BadRequest(new ApiErrorResponse(
                Error: "missing_signing_secret",
                Message: "Field 'signingSecret' is required."
            ));
        }

        var updated = await repository.UpdateSigningSecretAsync(
            projectId,
            webhookKeyId,
            request.SigningSecret.Trim(),
            ct);

        if (!updated)
        {
            return TypedResults.NotFound(new ApiErrorResponse(
                Error: "webhook_key_not_found",
                Message: "Active webhook key not found."
            ));
        }

        ProductEventLogging.LogWebhookKeySecretRotated(
            loggerFactory,
            projectId,
            webhookKeyId);

        return TypedResults.Ok(new UpdateSigningSecretResponse(Success: true));
    }
}
