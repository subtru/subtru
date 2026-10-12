using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using SubTru.Api.Platform.Observability;

namespace SubTru.Tests.Platform.Observability;

public sealed class BusinessMetricsTests
{
    [Theory]
    [InlineData("missing_or_invalid_app_account_token")]
    [InlineData("unsupported_multiple_subscription_items")]
    [InlineData("refund_original_transaction_id_mismatch")]
    [InlineData("invalid_apple_certificate_chain")]
    [InlineData("invalid_apple_signature")]
    [InlineData("invalid_google_pubsub_token_issuer")]
    [InlineData("google_subscription_fetch_timeout")]
    [InlineData("stale_provider_snapshot")]
    [InlineData("stripe_api_version_mismatch")]
    public void RecordWebhookEvent_ReportsErrorCodeVerbatim(string errorCode)
    {
        using var capture = MetricCapture.Start(BusinessMetrics.WebhookEventsName);
        using var serviceProvider = CreateServiceProvider();
        var metrics = serviceProvider.GetRequiredService<BusinessMetrics>();

        metrics.RecordWebhookEvent("apple", "test", "failed", errorCode, TimeSpan.Zero);

        Assert.Contains(capture.LongMeasurements, measurement =>
            measurement.InstrumentName == BusinessMetrics.WebhookEventsName
            && HasTags(measurement.Tags, ("error_code", errorCode)));
    }

    [Fact]
    public void RecordWebhookEvent_BucketsMalformedErrorCodeAsOther()
    {
        using var capture = MetricCapture.Start(BusinessMetrics.WebhookEventsName);
        using var serviceProvider = CreateServiceProvider();
        var metrics = serviceProvider.GetRequiredService<BusinessMetrics>();

        metrics.RecordWebhookEvent("apple", "test", "failed", "Not A Code", TimeSpan.Zero);

        Assert.Contains(capture.LongMeasurements, measurement =>
            measurement.InstrumentName == BusinessMetrics.WebhookEventsName
            && HasTags(measurement.Tags, ("error_code", "other")));
    }

    [Fact]
    public void RecordWebhookEvent_BucketsUnknownWellFormedErrorCodeAsOther()
    {
        using var capture = MetricCapture.Start(BusinessMetrics.WebhookEventsName);
        using var serviceProvider = CreateServiceProvider();
        var metrics = serviceProvider.GetRequiredService<BusinessMetrics>();

        metrics.RecordWebhookEvent("apple", "test", "failed", "future_provider_error", TimeSpan.Zero);

        Assert.Contains(capture.LongMeasurements, measurement =>
            measurement.InstrumentName == BusinessMetrics.WebhookEventsName
            && HasTags(measurement.Tags, ("error_code", "other")));
    }

    [Fact]
    public void RecordWebhookEvent_EmitsCounterAndDurationWithBoundedTags()
    {
        using var capture = MetricCapture.Start(
            BusinessMetrics.WebhookEventsName,
            BusinessMetrics.WebhookProcessingDurationName);
        using var serviceProvider = CreateServiceProvider();
        var metrics = serviceProvider.GetRequiredService<BusinessMetrics>();

        metrics.RecordWebhookEvent(
            "stripe",
            "test",
            "failed",
            "stripe_api_version_mismatch",
            TimeSpan.FromMilliseconds(250));

        Assert.Contains(capture.LongMeasurements, measurement =>
            measurement.InstrumentName == BusinessMetrics.WebhookEventsName
            && measurement.Value == 1
            && HasTags(
                measurement.Tags,
                ("provider", "stripe"),
                ("environment", "test"),
                ("status", "failed"),
                ("error_code", "stripe_api_version_mismatch")));

        Assert.Contains(capture.DoubleMeasurements, measurement =>
            measurement.InstrumentName == BusinessMetrics.WebhookProcessingDurationName
            && Math.Abs(measurement.Value - 0.25) < 0.001
            && HasTags(
                measurement.Tags,
                ("provider", "stripe"),
                ("environment", "test"),
                ("status", "failed"),
                ("error_code", "stripe_api_version_mismatch")));
    }

    [Fact]
    public void RecordWebhookAcknowledgement_MalformedErrorCode_IsBucketed()
    {
        using var capture = MetricCapture.Start(BusinessMetrics.WebhookAcknowledgementsName);
        using var serviceProvider = CreateServiceProvider();
        var metrics = serviceProvider.GetRequiredService<BusinessMetrics>();

        metrics.RecordWebhookAcknowledgement(
            "google",
            "production",
            "failed",
            "External provider noisy error!");

        Assert.Contains(capture.LongMeasurements, measurement =>
            measurement.InstrumentName == BusinessMetrics.WebhookAcknowledgementsName
            && measurement.Value == 1
            && HasTags(
                measurement.Tags,
                ("provider", "google"),
                ("environment", "production"),
                ("status", "failed"),
                ("error_code", "other")));
    }

    [Fact]
    public void RecordAccessCheck_EmitsBoundedTags()
    {
        using var capture = MetricCapture.Start(BusinessMetrics.AccessChecksName);
        using var serviceProvider = CreateServiceProvider();
        var metrics = serviceProvider.GetRequiredService<BusinessMetrics>();

        metrics.RecordAccessCheck("test", "granted", "active_subscription");

        Assert.Contains(capture.LongMeasurements, measurement =>
            measurement.InstrumentName == BusinessMetrics.AccessChecksName
            && measurement.Value == 1
            && HasTags(
                measurement.Tags,
                ("environment", "test"),
                ("status", "granted"),
                ("reason", "active_subscription")));
    }

    [Fact]
    public void RecordAccessCheck_UnexpectedStatusAndReason_AreBucketedAsUnknown()
    {
        using var capture = MetricCapture.Start(BusinessMetrics.AccessChecksName);
        using var serviceProvider = CreateServiceProvider();
        var metrics = serviceProvider.GetRequiredService<BusinessMetrics>();

        metrics.RecordAccessCheck("test", "future_status", "future_reason");

        Assert.Contains(capture.LongMeasurements, measurement =>
            measurement.InstrumentName == BusinessMetrics.AccessChecksName
            && measurement.Value == 1
            && HasTags(
                measurement.Tags,
                ("environment", "test"),
                ("status", "unknown"),
                ("reason", "unknown")));
    }

    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton<BusinessMetrics>();
        return services.BuildServiceProvider();
    }

    private static bool HasTags(
        IReadOnlyDictionary<string, object?> tags,
        params (string Key, string Value)[] expectedTags)
        => expectedTags.All(tag =>
            tags.TryGetValue(tag.Key, out var value)
            && string.Equals(Convert.ToString(value), tag.Value, StringComparison.Ordinal));

    private sealed class MetricCapture : IDisposable
    {
        private readonly HashSet<string> _instrumentNames;
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<CapturedLongMeasurement> _longMeasurements = new();
        private readonly ConcurrentQueue<CapturedDoubleMeasurement> _doubleMeasurements = new();

        private MetricCapture(IEnumerable<string> instrumentNames)
        {
            _instrumentNames = new HashSet<string>(instrumentNames, StringComparer.Ordinal);
        }

        public IReadOnlyCollection<CapturedLongMeasurement> LongMeasurements
            => _longMeasurements.ToArray();

        public IReadOnlyCollection<CapturedDoubleMeasurement> DoubleMeasurements
            => _doubleMeasurements.ToArray();

        public static MetricCapture Start(params string[] instrumentNames)
        {
            var capture = new MetricCapture(instrumentNames);
            capture.Start();
            return capture;
        }

        public void Dispose()
            => _listener.Dispose();

        private void Start()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == BusinessMetrics.MeterName
                    && _instrumentNames.Contains(instrument.Name))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };

            _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            {
                _longMeasurements.Enqueue(new CapturedLongMeasurement(
                    instrument.Name,
                    measurement,
                    ToDictionary(tags)));
            });

            _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
            {
                _doubleMeasurements.Enqueue(new CapturedDoubleMeasurement(
                    instrument.Name,
                    measurement,
                    ToDictionary(tags)));
            });

            _listener.Start();
        }
    }

    private static Dictionary<string, object?> ToDictionary(
        ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var result = new Dictionary<string, object?>();
        foreach (var tag in tags)
        {
            result[tag.Key] = tag.Value;
        }

        return result;
    }

    private sealed record CapturedLongMeasurement(
        string InstrumentName,
        long Value,
        IReadOnlyDictionary<string, object?> Tags);

    private sealed record CapturedDoubleMeasurement(
        string InstrumentName,
        double Value,
        IReadOnlyDictionary<string, object?> Tags);
}
