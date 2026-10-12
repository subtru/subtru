using Microsoft.Extensions.Logging;

namespace SubTru.Api.Platform.Observability;

internal static partial class ProductEventLogging
{
    private const string Category = "SubTru.ProductEvents";
    private const string Actor = "installation_admin";

    private static ILogger CreateLogger(ILoggerFactory loggerFactory)
        => loggerFactory.CreateLogger(Category);

    public static void LogApiKeyCreated(
        ILoggerFactory loggerFactory,
        Guid projectId,
        Guid apiKeyId,
        string keyId,
        string environment)
        => LogApiKeyCreatedCore(
            CreateLogger(loggerFactory),
            "api_key.created",
            projectId,
            apiKeyId,
            keyId,
            environment,
            Actor);

    public static void LogApiKeyRevoked(
        ILoggerFactory loggerFactory,
        Guid projectId,
        Guid apiKeyId)
        => LogApiKeyRevokedCore(
            CreateLogger(loggerFactory),
            "api_key.revoked",
            projectId,
            apiKeyId,
            Actor);

    public static void LogWebhookKeyCreated(
        ILoggerFactory loggerFactory,
        Guid projectId,
        Guid webhookKeyId,
        string keyId,
        string provider,
        string environment)
        => LogWebhookKeyCreatedCore(
            CreateLogger(loggerFactory),
            "webhook_key.created",
            projectId,
            webhookKeyId,
            keyId,
            provider,
            environment,
            Actor);

    public static void LogWebhookKeyDeactivated(
        ILoggerFactory loggerFactory,
        Guid projectId,
        Guid webhookKeyId)
        => LogWebhookKeyDeactivatedCore(
            CreateLogger(loggerFactory),
            "webhook_key.deactivated",
            projectId,
            webhookKeyId,
            Actor);

    public static void LogWebhookKeySecretRotated(
        ILoggerFactory loggerFactory,
        Guid projectId,
        Guid webhookKeyId)
        => LogWebhookKeySecretRotatedCore(
            CreateLogger(loggerFactory),
            "webhook_key.secret_rotated",
            projectId,
            webhookKeyId,
            Actor);

    public static void LogProviderConfigUpserted(
        ILoggerFactory loggerFactory,
        Guid projectId,
        string provider)
        => LogProviderConfigUpsertedCore(
            CreateLogger(loggerFactory),
            "provider_config.upserted",
            projectId,
            provider,
            Actor);

    public static void LogGoogleProviderConfigUpserted(
        ILoggerFactory loggerFactory,
        Guid projectId,
        bool serviceAccountCredentialsUpdated)
        => LogGoogleProviderConfigUpsertedCore(
            CreateLogger(loggerFactory),
            "provider_config.upserted",
            projectId,
            "google",
            serviceAccountCredentialsUpdated,
            Actor);

    public static void LogProjectCreated(
        ILoggerFactory loggerFactory,
        Guid projectId)
        => LogProjectCreatedCore(
            CreateLogger(loggerFactory),
            "project.created",
            projectId);

    public static void LogProjectUpdated(
        ILoggerFactory loggerFactory,
        Guid projectId,
        bool isActive)
        => LogProjectUpdatedCore(
            CreateLogger(loggerFactory),
            "project.updated",
            projectId,
            isActive);

    [LoggerMessage(
        EventId = 5001,
        Level = LogLevel.Information,
        Message = "Product event {product_event}. project_id={project_id} api_key_id={api_key_id} key_id={key_id} environment={environment} actor={actor}")]
    private static partial void LogApiKeyCreatedCore(
        ILogger logger,
        string product_event,
        Guid project_id,
        Guid api_key_id,
        string key_id,
        string environment,
        string actor);

    [LoggerMessage(
        EventId = 5002,
        Level = LogLevel.Information,
        Message = "Product event {product_event}. project_id={project_id} api_key_id={api_key_id} actor={actor}")]
    private static partial void LogApiKeyRevokedCore(
        ILogger logger,
        string product_event,
        Guid project_id,
        Guid api_key_id,
        string actor);

    [LoggerMessage(
        EventId = 5011,
        Level = LogLevel.Information,
        Message = "Product event {product_event}. project_id={project_id} webhook_key_id={webhook_key_id} key_id={key_id} provider={provider} environment={environment} actor={actor}")]
    private static partial void LogWebhookKeyCreatedCore(
        ILogger logger,
        string product_event,
        Guid project_id,
        Guid webhook_key_id,
        string key_id,
        string provider,
        string environment,
        string actor);

    [LoggerMessage(
        EventId = 5012,
        Level = LogLevel.Information,
        Message = "Product event {product_event}. project_id={project_id} webhook_key_id={webhook_key_id} actor={actor}")]
    private static partial void LogWebhookKeyDeactivatedCore(
        ILogger logger,
        string product_event,
        Guid project_id,
        Guid webhook_key_id,
        string actor);

    [LoggerMessage(
        EventId = 5013,
        Level = LogLevel.Information,
        Message = "Product event {product_event}. project_id={project_id} webhook_key_id={webhook_key_id} actor={actor}")]
    private static partial void LogWebhookKeySecretRotatedCore(
        ILogger logger,
        string product_event,
        Guid project_id,
        Guid webhook_key_id,
        string actor);

    [LoggerMessage(
        EventId = 5021,
        Level = LogLevel.Information,
        Message = "Product event {product_event}. project_id={project_id} provider={provider} actor={actor}")]
    private static partial void LogProviderConfigUpsertedCore(
        ILogger logger,
        string product_event,
        Guid project_id,
        string provider,
        string actor);

    [LoggerMessage(
        EventId = 5022,
        Level = LogLevel.Information,
        Message = "Product event {product_event}. project_id={project_id} provider={provider} service_account_credentials_updated={service_account_credentials_updated} actor={actor}")]
    private static partial void LogGoogleProviderConfigUpsertedCore(
        ILogger logger,
        string product_event,
        Guid project_id,
        string provider,
        bool service_account_credentials_updated,
        string actor);

    [LoggerMessage(
        EventId = 5031,
        Level = LogLevel.Information,
        Message = "Product event {product_event}. project_id={project_id}")]
    private static partial void LogProjectCreatedCore(
        ILogger logger,
        string product_event,
        Guid project_id);

    [LoggerMessage(
        EventId = 5032,
        Level = LogLevel.Information,
        Message = "Product event {product_event}. project_id={project_id} is_active={is_active}")]
    private static partial void LogProjectUpdatedCore(
        ILogger logger,
        string product_event,
        Guid project_id,
        bool is_active);
}
