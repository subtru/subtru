using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;
using SubTru.Api.Platform.Http;
using SubTru.Api.Platform.Authentication;
using SubTru.Infrastructure.Persistence.Postgres.WebhookEvents;
using System.Text;

namespace SubTru.Api.Features.Webhooks.Events;

public static class WebhookEventsEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 100;

    public static void MapWebhookEventsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/projects/{projectId}/webhook-events", GetWebhookEventsAsync);
    }

    private static async Task<Results<Ok<GetWebhookEventsResponse>, BadRequest<ApiErrorResponse>, JsonHttpResult<ApiErrorResponse>>> GetWebhookEventsAsync(
        Guid projectId,
        HttpContext ctx,
        IWebhookEventLogRepository repository,
        string? environment,
        int? limit,
        string? cursor,
        string? provider,
        string? status,
        CancellationToken ct)
    {
        var permissionError = ManagementAuthorization.RequireAdmin(ctx, "Webhook event access");
        if (permissionError is not null)
        {
            return permissionError;
        }

        string? resolvedProvider = null;
        if (!string.IsNullOrWhiteSpace(provider))
        {
            resolvedProvider = provider.Trim().ToLowerInvariant();
            if (resolvedProvider is not ("stripe" or "apple" or "google"))
            {
                return TypedResults.BadRequest(new ApiErrorResponse(
                    Error: "invalid_provider",
                    Message: "Query parameter 'provider' must be one of 'stripe', 'apple', or 'google'."
                ));
            }
        }

        WebhookEventStatusFilter? resolvedStatusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            resolvedStatusFilter = status.Trim().ToLowerInvariant() switch
            {
                "failed" => WebhookEventStatusFilter.Failed,
                "succeeded" => WebhookEventStatusFilter.Succeeded,
                _ => null
            };
            if (resolvedStatusFilter is null)
            {
                return TypedResults.BadRequest(new ApiErrorResponse(
                    Error: "invalid_status",
                    Message: "Query parameter 'status' must be 'failed' or 'succeeded'."
                ));
            }
        }

        var resolvedLimit = limit ?? DefaultLimit;
        if (resolvedLimit is < 1 or > MaxLimit)
        {
            return TypedResults.BadRequest(new ApiErrorResponse(
                Error: "invalid_limit",
                Message: $"Query parameter 'limit' must be between 1 and {MaxLimit}."
            ));
        }

        if (!TryParseCursor(cursor, out var parsedCursor))
        {
            return TypedResults.BadRequest(new ApiErrorResponse(
                Error: "invalid_cursor",
                Message: "Query parameter 'cursor' is invalid."
            ));
        }

        if (!RequestEnvironment.TryResolve(ctx, environment, out var resolvedEnvironment, out var environmentError))
        {
            return TypedResults.BadRequest(environmentError);
        }

        var page = await repository.ListRecentAsync(
            projectId,
            resolvedEnvironment,
            resolvedLimit,
            parsedCursor,
            resolvedProvider,
            resolvedStatusFilter,
            ct);

        var response = new GetWebhookEventsResponse(
            Items: page.Items.Select(item => new WebhookEventDto(
                Id: item.EventId,
                Provider: item.Provider,
                Environment: item.Environment,
                Type: item.EventType,
                Change: item.EventChange,
                ExternalUserId: item.ExternalUserId,
                ProviderSubscriptionId: item.ProviderSubscriptionId,
                Status: item.Status,
                ErrorCode: item.ErrorCode,
                ReceivedAt: item.ReceivedAt,
                ProcessedAt: item.ProcessedAt
            )).ToList(),
            NextCursor: page.NextCursor is null
                ? null
                : EncodeCursor(page.NextCursor)
        );

        return TypedResults.Ok(response);
    }

    private static string EncodeCursor(WebhookEventCursorReadModel cursor)
    {
        var payload = $"{cursor.ReceivedAt.ToUnixTimeMilliseconds()}|{cursor.EventId}";
        return WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
    }

    private static bool TryParseCursor(string? raw, out WebhookEventCursorReadModel? cursor)
    {
        cursor = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        try
        {
            var bytes = WebEncoders.Base64UrlDecode(raw);
            var decoded = Encoding.UTF8.GetString(bytes);
            var separator = decoded.IndexOf('|');

            if (separator <= 0 || separator == decoded.Length - 1)
            {
                return false;
            }

            var timestampPart = decoded[..separator];
            var eventIdPart = decoded[(separator + 1)..];

            if (!long.TryParse(timestampPart, out var unixMs))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(eventIdPart))
            {
                return false;
            }

            cursor = new WebhookEventCursorReadModel(
                ReceivedAt: DateTimeOffset.FromUnixTimeMilliseconds(unixMs),
                EventId: eventIdPart
            );

            return true;
        }
        catch
        {
            return false;
        }
    }
}
