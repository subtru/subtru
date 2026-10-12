using System.Globalization;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace SubTru.Api.Platform.Observability;

internal static class OpenTelemetryRegistration
{
    private const string DefaultServiceName = "subtru-api";

    public static IServiceCollection AddSubTruOpenTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOpenTelemetry()
            .ConfigureResource(resource =>
                resource.AddService(GetServiceName(configuration)))
            .WithTracing(tracing =>
            {
                tracing
                    .SetSampler(CreateSampler(configuration))
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddNpgsql();

                if (HasOtlpEndpoint(configuration))
                {
                    tracing.AddOtlpExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(BusinessMetrics.MeterName)
                    .AddAspNetCoreInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddProcessInstrumentation()
                    .AddView(
                        BusinessMetrics.WebhookProcessingDurationName,
                        new ExplicitBucketHistogramConfiguration
                        {
                            Boundaries =
                            [
                                0.005,
                                0.01,
                                0.025,
                                0.05,
                                0.075,
                                0.1,
                                0.25,
                                0.5,
                                0.75,
                                1,
                                2.5,
                                5,
                                7.5,
                                10
                            ]
                        });

                if (HasOtlpEndpoint(configuration))
                {
                    metrics.AddOtlpExporter();
                }
            });

        return services;
    }

    private static ParentBasedSampler CreateSampler(IConfiguration configuration)
    {
        var sampleRatio = 1.0;
        var configuredRatio = configuration["OTEL_TRACES_SAMPLER_ARG"];

        if (!string.IsNullOrWhiteSpace(configuredRatio)
            && (!double.TryParse(
                    configuredRatio,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out sampleRatio)
                || sampleRatio is < 0.0 or > 1.0))
        {
            throw new InvalidOperationException(
                "OTEL_TRACES_SAMPLER_ARG must be a number between 0.0 and 1.0.");
        }

        return new ParentBasedSampler(new TraceIdRatioBasedSampler(sampleRatio));
    }

    private static string GetServiceName(IConfiguration configuration)
        => string.IsNullOrWhiteSpace(configuration["OTEL_SERVICE_NAME"])
            ? DefaultServiceName
            : configuration["OTEL_SERVICE_NAME"]!;

    private static bool HasOtlpEndpoint(IConfiguration configuration)
        => !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
}
