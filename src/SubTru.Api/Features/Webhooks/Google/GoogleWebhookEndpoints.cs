using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SubTru.Api.Features.Webhooks.Shared;
using SubTru.Api.Platform.Observability;
using SubTru.Domain.Subscriptions;
using SubTru.Infrastructure.Providers.Google;
using SubTru.Infrastructure.Persistence.Postgres.Subscriptions;
using SubTru.Infrastructure.Persistence.Postgres.Google;
using SubTru.Infrastructure.Persistence.Postgres.ProviderConfiguration;
using SubTru.Infrastructure.Persistence.Postgres.WebhookEvents;
using SubTru.Infrastructure.Persistence.Postgres.WebhookKeys;

namespace SubTru.Api.Features.Webhooks.Google;

public static class GoogleWebhookEndpoints
{
    private const string ProviderName = "google";
    private const string UnverifiedEventType = "google.unverified";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapGoogleWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/google/{webhookKey}", async (
            string webhookKey,
            HttpRequest request,
            ILoggerFactory loggerFactory,
            IWebhookKeyRepository webhookKeyRepository,
            IProviderConfigRepository providerConfigRepository,
            IGooglePubSubPushJwtValidator pushJwtValidator,
            IOptions<WebhookUrlOptions> webhookUrlOptions,
            IGooglePlaySubscriptionClient googlePlaySubscriptionClient,
            IGoogleSubscriptionSnapshotRepository googleSubscriptionSnapshotRepository,
            IGoogleSubscriptionWriteRepository googleSubscriptionWriteRepository,
            ISubscriptionReadRepository subscriptionReadRepository,
            IWebhookEventLogRepository webhookEventLogRepository,
            IBusinessMetrics businessMetrics,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("SubTru.GoogleWebhook");

            if (string.IsNullOrWhiteSpace(webhookKey))
            {
                logger.LogWarning("Google webhook called with empty webhook key.");
                return Results.Ok();
            }

            var webhookKeyInfo = await webhookKeyRepository.GetByKeyAsync(webhookKey, ProviderName, ct);
            if (webhookKeyInfo is null)
            {
                logger.LogWarning("Google webhook called with invalid or inactive webhook key.");
                return Results.Ok();
            }

            var projectId = webhookKeyInfo.ProjectId;
            var environment = webhookKeyInfo.Environment;
            var now = DateTimeOffset.UtcNow;
            var unverifiedEventId = CreateUnverifiedEventId(request.HttpContext.TraceIdentifier);

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
                await LogFailedAttemptAsync(
                    webhookEventLogRepository,
                    projectId,
                    environment,
                    unverifiedEventId,
                    errorCode: "google_provider_config_lookup_failed",
                    errorMessage: "Google provider config lookup failed.",
                    receivedAt: now,
                    businessMetrics,
                    logger,
                    ct);

                logger.LogError(ex, "Google webhook provider config lookup failed. project_id={ProjectId}", projectId);
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            if (config is null)
            {
                await LogFailedAttemptAsync(
                    webhookEventLogRepository,
                    projectId,
                    environment,
                    unverifiedEventId,
                    errorCode: "missing_google_provider_config",
                    errorMessage: "Google provider config is missing for this project.",
                    receivedAt: now,
                    businessMetrics,
                    logger,
                    ct);

                logger.LogWarning(
                    "Google provider config missing. Returning 503 so Google can retry after setup is corrected. project_id={ProjectId}",
                    projectId);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            if (credential is null)
            {
                await LogFailedAttemptAsync(
                    webhookEventLogRepository,
                    projectId,
                    environment,
                    unverifiedEventId,
                    errorCode: "missing_google_provider_credentials",
                    errorMessage: "Google provider credentials are missing for this project.",
                    receivedAt: now,
                    businessMetrics,
                    logger,
                    ct);

                logger.LogWarning(
                    "Google provider credentials missing. Returning 503 so Google can retry after setup is corrected. project_id={ProjectId}",
                    projectId);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            if (!TryGetBearerToken(request, out var bearerToken))
            {
                await LogFailedAttemptAsync(
                    webhookEventLogRepository,
                    projectId,
                    environment,
                    unverifiedEventId,
                    errorCode: "missing_google_pubsub_bearer_token",
                    errorMessage: "Google Pub/Sub push request is missing the bearer token.",
                    receivedAt: now,
                    businessMetrics,
                    logger,
                    ct);

                logger.LogWarning("Google webhook missing bearer token. project_id={ProjectId}", projectId);
                return Results.Ok();
            }

            var expectedAudience = WebhookUrlBuilder.Build(webhookUrlOptions.Value, ProviderName, webhookKey);

            try
            {
                await pushJwtValidator.ValidateAsync(
                    bearerToken,
                    expectedAudience,
                    config.PubSubAuthServiceAccountEmail,
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (GooglePubSubPushJwtValidationException ex)
            {
                await LogFailedAttemptAsync(
                    webhookEventLogRepository,
                    projectId,
                    environment,
                    unverifiedEventId,
                    errorCode: ex.ErrorCode,
                    errorMessage: ex.Message,
                    receivedAt: now,
                    businessMetrics,
                    logger,
                    ct);

                var retryable = IsRetryableGooglePushJwtFailure(ex);
                logger.LogWarning(
                    ex,
                    retryable
                        ? "Google Pub/Sub push JWT validation failed with retryable setup or transient failure. Returning 503. error_code={ErrorCode} project_id={ProjectId}"
                        : "Google Pub/Sub push JWT validation failed. error_code={ErrorCode} project_id={ProjectId}",
                    ex.ErrorCode,
                    projectId);
                return retryable
                    ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
                    : Results.Ok();
            }

            var rawBody = await WebhookRequestBodyReader.ReadIfWithinLimitAsync(request, ct);
            if (rawBody is null)
            {
                logger.LogWarning(
                    "Google webhook payload exceeded the {MaxBodySizeBytes} byte limit. project_id={ProjectId}",
                    WebhookRequestBodyReader.MaxBodySizeBytes,
                    projectId);
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            unverifiedEventId = CreateUnverifiedEventId(rawBody);

            GooglePubSubPushEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<GooglePubSubPushEnvelope>(rawBody, JsonOptions);
            }
            catch (JsonException ex)
            {
                await LogFailedAttemptAsync(
                    webhookEventLogRepository,
                    projectId,
                    environment,
                    unverifiedEventId,
                    errorCode: "invalid_google_pubsub_json",
                    errorMessage: "Google Pub/Sub push request body could not be parsed.",
                    receivedAt: now,
                    businessMetrics,
                    logger,
                    ct);

                logger.LogWarning(ex, "Google Pub/Sub push request body could not be parsed. project_id={ProjectId}", projectId);
                return Results.Ok();
            }

            if (envelope?.Message is null
                || string.IsNullOrWhiteSpace(envelope.Message.MessageId)
                || string.IsNullOrWhiteSpace(envelope.Message.Data))
            {
                await LogFailedAttemptAsync(
                    webhookEventLogRepository,
                    projectId,
                    environment,
                    unverifiedEventId,
                    errorCode: "invalid_google_pubsub_message",
                    errorMessage: "Google Pub/Sub push envelope is missing message id or data.",
                    receivedAt: now,
                    businessMetrics,
                    logger,
                    ct);

                logger.LogWarning("Google Pub/Sub push envelope missing message id or data. project_id={ProjectId}", projectId);
                return Results.Ok();
            }

            var eventId = envelope.Message.MessageId.Trim();

            GoogleDeveloperNotification? developerNotification;
            try
            {
                var messageBytes = Convert.FromBase64String(envelope.Message.Data);
                var messageJson = Encoding.UTF8.GetString(messageBytes);
                developerNotification = JsonSerializer.Deserialize<GoogleDeveloperNotification>(messageJson, JsonOptions);
            }
            catch (FormatException ex)
            {
                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, eventId, UnverifiedEventType, externalUserId: null,
                        WebhookEventStatus.Failed, "invalid_google_pubsub_message_data",
                        "Google Pub/Sub message data is not valid base64.",
                        providerCreatedAt: null, receivedAt: now, now, processedAt: null),
                    businessMetrics,
                    logger,
                    ct);

                logger.LogWarning(ex, "Google Pub/Sub message data was not valid base64. project_id={ProjectId}", projectId);
                return Results.Ok();
            }
            catch (JsonException ex)
            {
                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, eventId, UnverifiedEventType, externalUserId: null,
                        WebhookEventStatus.Failed, "invalid_google_rtdn_payload",
                        "Google RTDN payload could not be parsed.",
                        providerCreatedAt: null, receivedAt: now, now, processedAt: null),
                    businessMetrics,
                    logger,
                    ct);

                logger.LogWarning(ex, "Google RTDN payload could not be parsed. project_id={ProjectId}", projectId);
                return Results.Ok();
            }

            if (developerNotification is null)
            {
                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, eventId, UnverifiedEventType, externalUserId: null,
                        WebhookEventStatus.Failed, "invalid_google_rtdn_payload",
                        "Google RTDN payload could not be parsed.",
                        providerCreatedAt: null, receivedAt: now, now, processedAt: null),
                    businessMetrics,
                    logger,
                    ct);

                logger.LogWarning("Google RTDN payload was empty. project_id={ProjectId}", projectId);
                return Results.Ok();
            }

            var providerCreatedAt = TryParseProviderCreatedAt(developerNotification.EventTimeMillis);

            if (developerNotification.TestNotification is not null)
            {
                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, eventId, "google.test_notification", externalUserId: null,
                        WebhookEventStatus.Ignored, errorCode: null, errorMessage: null,
                        providerCreatedAt, receivedAt: now, now, processedAt: now),
                    businessMetrics,
                    logger,
                    ct);

                return Results.Ok();
            }

            if (developerNotification.PendingRefundReviewNotification is not null)
            {
                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, eventId, "google.pending_refund_review", externalUserId: null,
                        WebhookEventStatus.Ignored, errorCode: null, errorMessage: null,
                        providerCreatedAt, receivedAt: now, now, processedAt: now),
                    businessMetrics,
                    logger,
                    ct);

                return Results.Ok();
            }

            if (developerNotification.SubscriptionNotification is null)
            {
                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, eventId, "google.non_subscription_notification", externalUserId: null,
                        WebhookEventStatus.Ignored, errorCode: null, errorMessage: null,
                        providerCreatedAt, receivedAt: now, now, processedAt: now),
                    businessMetrics,
                    logger,
                    ct);

                return Results.Ok();
            }

            var eventType = CreateSubscriptionEventType(developerNotification.SubscriptionNotification.NotificationType);

            if (providerCreatedAt is null)
            {
                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, eventId, eventType, externalUserId: null,
                        WebhookEventStatus.Failed, "missing_or_invalid_google_event_time",
                        "Google RTDN subscription notification is missing a valid eventTimeMillis.",
                        providerCreatedAt: null, receivedAt: now, now, processedAt: null),
                    businessMetrics,
                    logger,
                    ct);

                logger.LogWarning(
                    "Google RTDN subscription notification missing valid event time. event_id={EventId} project_id={ProjectId}",
                    eventId,
                    projectId);
                return Results.Ok();
            }

            if (!string.Equals(developerNotification.PackageName, config.PackageName, StringComparison.Ordinal))
            {
                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, eventId, eventType, externalUserId: null,
                        WebhookEventStatus.Failed, "google_package_name_mismatch",
                        "Google RTDN packageName does not match the configured Google package.",
                        providerCreatedAt, receivedAt: now, now, processedAt: null),
                    businessMetrics,
                    logger,
                    ct);

                logger.LogWarning("Google RTDN package mismatch. Returning 503 so Google can retry after package configuration is corrected. package_name={PackageName} configured_package={ConfiguredPackage} project_id={ProjectId}",
                    developerNotification.PackageName,
                    config.PackageName,
                    projectId);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            var purchaseToken = developerNotification.SubscriptionNotification.PurchaseToken?.Trim();
            if (string.IsNullOrWhiteSpace(purchaseToken))
            {
                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, eventId, eventType, externalUserId: null,
                        WebhookEventStatus.Failed, "missing_google_purchase_token",
                        "Google RTDN subscription notification is missing purchaseToken.",
                        providerCreatedAt, receivedAt: now, now, processedAt: null),
                    businessMetrics,
                    logger,
                    ct);

                logger.LogWarning("Google RTDN subscription notification missing purchase token. project_id={ProjectId}", projectId);
                return Results.Ok();
            }

            GooglePlaySubscriptionPurchase purchase;
            string effectivePurchaseToken = purchaseToken;
            var fetchSequence = 0;
            DateTimeOffset providerStateObservedAt;
            try
            {
                purchase = await googlePlaySubscriptionClient.GetSubscriptionAsync(
                    config.PackageName,
                    purchaseToken,
                    credential.ServiceAccountJson,
                    ct);
                await TrySaveSubscriptionSnapshotAsync(
                    googleSubscriptionSnapshotRepository,
                    projectId,
                    environment,
                    eventId,
                    ++fetchSequence,
                    purchaseToken,
                    purchase,
                    logger,
                    ct);

                if (purchase.SubscriptionState == GoogleSubscriptionMapper.SubscriptionStatePendingPurchaseCanceled
                    && !string.IsNullOrWhiteSpace(purchase.LinkedPurchaseToken))
                {
                    effectivePurchaseToken = purchase.LinkedPurchaseToken;
                    purchase = await googlePlaySubscriptionClient.GetSubscriptionAsync(
                        config.PackageName,
                        effectivePurchaseToken,
                        credential.ServiceAccountJson,
                        ct);
                    await TrySaveSubscriptionSnapshotAsync(
                        googleSubscriptionSnapshotRepository,
                        projectId,
                        environment,
                        eventId,
                        ++fetchSequence,
                        effectivePurchaseToken,
                        purchase,
                        logger,
                        ct);
                }

                providerStateObservedAt = DateTimeOffset.UtcNow;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (GooglePlayApiException ex)
            {
                var (errorCode, errorMessage, statusCode) = ClassifyGoogleApiFailure(ex, fetchOperation: true);

                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, eventId, eventType, externalUserId: null,
                        WebhookEventStatus.Failed, errorCode, errorMessage,
                        providerCreatedAt, receivedAt: now, now, processedAt: null,
                        providerSubscriptionId: purchaseToken),
                    businessMetrics,
                    logger,
                    ct);

                logger.LogWarning(ex, "Google subscription fetch failed. error_code={ErrorCode} project_id={ProjectId} requested_purchase_token_fingerprint={RequestedPurchaseTokenFingerprint}",
                    errorCode,
                    projectId,
                    LogSanitizer.Fingerprint(purchaseToken));
                return Results.StatusCode(statusCode);
            }

            var purchaseEnvironment = purchase.TestPurchase is null
                ? SubscriptionEnvironment.Production
                : SubscriptionEnvironment.Test;
            if (!string.Equals(purchaseEnvironment, environment, StringComparison.Ordinal))
            {
                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, eventId, eventType, externalUserId: null,
                        WebhookEventStatus.Ignored, "environment_mismatch",
                        "Google purchase testPurchase state does not match the webhook key environment.",
                        providerCreatedAt, receivedAt: now, now, processedAt: now,
                        providerSubscriptionId: purchaseToken),
                    businessMetrics,
                    logger,
                    ct);

                logger.LogInformation(
                    "Google webhook ignored due to environment mismatch. purchase_environment={PurchaseEnvironment} key_environment={KeyEnvironment} project_id={ProjectId} effective_purchase_token_fingerprint={EffectivePurchaseTokenFingerprint}",
                    purchaseEnvironment,
                    environment,
                    projectId,
                    LogSanitizer.Fingerprint(effectivePurchaseToken));
                return Results.Ok();
            }

            var mapping = GoogleSubscriptionMapper.Map(effectivePurchaseToken, purchase);
            if (mapping.Kind == GoogleSubscriptionMappingKind.Unsupported || mapping.Data is null)
            {
                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, eventId, eventType, externalUserId: null,
                        WebhookEventStatus.Failed, mapping.ErrorCode, mapping.ErrorMessage,
                        providerCreatedAt, receivedAt: now, now, processedAt: null,
                        providerSubscriptionId: purchaseToken),
                    businessMetrics,
                    logger,
                    ct);

                logger.LogWarning("Google subscription could not be normalized. error_code={ErrorCode} project_id={ProjectId} effective_purchase_token_fingerprint={EffectivePurchaseTokenFingerprint}",
                    mapping.ErrorCode,
                    projectId,
                    LogSanitizer.Fingerprint(effectivePurchaseToken));
                return Results.Ok();
            }

            var externalUserId = await ResolveExternalUserIdAsync(
                projectId,
                environment,
                effectivePurchaseToken,
                purchase,
                subscriptionReadRepository,
                ct);

            if (string.IsNullOrWhiteSpace(externalUserId))
            {
                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, eventId, eventType, externalUserId: null,
                        WebhookEventStatus.Failed, "google_unbound_purchase",
                        "Google purchase could not be safely bound to a SubTru user.",
                        providerCreatedAt, receivedAt: now, now, processedAt: null,
                        providerSubscriptionId: purchaseToken),
                    businessMetrics,
                    logger,
                    ct);

                logger.LogWarning(
                    "Google purchase could not be bound. project_id={ProjectId} effective_purchase_token_fingerprint={EffectivePurchaseTokenFingerprint}",
                    projectId,
                    LogSanitizer.Fingerprint(effectivePurchaseToken));
                return Results.Ok();
            }

            var normalized = new NormalizedSubscription(
                ProjectId: projectId,
                Environment: environment,
                ExternalUserId: externalUserId,
                Provider: ProviderName,
                ProviderSubscriptionId: mapping.Data.ProviderSubscriptionId,
                ProductId: mapping.Data.ProductId,
                PriceId: mapping.Data.PriceId,
                Status: mapping.Data.Status,
                ProviderStatus: mapping.Data.ProviderStatus,
                CurrentPeriodEnd: mapping.Data.CurrentPeriodEnd,
                AccessEndsAt: mapping.Data.AccessEndsAt,
                CancelAtPeriodEnd: mapping.Data.CancelAtPeriodEnd,
                ProviderEventCreatedAt: providerStateObservedAt);

            try
            {
                var writeOutcome = await googleSubscriptionWriteRepository.UpsertGoogleWithEventAsync(
                    normalized,
                    eventId,
                    mapping.Data.LinkedPurchaseToken,
                    ct);

                if (writeOutcome == SubscriptionWriteOutcome.Duplicate)
                {
                    logger.LogInformation(
                        "Google webhook event was already processed. Continuing for possible acknowledgement retry. event_id={EventId} project_id={ProjectId}",
                        eventId,
                        projectId);
                }
                else if (writeOutcome == SubscriptionWriteOutcome.Stale)
                {
                    await TryLogEventAsync(
                        webhookEventLogRepository,
                        CreateLogModel(projectId, environment, eventId, eventType, externalUserId,
                            WebhookEventStatus.Ignored, "stale_provider_snapshot",
                            "Fetched Google subscription state was observed before the stored subscription state.",
                            providerCreatedAt, receivedAt: now, now, processedAt: now,
                            providerSubscriptionId: purchaseToken),
                        businessMetrics,
                        logger,
                        ct);

                    logger.LogInformation(
                        "Google subscription snapshot ignored as stale. event_id={EventId} project_id={ProjectId} effective_purchase_token_fingerprint={EffectivePurchaseTokenFingerprint}",
                        eventId,
                        projectId,
                        LogSanitizer.Fingerprint(effectivePurchaseToken));
                    return Results.Ok();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await TryLogEventAsync(
                    webhookEventLogRepository,
                    CreateLogModel(projectId, environment, eventId, eventType, externalUserId,
                        WebhookEventStatus.Failed, "processing_failed",
                        "Unhandled exception while processing Google webhook event.",
                        providerCreatedAt, receivedAt: now, now, processedAt: null,
                        providerSubscriptionId: purchaseToken),
                    businessMetrics,
                    logger,
                    ct);

                logger.LogError(ex, "Google webhook processing failed during durable write. event_id={EventId} project_id={ProjectId}", eventId, projectId);
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            if (mapping.Data.ShouldAcknowledge
                && purchase.AcknowledgementState == GoogleSubscriptionMapper.AcknowledgementStatePending)
            {
                try
                {
                    await googlePlaySubscriptionClient.AcknowledgeAsync(
                        config.PackageName,
                        effectivePurchaseToken,
                        credential.ServiceAccountJson,
                        mapping.Data.AcknowledgeSubscriptionId,
                        ct);

                    businessMetrics.RecordWebhookAcknowledgement(
                        ProviderName,
                        environment,
                        "success",
                        errorCode: null);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (GooglePlayApiException ex)
                {
                    var (errorCode, errorMessage, statusCode) = ClassifyGoogleApiFailure(ex, fetchOperation: false);

                    businessMetrics.RecordWebhookAcknowledgement(
                        ProviderName,
                        environment,
                        "failed",
                        errorCode);

                    await TryLogEventAsync(
                        webhookEventLogRepository,
                        CreateLogModel(projectId, environment, eventId, eventType, externalUserId,
                            WebhookEventStatus.Failed, errorCode, errorMessage,
                            providerCreatedAt, receivedAt: now, now, processedAt: null,
                            providerSubscriptionId: purchaseToken),
                        businessMetrics,
                        logger,
                        ct);

                    logger.LogWarning(ex, "Google subscription acknowledgement failed. error_code={ErrorCode} project_id={ProjectId} effective_purchase_token_fingerprint={EffectivePurchaseTokenFingerprint}",
                        errorCode,
                        projectId,
                        LogSanitizer.Fingerprint(effectivePurchaseToken));
                    return Results.StatusCode(statusCode);
                }
            }

            await TryLogEventAsync(
                webhookEventLogRepository,
                CreateLogModel(projectId, environment, eventId, eventType, externalUserId,
                    WebhookEventStatus.Success, errorCode: null, errorMessage: null,
                    providerCreatedAt, receivedAt: now, now, processedAt: now,
                    providerSubscriptionId: purchaseToken),
                businessMetrics,
                logger,
                ct);

            return Results.Ok();
        });
    }

    private static async Task<string?> ResolveExternalUserIdAsync(
        Guid projectId,
        string environment,
        string effectivePurchaseToken,
        GooglePlaySubscriptionPurchase purchase,
        ISubscriptionReadRepository subscriptionReadRepository,
        CancellationToken ct)
    {
        var obfuscatedExternalAccountId = purchase.ExternalAccountIdentifiers?.ObfuscatedExternalAccountId?.Trim();
        if (!string.IsNullOrWhiteSpace(obfuscatedExternalAccountId))
        {
            return obfuscatedExternalAccountId;
        }

        var expiredObfuscatedAccountId = purchase.OutOfAppPurchaseContext?.ExpiredExternalAccountIdentifiers?.ObfuscatedExternalAccountId?.Trim();
        if (!string.IsNullOrWhiteSpace(expiredObfuscatedAccountId))
        {
            return expiredObfuscatedAccountId;
        }

        var expiredPurchaseToken = purchase.OutOfAppPurchaseContext?.ExpiredPurchaseToken?.Trim();
        if (!string.IsNullOrWhiteSpace(expiredPurchaseToken))
        {
            var expiredExternalUserId = await subscriptionReadRepository.GetExternalUserIdByProviderSubscriptionIdAsync(
                projectId,
                environment,
                ProviderName,
                expiredPurchaseToken,
                ct);

            if (!string.IsNullOrWhiteSpace(expiredExternalUserId))
            {
                return expiredExternalUserId;
            }
        }

        var currentExternalUserId = await subscriptionReadRepository.GetExternalUserIdByProviderSubscriptionIdAsync(
            projectId,
            environment,
            ProviderName,
            effectivePurchaseToken,
            ct);

        if (!string.IsNullOrWhiteSpace(currentExternalUserId))
        {
            return currentExternalUserId;
        }

        var linkedPurchaseToken = purchase.LinkedPurchaseToken?.Trim();
        if (string.IsNullOrWhiteSpace(linkedPurchaseToken))
        {
            return null;
        }

        return await subscriptionReadRepository.GetExternalUserIdByProviderSubscriptionIdAsync(
            projectId,
            environment,
            ProviderName,
            linkedPurchaseToken,
            ct);
    }

    private static async Task TrySaveSubscriptionSnapshotAsync(
        IGoogleSubscriptionSnapshotRepository repository,
        Guid projectId,
        string environment,
        string eventId,
        int fetchSequence,
        string purchaseToken,
        GooglePlaySubscriptionPurchase purchase,
        ILogger logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(purchase.RawResponseJson))
        {
            logger.LogWarning(
                "Google subscription client returned no raw response for diagnostic persistence. event_id={EventId} project_id={ProjectId} fetch_sequence={FetchSequence}",
                eventId,
                projectId,
                fetchSequence);
            return;
        }

        try
        {
            await repository.SaveAsync(
                new GoogleSubscriptionSnapshotWriteModel(
                    ProjectId: projectId,
                    Environment: environment,
                    EventId: eventId,
                    FetchSequence: fetchSequence,
                    PurchaseToken: purchaseToken,
                    FetchedAt: DateTimeOffset.UtcNow,
                    RawResponseJson: purchase.RawResponseJson,
                    SubscriptionState: purchase.SubscriptionState,
                    ProductIds: purchase.LineItems?
                        .Select(item => item.ProductId?.Trim())
                        .Where(productId => !string.IsNullOrWhiteSpace(productId))
                        .Cast<string>()
                        .Distinct(StringComparer.Ordinal)
                        .ToArray() ?? []),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Diagnostic persistence must never alter subscription processing.
            logger.LogError(
                ex,
                "Failed to persist encrypted Google subscription diagnostic snapshot. event_id={EventId} project_id={ProjectId} fetch_sequence={FetchSequence}",
                eventId,
                projectId,
                fetchSequence);
        }
    }

    private static (string ErrorCode, string ErrorMessage, int StatusCode) ClassifyGoogleApiFailure(
        GooglePlayApiException exception,
        bool fetchOperation)
    {
        if (IsGooglePlayConfigurationFailure(exception))
        {
            return (fetchOperation ? "google_subscription_fetch_configuration_error" : "google_subscription_acknowledge_configuration_error",
                fetchOperation
                    ? "Google Play subscription fetch cannot be completed until this project configuration is corrected."
                    : "Google Play subscription acknowledgement cannot be completed until this project configuration is corrected.",
                StatusCodes.Status503ServiceUnavailable);
        }

        if (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return (fetchOperation ? "google_subscription_not_found" : "google_subscription_acknowledge_not_found",
                fetchOperation
                    ? "Google Play subscription token was not found."
                    : "Google Play subscription token could not be acknowledged because it was not found.",
                StatusCodes.Status200OK);
        }

        if ((int?)exception.StatusCode == 410)
        {
            return (fetchOperation ? "google_subscription_gone" : "google_subscription_acknowledge_gone",
                fetchOperation
                    ? "Google Play subscription token is no longer available."
                    : "Google Play subscription token is no longer available for acknowledgement.",
                StatusCodes.Status200OK);
        }

        if (exception.IsTransient)
        {
            return (exception.ErrorCode, exception.Message, StatusCodes.Status503ServiceUnavailable);
        }

        return (exception.ErrorCode, exception.Message, StatusCodes.Status200OK);
    }

    private static bool IsRetryableGooglePushJwtFailure(GooglePubSubPushJwtValidationException exception)
        => exception.IsTransient
            || exception.ErrorCode == "invalid_google_pubsub_token_email";

    private static bool IsGooglePlayConfigurationFailure(GooglePlayApiException exception)
        => exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            || exception.ErrorCode is "missing_google_service_account_credentials"
                or "google_access_token_token_response_error"
                or "google_access_token_credential_error";

    private static string CreateSubscriptionEventType(int notificationType)
    {
        var label = notificationType switch
        {
            1 => "SUBSCRIPTION_RECOVERED",
            2 => "SUBSCRIPTION_RENEWED",
            3 => "SUBSCRIPTION_CANCELED",
            4 => "SUBSCRIPTION_PURCHASED",
            5 => "SUBSCRIPTION_ON_HOLD",
            6 => "SUBSCRIPTION_IN_GRACE_PERIOD",
            7 => "SUBSCRIPTION_RESTARTED",
            8 => "SUBSCRIPTION_PRICE_CHANGE_CONFIRMED",
            9 => "SUBSCRIPTION_DEFERRED",
            10 => "SUBSCRIPTION_PAUSED",
            11 => "SUBSCRIPTION_PAUSE_SCHEDULE_CHANGED",
            12 => "SUBSCRIPTION_REVOKED",
            13 => "SUBSCRIPTION_EXPIRED",
            17 => "SUBSCRIPTION_ITEMS_CHANGED",
            18 => "SUBSCRIPTION_CANCELLATION_SCHEDULED",
            19 => "SUBSCRIPTION_PRICE_CHANGE_UPDATED",
            20 => "SUBSCRIPTION_PENDING_PURCHASE_CANCELED",
            22 => "SUBSCRIPTION_PRICE_STEP_UP_CONSENT_UPDATED",
            _ => notificationType.ToString()
        };

        return $"google.subscription_notification:{label}";
    }

    private static bool TryGetBearerToken(HttpRequest request, out string bearerToken)
    {
        bearerToken = string.Empty;

        if (!request.Headers.TryGetValue("Authorization", out var authorizationHeader))
        {
            return false;
        }

        var value = authorizationHeader.ToString();
        const string prefix = "Bearer ";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var token = value[prefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        bearerToken = token;
        return true;
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
        DateTimeOffset? processedAt,
        string? providerSubscriptionId = null) =>
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
            ProviderSubscriptionId: providerSubscriptionId
        );

    private static string CreateUnverifiedEventId(string rawValue)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawValue));
        return $"unverified:{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    private static DateTimeOffset? TryParseProviderCreatedAt(string? eventTimeMillis)
    {
        if (!long.TryParse(eventTimeMillis, out var unixMillis))
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMillis);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
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

    private sealed record GooglePubSubPushEnvelope(
        [property: JsonPropertyName("message")]
        GooglePubSubPushMessage? Message
    );

    private sealed record GooglePubSubPushMessage(
        [property: JsonPropertyName("messageId")]
        string? MessageId,
        [property: JsonPropertyName("data")]
        string? Data
    );

    private sealed record GoogleDeveloperNotification(
        [property: JsonPropertyName("packageName")]
        string? PackageName,
        [property: JsonPropertyName("eventTimeMillis")]
        string? EventTimeMillis,
        [property: JsonPropertyName("subscriptionNotification")]
        GoogleSubscriptionNotification? SubscriptionNotification,
        [property: JsonPropertyName("pendingRefundReviewNotification")]
        GooglePendingRefundReviewNotification? PendingRefundReviewNotification,
        [property: JsonPropertyName("testNotification")]
        object? TestNotification
    );

    private sealed record GooglePendingRefundReviewNotification(
        [property: JsonPropertyName("version")]
        string? Version,
        [property: JsonPropertyName("pendingRefundToken")]
        string? PendingRefundToken,
        [property: JsonPropertyName("orderId")]
        string? OrderId,
        [property: JsonPropertyName("refundReason")]
        int RefundReason,
        [property: JsonPropertyName("obfuscatedAccountId")]
        string? ObfuscatedAccountId,
        [property: JsonPropertyName("obfuscatedProfileId")]
        string? ObfuscatedProfileId
    );

    private sealed record GoogleSubscriptionNotification(
        [property: JsonPropertyName("notificationType")]
        int NotificationType,
        [property: JsonPropertyName("purchaseToken")]
        string? PurchaseToken
    );
}
