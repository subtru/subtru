using Microsoft.AspNetCore.Http.HttpResults;
using SubTru.Api.Platform.Authentication;
using SubTru.Api.Platform.Http;
using SubTru.Api.Platform.Observability;
using SubTru.Domain.Subscriptions;
using SubTru.Infrastructure.Persistence.Postgres.ApiKeys;

namespace SubTru.Api.Features.ApiKeys;

public static class ApiKeysEndpoints
{
    public static void MapApiKeysEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/projects/{projectId}/api-keys");

        group.MapGet("", ListApiKeysAsync);
        group.MapPost("", CreateApiKeyAsync);
        group.MapDelete("/{id}", DeleteApiKeyAsync);
    }

    private static async Task<Results<Ok<ApiKeyListResponse>, JsonHttpResult<ApiErrorResponse>>> ListApiKeysAsync(
        Guid projectId,
        HttpContext ctx,
        IApiKeyManagementRepository repository,
        CancellationToken ct)
    {
        var permissionError = ManagementAuthorization.RequireAdmin(ctx, "API key management");
        if (permissionError is not null)
        {
            return permissionError;
        }

        var items = await repository.ListByProjectAsync(projectId, ct);

        var response = new ApiKeyListResponse(
            Items: items.Select(x => new ApiKeyDto(
                Id: x.Id,
                Name: x.Name,
                KeyId: x.KeyId,
                Environment: x.Environment,
                CreatedAt: x.CreatedAt,
                LastUsedAt: x.LastUsedAt,
                ExpiresAt: x.ExpiresAt,
                IsRevoked: x.IsRevoked,
                RevokedAt: x.RevokedAt
            )).ToList(),
            NextCursor: null
        );

        return TypedResults.Ok(response);
    }

    private static async Task<Results<Created<CreatedApiKeyResponse>, JsonHttpResult<ApiErrorResponse>, BadRequest<ApiErrorResponse>>> CreateApiKeyAsync(
        Guid projectId,
        CreateApiKeyRequest? request,
        HttpContext ctx,
        IApiKeyManagementRepository repository,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var permissionError = ManagementAuthorization.RequireAdmin(ctx, "API key management");
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

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return TypedResults.BadRequest(new ApiErrorResponse(
                Error: "missing_name",
                Message: "Field 'name' is required."
            ));
        }

        var trimmedName = request.Name.Trim();

        var environment = request.Environment?.Trim().ToLowerInvariant() ?? SubscriptionEnvironment.Production;
        if (!SubscriptionEnvironment.IsValid(environment))
        {
            return TypedResults.BadRequest(new ApiErrorResponse(
                Error: "invalid_environment",
                Message: "Field 'environment' must be one of: production, test."
            ));
        }

        if (trimmedName.Length > 200)
        {
            return TypedResults.BadRequest(new ApiErrorResponse(
                Error: "invalid_name",
                Message: "Field 'name' must be 200 characters or fewer."
            ));
        }

        if (request.ExpiresAt.HasValue && request.ExpiresAt.Value <= DateTimeOffset.UtcNow)
        {
            return TypedResults.BadRequest(new ApiErrorResponse(
                Error: "invalid_expires_at",
                Message: "Field 'expiresAt' must be in the future."
            ));
        }

        var created = await repository.CreateAsync(
            projectId,
            trimmedName,
            environment,
            request.ExpiresAt,
            ct);

        var response = new CreatedApiKeyResponse(
            Id: created.Id,
            Name: created.Name,
            KeyId: created.KeyId,
            ApiKey: created.ApiKey,
            Environment: created.Environment,
            CreatedAt: created.CreatedAt
        );

        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.Pragma = "no-cache";

        ProductEventLogging.LogApiKeyCreated(
            loggerFactory,
            projectId,
            created.Id,
            created.KeyId,
            created.Environment);

        return TypedResults.Created($"/v1/projects/{projectId}/api-keys/{created.Id}", response);
    }

    private static async Task<Results<Ok<DeleteApiKeyResponse>, JsonHttpResult<ApiErrorResponse>, NotFound<ApiErrorResponse>, BadRequest<ApiErrorResponse>>> DeleteApiKeyAsync(
        Guid projectId,
        string id,
        HttpContext ctx,
        IApiKeyManagementRepository repository,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var permissionError = ManagementAuthorization.RequireAdmin(ctx, "API key management");
        if (permissionError is not null)
        {
            return permissionError;
        }

        if (!Guid.TryParse(id, out var apiKeyId))
        {
            return TypedResults.BadRequest(new ApiErrorResponse(
                Error: "invalid_api_key_id",
                Message: "API key id must be a valid UUID."
            ));
        }

        var revoked = await repository.RevokeAsync(projectId, apiKeyId, ct);
        if (revoked is null)
        {
            return TypedResults.NotFound(new ApiErrorResponse(
                Error: "api_key_not_found",
                Message: "API key not found."
            ));
        }

        var response = new DeleteApiKeyResponse(
            Id: revoked.Id,
            IsRevoked: revoked.IsRevoked,
            RevokedAt: revoked.RevokedAt,
            Success: true
        );

        ProductEventLogging.LogApiKeyRevoked(
            loggerFactory,
            projectId,
            revoked.Id);

        return TypedResults.Ok(response);
    }
}
