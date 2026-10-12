using Stripe;
using SubTru.Api.Features.Webhooks.Shared;
using SubTru.Api.Platform.Observability;
using SubTru.Domain.Subscriptions;
using SubTru.Infrastructure.Providers.Stripe;
using SubTru.Infrastructure.Persistence.Postgres.Subscriptions;
using SubTru.Infrastructure.Persistence.Postgres.WebhookEvents;
using SubTru.Infrastructure.Persistence.Postgres.WebhookKeys;

namespace SubTru.Api.Features.Webhooks.Stripe;

public static class StripeWebhookEndpoints
{
    private const string ProviderName = "stripe";
    private const string ExternalUserIdMetadataKey = "subtru_external_user_id";
    private const string SubscriptionUpdatedEvent = "customer.subscription.updated";
    private static readonly TimeSpan SignatureTolerance = TimeSpan.FromMinutes(5);

    private static readonly HashSet<string> SubscriptionEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "customer.subscription.created",
        SubscriptionUpdatedEvent,
        "customer.subscription.deleted",
    };

    public static void MapStripeWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/stripe/{webhookKey}", async (
            string webhookKey,
            HttpRequest request,
            ILoggerFactory loggerFactory,
            IWebhookKeyRepository webhookKeyRepository,
            IWebhookEventLogRepository webhookEventLogRepository,
            IProcessedEventRepository processedEventRepository,
            ISubscriptionWriteRepository subscriptionWriteRepository,
            IBusinessMetrics businessMetrics,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("SubTru.StripeWebhook");

            // Validate webhook key format
            if (string.IsNullOrWhiteSpace(webhookKey))
            {
                logger.LogWarning("Stripe webhook called with empty webhook key.");
                return Results.BadRequest(new { error = "missing_webhook_key" });
            }

            // Look up webhook key to get project context and signing secret
            var webhookKeyInfo = await webhookKeyRepository.GetByKeyAsync(webhookKey, ProviderName, ct);
            if (webhookKeyInfo is null)
            {
                logger.LogWarning(
                    "Stripe webhook called with invalid or inactive webhook key. webhook_key_fingerprint={WebhookKeyFingerprint}",
                    LogSanitizer.Fingerprint(webhookKey));
                return Results.NotFound(new { error = "invalid_webhook_key" });
            }

            var projectId = webhookKeyInfo.ProjectId;
            var environment = webhookKeyInfo.Environment;
            var now = DateTimeOffset.UtcNow;

            // Stripe requires a signing secret for signature verification
            if (webhookKeyInfo.SigningSecret is null)
            {
                logger.LogError("Stripe webhook key has no signing secret configured. project_id={ProjectId}", projectId);
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            // Stripe signature header
            if (!request.Headers.TryGetValue("Stripe-Signature", out var signatureHeader))
            {
                logger.LogWarning("Stripe webhook missing Stripe-Signature header. project_id={ProjectId}", projectId);
                return Results.BadRequest(new { error = "missing_stripe_signature" });
            }

            // Read raw request body (required for signature verification)
            var payload = await WebhookRequestBodyReader.ReadIfWithinLimitAsync(request, ct);
            if (payload is null)
            {
                logger.LogWarning(
                    "Stripe webhook payload exceeded the {MaxBodySizeBytes} byte limit. project_id={ProjectId}",
                    WebhookRequestBodyReader.MaxBodySizeBytes,
                    projectId);
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            Event stripeEvent;

            try
            {
                stripeEvent = EventUtility.ConstructEvent(
                    payload,
                    signatureHeader.ToString(),
                    webhookKeyInfo.SigningSecret,
                    tolerance: (long)SignatureTolerance.TotalSeconds,
                    throwOnApiVersionMismatch: false
                );
            }
            catch (StripeException ex)
            {
                logger.LogWarning(ex, "Stripe webhook signature verification failed. project_id={ProjectId}", projectId);
                return Results.BadRequest(new
                {
                    error = "invalid_stripe_signature",
                    message = ex.Message
                });
            }

            var providerCreatedAt = ToDateTimeOffset(stripeEvent.Created);
            var providerSubscriptionId = GetProviderSubscriptionId(stripeEvent.Data?.Object);

            if (!string.Equals(stripeEvent.ApiVersion, StripeConfiguration.ApiVersion, StringComparison.Ordinal))
            {
                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, stripeEvent.Id, stripeEvent.Type, externalUserId: null,
                        WebhookEventStatus.Failed, "stripe_api_version_mismatch",
                        "Stripe event API version did not match the Stripe.net API version.",
                        providerCreatedAt, receivedAt: now, now, processedAt: null,
                        providerSubscriptionId: providerSubscriptionId),
                    businessMetrics,
                    logger,
                    ct);

                logger.LogError(
                    "Stripe webhook API version mismatch. event_id={EventId} event_api_version={EventApiVersion} expected_api_version={ExpectedApiVersion} project_id={ProjectId}",
                    stripeEvent.Id,
                    stripeEvent.ApiVersion,
                    StripeConfiguration.ApiVersion,
                    projectId);

                return Results.Ok(new
                {
                    error = "stripe_api_version_mismatch",
                    eventApiVersion = stripeEvent.ApiVersion,
                    expectedApiVersion = StripeConfiguration.ApiVersion
                });
            }

            var eventEnvironment = stripeEvent.Livemode
                ? SubscriptionEnvironment.Production
                : SubscriptionEnvironment.Test;
            if (!string.Equals(eventEnvironment, environment, StringComparison.Ordinal))
            {
                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, stripeEvent.Id, stripeEvent.Type, externalUserId: null,
                        WebhookEventStatus.Failed, "environment_mismatch",
                        "Stripe event livemode did not match the webhook key environment.",
                        providerCreatedAt, receivedAt: now, now, processedAt: null,
                        providerSubscriptionId: providerSubscriptionId),
                    businessMetrics,
                    logger,
                    ct);

                logger.LogWarning("Stripe webhook environment mismatch. event_id={EventId} event_environment={EventEnvironment} key_environment={KeyEnvironment} project_id={ProjectId}",
                    stripeEvent.Id,
                    eventEnvironment,
                    environment,
                    projectId);
                return Results.Ok();
            }

            if (!SubscriptionEvents.Contains(stripeEvent.Type))
            {
                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, stripeEvent.Id, stripeEvent.Type, externalUserId: null,
                        WebhookEventStatus.Ignored, errorCode: null, errorMessage: null,
                        providerCreatedAt, receivedAt: now, now, processedAt: now,
                        providerSubscriptionId: providerSubscriptionId),
                    businessMetrics,
                    logger,
                    ct);

                return Results.Ok();
            }

            // Idempotency: short-circuit if already handled
            if (await processedEventRepository.IsProcessedAsync(projectId, ProviderName, environment, stripeEvent.Id, ct))
            {
                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, stripeEvent.Id, stripeEvent.Type, externalUserId: null,
                        WebhookEventStatus.Duplicate, errorCode: null, errorMessage: null,
                        providerCreatedAt, receivedAt: now, now, processedAt: now,
                        providerSubscriptionId: providerSubscriptionId),
                    businessMetrics,
                    logger,
                    ct);

                logger.LogInformation("Stripe webhook event already processed. event_id={EventId} type={EventType} project_id={ProjectId}", stripeEvent.Id, stripeEvent.Type, projectId);
                return Results.Ok();
            }

            // Stripe.net gives us the event object as Stripe.Subscription
            if (stripeEvent.Data?.Object is not Subscription stripeSub)
            {
                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, stripeEvent.Id, stripeEvent.Type, externalUserId: null,
                        WebhookEventStatus.Failed, "unexpected_event_object",
                        "Stripe event data object was not a subscription.",
                        providerCreatedAt, receivedAt: now, now, processedAt: null,
                        providerSubscriptionId: providerSubscriptionId),
                    businessMetrics,
                    logger,
                    ct);

                logger.LogWarning("Stripe webhook unexpected event object type. event_id={EventId} type={EventType} project_id={ProjectId}", stripeEvent.Id, stripeEvent.Type, projectId);
                return Results.BadRequest(new { error = "unexpected_event_object" });
            }

            var eventChange = string.Equals(
                stripeEvent.Type,
                SubscriptionUpdatedEvent,
                StringComparison.OrdinalIgnoreCase)
                    ? StripeSubscriptionUpdateChangeClassifier.Classify(payload)
                    : null;

            // REQUIRED metadata: subtru_external_user_id
            // Customer must set this when creating subscriptions.
            // Project ID is determined from the webhook key in the URL.
            // Return 200 even on missing metadata to prevent infinite Stripe retries.
            // The failure is still recorded in webhook_events for the customer to diagnose.
            var meta = stripeSub.Metadata ?? new Dictionary<string, string>();

            if (!meta.TryGetValue(ExternalUserIdMetadataKey, out var externalUserId) ||
                string.IsNullOrWhiteSpace(externalUserId))
            {
                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, stripeEvent.Id, stripeEvent.Type, externalUserId: null,
                        WebhookEventStatus.Failed, "missing_or_invalid_subtru_external_user_id",
                        "Stripe subscription metadata is missing required subtru_external_user_id.",
                        providerCreatedAt, receivedAt: now, now, processedAt: null,
                        providerSubscriptionId: providerSubscriptionId,
                        eventChange: eventChange),
                    businessMetrics,
                    logger,
                    ct);

                logger.LogWarning("Stripe webhook missing/invalid subtru_external_user_id. event_id={EventId} type={EventType} subscription_id={SubscriptionId} project_id={ProjectId}", stripeEvent.Id, stripeEvent.Type, stripeSub.Id, projectId);

                // Return 200 to prevent infinite Stripe retries — metadata will never self-correct.
                return Results.Ok();
            }

            if (!StripeSubscriptionMapper.TryMapSingleItem(
                    stripeSub,
                    projectId,
                    environment,
                    externalUserId,
                    out var normalized,
                    out var error)
                || normalized is null)
            {
                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, stripeEvent.Id, stripeEvent.Type, externalUserId,
                        WebhookEventStatus.Failed, error, "Stripe subscription mapping failed.",
                        providerCreatedAt, receivedAt: now, now, processedAt: null,
                        providerSubscriptionId: providerSubscriptionId,
                        eventChange: eventChange),
                    businessMetrics,
                    logger,
                    ct);

                logger.LogWarning(
                    "Stripe webhook could not normalize subscription. error={Error} event_id={EventId} type={EventType} subscription_id={SubscriptionId} project_id={ProjectId} user_id={UserId}",
                    error,
                    stripeEvent.Id,
                    stripeEvent.Type,
                    stripeSub.Id,
                    projectId,
                    externalUserId);

                // Return 200 — mapping failures are non-recoverable; retrying won't change the payload.
                return Results.Ok();
            }

            var subscription = normalized with
            {
                ProviderEventCreatedAt = providerCreatedAt
            };

            try
            {
                var writeOutcome = await subscriptionWriteRepository.UpsertWithEventAsync(
                    subscription,
                    stripeEvent.Id,
                    ct);

                if (writeOutcome == SubscriptionWriteOutcome.Duplicate)
                {
                    await TryLogEventAsync(
                        webhookEventLogRepository,
                        CreateLogModel(projectId, environment, stripeEvent.Id, stripeEvent.Type, externalUserId,
                            WebhookEventStatus.Duplicate, errorCode: null, errorMessage: null,
                            providerCreatedAt, receivedAt: now, now, processedAt: now,
                            providerSubscriptionId: providerSubscriptionId,
                            eventChange: eventChange),
                        businessMetrics,
                        logger,
                        ct);

                    logger.LogInformation("Stripe webhook event was already claimed during processing. event_id={EventId} type={EventType} project_id={ProjectId}", stripeEvent.Id, stripeEvent.Type, projectId);
                    return Results.Ok();
                }

                if (writeOutcome == SubscriptionWriteOutcome.Stale)
                {
                    await TryLogEventAsync(
                        webhookEventLogRepository,
                        CreateLogModel(projectId, environment, stripeEvent.Id, stripeEvent.Type, externalUserId,
                            WebhookEventStatus.Ignored, "stale_provider_event",
                            "Stripe webhook event was older than the stored subscription state.",
                            providerCreatedAt, receivedAt: now, now, processedAt: now,
                            providerSubscriptionId: providerSubscriptionId,
                            eventChange: eventChange),
                        businessMetrics,
                        logger,
                        ct);

                    logger.LogInformation(
                        "Stripe webhook event ignored as stale. event_id={EventId} type={EventType} subscription_id={SubscriptionId} project_id={ProjectId}",
                        stripeEvent.Id,
                        stripeEvent.Type,
                        stripeSub.Id,
                        projectId);
                    return Results.Ok();
                }

                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, stripeEvent.Id, stripeEvent.Type, externalUserId,
                        WebhookEventStatus.Success, errorCode: null, errorMessage: null,
                        providerCreatedAt, receivedAt: now, now, processedAt: now,
                        providerSubscriptionId: providerSubscriptionId,
                        eventChange: eventChange),
                    businessMetrics,
                    logger,
                    ct);

                logger.LogInformation(
                    "Stripe webhook processed successfully. event_id={EventId} type={EventType} subscription_id={SubscriptionId} project_id={ProjectId} user_id={UserId} status={Status}",
                    stripeEvent.Id,
                    stripeEvent.Type,
                    stripeSub.Id,
                    projectId,
                    externalUserId,
                    normalized?.Status);
            }
            catch (Exception ex)
            {
                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, stripeEvent.Id, stripeEvent.Type, externalUserId,
                        WebhookEventStatus.Failed, "processing_failed",
                        "Unhandled exception while processing Stripe webhook event.",
                        providerCreatedAt, receivedAt: now, now, processedAt: null,
                        providerSubscriptionId: providerSubscriptionId,
                        eventChange: eventChange),
                    businessMetrics,
                    logger,
                    ct);

                logger.LogError(
                    ex,
                    "Stripe webhook processing failed. event_id={EventId} type={EventType} subscription_id={SubscriptionId} project_id={ProjectId} user_id={UserId}",
                    stripeEvent.Id,
                    stripeEvent.Type,
                    stripeSub.Id,
                    projectId,
                    externalUserId);

                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            return Results.Ok();
        });
    }

    /// <summary>
    /// Creates a <see cref="WebhookEventLogWriteModel"/> with consistent field assignment.
    /// </summary>
    private static WebhookEventLogWriteModel CreateLogModel(
        Guid projectId,
        string environment,
        string eventId,
        string? eventType,
        string? externalUserId,
        string status,
        string? errorCode,
        string? errorMessage,
        DateTimeOffset? providerCreatedAt,
        DateTimeOffset receivedAt,
        DateTimeOffset lastAttemptedAt,
        DateTimeOffset? processedAt,
        string? providerSubscriptionId,
        string? eventChange = null) =>
        new(
            ProjectId: projectId,
            Provider: ProviderName,
            Environment: environment,
            EventId: eventId,
            EventType: eventType,
            ExternalUserId: externalUserId,
            Status: status,
            ErrorCode: errorCode,
            ErrorMessage: errorMessage,
            ProviderCreatedAt: providerCreatedAt,
            ReceivedAt: receivedAt,
            LastAttemptedAt: lastAttemptedAt,
            ProcessedAt: processedAt,
            ProviderSubscriptionId: providerSubscriptionId,
            EventChange: eventChange
        );

    private static string? GetProviderSubscriptionId(object? dataObject)
    {
        var providerSubscriptionId = dataObject switch
        {
            Subscription subscription => subscription.Id,
            Invoice invoice => invoice.Parent?.SubscriptionDetails?.SubscriptionId,
            _ => null
        };

        return string.IsNullOrWhiteSpace(providerSubscriptionId)
            ? null
            : providerSubscriptionId;
    }

    private static DateTimeOffset? ToDateTimeOffset(DateTime? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        var utc = DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
        return new DateTimeOffset(utc);
    }

    private static async Task TryLogEventAsync(
        IWebhookEventLogRepository repository,
        WebhookEventLogWriteModel model,
        IBusinessMetrics businessMetrics,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            await repository.UpsertAsync(model, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to persist webhook event log. provider={Provider} event_id={EventId} project_id={ProjectId}",
                model.Provider,
                model.EventId,
                model.ProjectId);
        }

        businessMetrics.RecordWebhookEvent(
            model.Provider,
            model.Environment,
            model.Status,
            model.ErrorCode,
            DateTimeOffset.UtcNow - model.ReceivedAt);
    }
}
