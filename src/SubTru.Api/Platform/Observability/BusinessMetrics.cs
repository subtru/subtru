using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SubTru.Api.Platform.Observability;

internal sealed class BusinessMetrics : IBusinessMetrics
{
    public const string MeterName = "SubTru.Business";

    public const string WebhookEventsName = "subtru.webhook.events";
    public const string WebhookProcessingDurationName = "subtru.webhook.processing.duration";
    public const string WebhookAcknowledgementsName = "subtru.webhook.acknowledgements";
    public const string AccessChecksName = "subtru.access_checks";

    private const string NoErrorCode = "none";
    private const string OtherErrorCode = "other";
    private const string Unknown = "unknown";

    private readonly Counter<long> _webhookEvents;
    private readonly Histogram<double> _webhookProcessingDuration;
    private readonly Counter<long> _webhookAcknowledgements;
    private readonly Counter<long> _accessChecks;

    private static readonly HashSet<string> AllowedProviders = new(StringComparer.Ordinal)
    {
        "apple",
        "google",
        "stripe",
    };

    private static readonly HashSet<string> AllowedWebhookStatuses = new(StringComparer.Ordinal)
    {
        "duplicate",
        "failed",
        "ignored",
        "success",
        Unknown,
    };

    private static readonly HashSet<string> AllowedAcknowledgementStatuses = new(StringComparer.Ordinal)
    {
        "failed",
        "success",
        Unknown,
    };

    private static readonly HashSet<string> AllowedAccessStatuses = new(StringComparer.Ordinal)
    {
        "denied",
        "granted",
        Unknown,
    };

    private static readonly HashSet<string> AllowedAccessReasons = new(StringComparer.Ordinal)
    {
        "active_subscription",
        "canceled_subscription",
        "none",
        "expired_subscription",
        "paused_subscription",
        "payment_failed_in_grace_period",
        "payment_failed_past_grace_period",
        "trialing_subscription",
        Unknown,
    };

    private static readonly HashSet<string> AllowedErrorCodes = new(StringComparer.Ordinal)
    {
        NoErrorCode,
        "apple_environment_mismatch",
        "app_apple_id_mismatch",
        "bundle_id_mismatch",
        "environment_mismatch",
        "google_access_token_credential_error",
        "google_access_token_empty",
        "google_access_token_network_error",
        "google_access_token_timeout",
        "google_access_token_token_response_error",
        "google_package_name_mismatch",
        "google_pending_purchase_canceled",
        "google_provider_config_lookup_failed",
        "google_pubsub_jwt_validation_network_error",
        "google_pubsub_jwt_validation_timeout",
        "google_subscription_acknowledge_configuration_error",
        "google_subscription_acknowledge_failed",
        "google_subscription_acknowledge_gone",
        "google_subscription_acknowledge_network_error",
        "google_subscription_acknowledge_not_found",
        "google_subscription_acknowledge_timeout",
        "google_subscription_fetch_configuration_error",
        "google_subscription_fetch_failed",
        "google_subscription_fetch_network_error",
        "google_subscription_fetch_timeout",
        "google_subscription_gone",
        "google_subscription_not_found",
        "google_unbound_purchase",
        "invalid_apple_certificate_chain",
        "invalid_apple_environment",
        "invalid_apple_signature",
        "invalid_apple_signed_payload",
        "invalid_apple_timestamp",
        "invalid_google_expiry_time",
        "invalid_google_pubsub_bearer_token",
        "invalid_google_pubsub_json",
        "invalid_google_pubsub_message",
        "invalid_google_pubsub_message_data",
        "invalid_google_pubsub_token_email",
        "invalid_google_pubsub_token_email_verified",
        "invalid_google_pubsub_token_issuer",
        "invalid_google_rtdn_payload",
        "invalid_google_subscription_response",
        "invalid_json",
        "missing_app_apple_id",
        "missing_apple_certificate_chain",
        "missing_apple_environment",
        "missing_apple_purchase_type",
        "missing_apple_provider_config",
        "missing_bundle_id",
        "missing_current_period_end",
        "missing_ended_at",
        "missing_environment",
        "missing_google_product_id",
        "missing_google_provider_config",
        "missing_google_provider_credentials",
        "missing_google_pubsub_bearer_token",
        "missing_google_purchase_token",
        "missing_google_service_account_credentials",
        "missing_google_subscription_state",
        "missing_notification_data",
        "missing_notification_type",
        "missing_notification_uuid",
        "missing_or_invalid_app_account_token",
        "missing_or_invalid_google_event_time",
        "missing_or_invalid_refund_subscription_status",
        "missing_or_invalid_subtru_external_user_id",
        "missing_original_transaction_id",
        "missing_price_id",
        "missing_product_id",
        "missing_provider_subscription_id",
        "missing_refund_original_transaction_id",
        "missing_refund_renewal_info",
        "missing_signed_date",
        "missing_signed_payload",
        "missing_signed_transaction_info",
        "missing_subscription_items",
        "missing_transaction_id",
        "original_transaction_id_mismatch",
        "processing_failed",
        "refund_original_transaction_id_mismatch",
        "retryable_apple_certificate_chain_verification",
        "stale_provider_event",
        "stale_provider_snapshot",
        "stripe_api_version_mismatch",
        "unexpected_event_object",
        "unsupported_apple_jws_algorithm",
        "unsupported_apple_jws_critical_header",
        "unsupported_apple_notification_type",
        "unsupported_apple_purchase_type",
        "unsupported_google_installment_subscription",
        "unsupported_google_pending_subscription_state",
        "unsupported_google_prepaid_subscription",
        "unsupported_google_subscription_shape",
        "unsupported_google_subscription_state",
        "unsupported_multiple_subscription_items",
    };


    public BusinessMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _webhookEvents = meter.CreateCounter<long>(
            WebhookEventsName,
            unit: "{event}",
            description: "Webhook processing outcomes by provider, environment, status, and error code.");

        _webhookProcessingDuration = meter.CreateHistogram<double>(
            WebhookProcessingDurationName,
            unit: "s",
            description: "Webhook processing duration in seconds by provider, environment, status, and error code.");

        _webhookAcknowledgements = meter.CreateCounter<long>(
            WebhookAcknowledgementsName,
            unit: "{acknowledgement}",
            description: "Provider acknowledgement outcomes by provider, environment, status, and error code.");

        _accessChecks = meter.CreateCounter<long>(
            AccessChecksName,
            unit: "{check}",
            description: "Access check outcomes by environment, status, and reason.");
    }

    public void RecordWebhookEvent(
        string provider,
        string environment,
        string status,
        string? errorCode,
        TimeSpan processingDuration)
    {
        var tags = CreateWebhookTags(provider, environment, status, errorCode);

        _webhookEvents.Add(1, tags);
        _webhookProcessingDuration.Record(Math.Max(0, processingDuration.TotalSeconds), tags);
    }

    public void RecordWebhookAcknowledgement(
        string provider,
        string environment,
        string status,
        string? errorCode)
    {
        _webhookAcknowledgements.Add(
            1,
            new TagList
            {
                { "provider", Normalize(provider, AllowedProviders, Unknown) },
                { "environment", NormalizeEnvironment(environment) },
                { "status", Normalize(status, AllowedAcknowledgementStatuses, Unknown) },
                { "error_code", NormalizeErrorCode(errorCode) },
            });
    }

    public void RecordAccessCheck(
        string environment,
        string status,
        string reason)
    {
        _accessChecks.Add(
            1,
            new TagList
            {
                { "environment", NormalizeEnvironment(environment) },
                { "status", Normalize(status, AllowedAccessStatuses, Unknown) },
                { "reason", Normalize(reason, AllowedAccessReasons, Unknown) },
            });
    }

    private static TagList CreateWebhookTags(
        string provider,
        string environment,
        string status,
        string? errorCode)
        => new()
        {
            { "provider", Normalize(provider, AllowedProviders, Unknown) },
            { "environment", NormalizeEnvironment(environment) },
            { "status", Normalize(status, AllowedWebhookStatuses, Unknown) },
            { "error_code", NormalizeErrorCode(errorCode) },
        };

    private static string NormalizeEnvironment(string? value)
        => Normalize(value, AllowedValues.SubscriptionEnvironments, Unknown);

    private static string NormalizeErrorCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return NoErrorCode;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return AllowedErrorCodes.Contains(normalized) ? normalized : OtherErrorCode;
    }

    private static string Normalize(string? value, HashSet<string> allowedValues, string fallback)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is not null && allowedValues.Contains(normalized)
            ? normalized
            : fallback;
    }

    private static class AllowedValues
    {
        public static readonly HashSet<string> SubscriptionEnvironments = new(StringComparer.Ordinal)
        {
            "production",
            "test",
        };
    }
}
