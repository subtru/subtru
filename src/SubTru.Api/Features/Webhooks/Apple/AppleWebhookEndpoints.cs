using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SubTru.Api.Platform.Observability;
using SubTru.Api.Features.Webhooks.Shared;
using SubTru.Domain.Subscriptions;
using SubTru.Infrastructure.Providers.Apple;
using SubTru.Infrastructure.Persistence.Postgres.Subscriptions;
using SubTru.Infrastructure.Persistence.Postgres.ProviderConfiguration;
using SubTru.Infrastructure.Persistence.Postgres.WebhookEvents;
using SubTru.Infrastructure.Persistence.Postgres.WebhookKeys;

namespace SubTru.Api.Features.Webhooks.Apple;

public static class AppleWebhookEndpoints
{
    private const string ProviderName = "apple";
    private const string UnverifiedEventType = "apple.unverified";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapAppleWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/apple/{webhookKey}", async (
            string webhookKey,
            HttpRequest request,
            ILoggerFactory loggerFactory,
            IWebhookKeyRepository webhookKeyRepository,
            IProviderConfigRepository providerConfigRepository,
            IAppleSignedPayloadVerifier verifier,
            IProcessedEventRepository processedEventRepository,
            IWebhookEventLogRepository webhookEventLogRepository,
            ISubscriptionWriteRepository subscriptionWriteRepository,
            IBusinessMetrics businessMetrics,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("SubTru.AppleWebhook");

            if (string.IsNullOrWhiteSpace(webhookKey))
            {
                logger.LogWarning("Apple webhook called with empty webhook key.");
                return Results.Ok();
            }

            var webhookKeyInfo = await webhookKeyRepository.GetByKeyAsync(webhookKey, ProviderName, ct);
            if (webhookKeyInfo is null)
            {
                logger.LogWarning("Apple webhook called with invalid or inactive webhook key.");
                return Results.Ok();
            }

            var projectId = webhookKeyInfo.ProjectId;
            var environment = webhookKeyInfo.Environment;
            var now = DateTimeOffset.UtcNow;

            var rawBody = await WebhookRequestBodyReader.ReadIfWithinLimitAsync(request, ct);
            if (rawBody is null)
            {
                logger.LogWarning(
                    "Apple webhook payload exceeded the {MaxBodySizeBytes} byte limit. project_id={ProjectId}",
                    WebhookRequestBodyReader.MaxBodySizeBytes,
                    projectId);
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var unverifiedEventId = CreateUnverifiedEventId(rawBody);

            AppleProviderConfigReadModel? config;
            try
            {
                config = await providerConfigRepository.GetAppleProviderConfigAsync(projectId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Apple webhook provider config lookup failed. project_id={ProjectId}", projectId);
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            if (config is null)
            {
                await LogFailedAttemptAsync(
                    webhookEventLogRepository,
                    projectId,
                    environment,
                    unverifiedEventId,
                    errorCode: "missing_apple_provider_config",
                    errorMessage: "Apple provider config is missing for this project.",
                    receivedAt: now,
                    businessMetrics,
                    logger,
                    ct);

                logger.LogError(
                    "Apple webhook provider config missing. Returning 503 so Apple can retry after setup is corrected. project_id={ProjectId}",
                    projectId);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            AppleWebhookRequest? payload;
            try
            {
                payload = JsonSerializer.Deserialize<AppleWebhookRequest>(rawBody, JsonOptions);
            }
            catch (JsonException ex)
            {
                await LogFailedAttemptAsync(
                    webhookEventLogRepository,
                    projectId,
                    environment,
                    unverifiedEventId,
                    errorCode: "invalid_json",
                    errorMessage: "Apple webhook request body could not be parsed.",
                    receivedAt: now,
                    businessMetrics,
                    logger,
                    ct);

                logger.LogWarning(ex, "Apple webhook request body could not be parsed. project_id={ProjectId}", projectId);
                return Results.Ok();
            }

            if (payload is null || string.IsNullOrWhiteSpace(payload.SignedPayload))
            {
                await LogFailedAttemptAsync(
                    webhookEventLogRepository,
                    projectId,
                    environment,
                    unverifiedEventId,
                    errorCode: "missing_signed_payload",
                    errorMessage: "Apple webhook request body missing signed payload.",
                    receivedAt: now,
                    businessMetrics,
                    logger,
                    ct);

                logger.LogWarning("Apple webhook request body missing signed payload. project_id={ProjectId}", projectId);
                return Results.Ok();
            }

            AppleTrustedNotification notification;
            try
            {
                notification = await verifier.VerifyAndDecode(payload.SignedPayload, config, ct);
            }
            catch (AppleSignedPayloadVerificationException ex)
            {
                await LogFailedAttemptAsync(
                    webhookEventLogRepository,
                    projectId,
                    environment,
                    CreateUnverifiedEventId(payload.SignedPayload),
                    errorCode: ex.ErrorCode,
                    errorMessage: ex.Message,
                    receivedAt: now,
                    businessMetrics,
                    logger,
                    ct);

                var retryable = IsRetryableAppleVerificationFailure(ex.ErrorCode);
                logger.LogWarning(
                    ex,
                    retryable
                        ? "Apple signed payload verification failed with retryable setup or trust failure. Returning 503. error_code={ErrorCode} project_id={ProjectId}"
                        : "Apple signed payload verification failed. error_code={ErrorCode} project_id={ProjectId}",
                    ex.ErrorCode,
                    projectId);
                return retryable
                    ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
                    : Results.Ok();
            }

            var providerCreatedAt = notification.SignedDate;
            var eventType = BuildEventType(notification);
            var handling = AppleSubscriptionMapper.ClassifyNotificationType(notification);

            if (handling.Kind == AppleNotificationHandlingKind.Ignored)
            {
                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, notification.NotificationUuid, eventType, externalUserId: null,
                        WebhookEventStatus.Ignored, errorCode: null, errorMessage: null,
                        providerCreatedAt, receivedAt: now, now, processedAt: now),
                    businessMetrics,
                    logger,
                    ct);

                return Results.Ok();
            }

            if (!TryResolveAppleEnvironment(notification, out var notificationEnvironment, out var environmentErrorCode, out var environmentErrorMessage))
            {
                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, notification.NotificationUuid, eventType, externalUserId: null,
                        WebhookEventStatus.Failed, environmentErrorCode, environmentErrorMessage,
                        providerCreatedAt, receivedAt: now, now, processedAt: null),
                    businessMetrics,
                    logger,
                    ct);

                return Results.Ok();
            }

            if (notificationEnvironment is not null
                && !string.Equals(notificationEnvironment, environment, StringComparison.Ordinal))
            {
                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, notification.NotificationUuid, eventType, externalUserId: null,
                        WebhookEventStatus.Failed, "environment_mismatch",
                        "Apple notification environment did not match the webhook key environment.",
                        providerCreatedAt, receivedAt: now, now, processedAt: null),
                    businessMetrics,
                    logger,
                    ct);

                logger.LogWarning("Apple webhook environment mismatch. event_id={EventId} notification_environment={NotificationEnvironment} key_environment={KeyEnvironment} project_id={ProjectId}",
                    notification.NotificationUuid,
                    notificationEnvironment,
                    environment,
                    projectId);
                return Results.Ok();
            }

            if (await processedEventRepository.IsProcessedAsync(projectId, ProviderName, environment, notification.NotificationUuid, ct))
            {
                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, notification.NotificationUuid, eventType, externalUserId: null,
                        WebhookEventStatus.Duplicate, errorCode: null, errorMessage: null,
                        providerCreatedAt, receivedAt: now, now, processedAt: now),
                    businessMetrics,
                    logger,
                    ct);

                return Results.Ok();
            }

            if (handling.Kind == AppleNotificationHandlingKind.Unsupported)
            {
                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, notification.NotificationUuid, eventType, externalUserId: null,
                        WebhookEventStatus.Failed, handling.ErrorCode, "Apple notification or purchase type is not supported by SubTru.",
                        providerCreatedAt, receivedAt: now, now, processedAt: null),
                    businessMetrics,
                    logger,
                    ct);

                logger.LogWarning("Apple webhook notification type is unsupported. type={NotificationType} subtype={Subtype} event_id={EventId} project_id={ProjectId}",
                    notification.NotificationType,
                    notification.Subtype,
                    notification.NotificationUuid,
                    projectId);
                return Results.Ok();
            }

            if (notification.Data?.TransactionInfo is null)
            {
                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, notification.NotificationUuid, eventType, externalUserId: null,
                        WebhookEventStatus.Failed, errorCode: "missing_signed_transaction_info", errorMessage: "Apple notification is missing signed transaction info.",
                        providerCreatedAt, receivedAt: now, now, processedAt: null),
                    businessMetrics,
                    logger,
                    ct);

                logger.LogWarning("Apple webhook missing signed transaction info. type={NotificationType} subtype={Subtype} event_id={EventId} project_id={ProjectId}",
                    notification.NotificationType,
                    notification.Subtype,
                    notification.NotificationUuid,
                    projectId);
                return Results.Ok();
            }

            if (!AppleSubscriptionMapper.TryMapAutoRenewableSubscription(
                    notification,
                    projectId,
                    environment,
                    out var normalized,
                    out var error)
                || normalized is null)
            {
                var externalUserId = notification.Data.TransactionInfo.AppAccountToken ?? notification.Data.RenewalInfo?.AppAccountToken;

                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, notification.NotificationUuid, eventType, externalUserId,
                        WebhookEventStatus.Failed, error, "Apple notification mapping failed.",
                        providerCreatedAt, receivedAt: now, now, processedAt: null),
                    businessMetrics,
                    logger,
                    ct);

                logger.LogWarning("Apple webhook could not normalize subscription. error={Error} event_id={EventId} project_id={ProjectId}", error, notification.NotificationUuid, projectId);
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
                    notification.NotificationUuid,
                    ct);

                if (writeOutcome == SubscriptionWriteOutcome.Duplicate)
                {
                    await TryLogEventAsync(
                        webhookEventLogRepository,
                        CreateLogModel(projectId, environment, notification.NotificationUuid, eventType, normalized.ExternalUserId,
                            WebhookEventStatus.Duplicate, errorCode: null, errorMessage: null,
                            providerCreatedAt, receivedAt: now, now, processedAt: now),
                        businessMetrics,
                        logger,
                        ct);

                    return Results.Ok();
                }

                if (writeOutcome == SubscriptionWriteOutcome.Stale)
                {
                    await TryLogEventAsync(
                        webhookEventLogRepository,
                        CreateLogModel(projectId, environment, notification.NotificationUuid, eventType, normalized.ExternalUserId,
                            WebhookEventStatus.Ignored, "stale_provider_event",
                            "Apple notification was older than the stored subscription state.",
                            providerCreatedAt, receivedAt: now, now, processedAt: now),
                        businessMetrics,
                        logger,
                        ct);

                    logger.LogInformation(
                        "Apple webhook event ignored as stale. event_id={EventId} project_id={ProjectId}",
                        notification.NotificationUuid,
                        projectId);
                    return Results.Ok();
                }

                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, notification.NotificationUuid, eventType, normalized.ExternalUserId,
                        WebhookEventStatus.Success, errorCode: null, errorMessage: null,
                        providerCreatedAt, receivedAt: now, now, processedAt: now),
                    businessMetrics,
                    logger,
                    ct);

                return Results.Ok();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, notification.NotificationUuid, eventType, notification.Data.TransactionInfo.AppAccountToken,
                        WebhookEventStatus.Failed, "processing_failed", "Unhandled exception while processing Apple webhook event.",
                        providerCreatedAt, receivedAt: now, now, processedAt: null),
                    businessMetrics,
                    logger,
                    ct);

                logger.LogError(ex, "Apple webhook processing failed. event_id={EventId} project_id={ProjectId}", notification.NotificationUuid, projectId);
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }
        });
    }

    private static string BuildEventType(AppleTrustedNotification notification)
        => string.IsNullOrWhiteSpace(notification.Subtype)
            ? notification.NotificationType
            : $"{notification.NotificationType}.{notification.Subtype}";

    private static bool IsRetryableAppleVerificationFailure(string errorCode)
        => errorCode is AppleSignedPayloadVerifier.RetryableCertificateChainErrorCode
            or "bundle_id_mismatch"
            or "app_apple_id_mismatch";

    private static bool TryResolveAppleEnvironment(
        AppleTrustedNotification notification,
        out string? environment,
        out string? errorCode,
        out string? errorMessage)
    {
        environment = null;
        errorCode = null;
        errorMessage = null;

        var rawValues = new[]
        {
            notification.Data?.Environment,
            notification.Data?.TransactionInfo?.Environment,
            notification.Data?.RenewalInfo?.Environment
        }.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();

        if (rawValues.Count == 0)
        {
            errorCode = "missing_apple_environment";
            errorMessage = "Apple notification did not contain environment evidence.";
            return false;
        }

        foreach (var rawValue in rawValues)
        {
            var mapped = MapAppleEnvironment(rawValue);
            if (mapped is null)
            {
                errorCode = "invalid_apple_environment";
                errorMessage = "Apple notification contained an unknown environment value.";
                return false;
            }

            environment ??= mapped;
            if (!string.Equals(environment, mapped, StringComparison.Ordinal))
            {
                errorCode = "apple_environment_mismatch";
                errorMessage = "Apple notification payloads did not agree on environment.";
                return false;
            }
        }

        return true;
    }

    private static string? MapAppleEnvironment(string? value)
    {
        if (string.Equals(value, "production", StringComparison.OrdinalIgnoreCase))
        {
            return SubscriptionEnvironment.Production;
        }

        if (string.Equals(value, "sandbox", StringComparison.OrdinalIgnoreCase))
        {
            return SubscriptionEnvironment.Test;
        }

        return null;
    }

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
        DateTimeOffset? processedAt) =>
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
            ProviderSubscriptionId: null
        );

    private static string CreateUnverifiedEventId(string rawValue)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawValue));
        return $"unverified:{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    private static Task LogFailedAttemptAsync(
        IWebhookEventLogRepository repository,
        Guid projectId,
        string environment,
        string eventId,
        string errorCode,
        string errorMessage,
        DateTimeOffset receivedAt,
        IBusinessMetrics businessMetrics,
        ILogger logger,
        CancellationToken ct) =>
        TryLogEventAsync(
            repository,
            CreateLogModel(projectId, environment, eventId, UnverifiedEventType, externalUserId: null,
                WebhookEventStatus.Failed, errorCode, errorMessage,
                providerCreatedAt: null, receivedAt: receivedAt, lastAttemptedAt: receivedAt, processedAt: null),
            businessMetrics,
            logger,
            ct);

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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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

    private sealed record AppleWebhookRequest(string SignedPayload);
}
