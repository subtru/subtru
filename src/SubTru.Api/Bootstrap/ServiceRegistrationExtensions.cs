using System.Text.Json;
using Microsoft.Extensions.Options;
using SubTru.Api.Features.Webhooks.Shared;
using SubTru.Api.Platform.Authentication;
using SubTru.Api.Platform.Health;
using SubTru.Api.Platform.Observability;
using SubTru.Infrastructure.Persistence.Postgres;
using SubTru.Infrastructure.Persistence.Postgres.ApiKeys;
using SubTru.Infrastructure.Persistence.Postgres.Authentication;
using SubTru.Infrastructure.Persistence.Postgres.Google;
using SubTru.Infrastructure.Persistence.Postgres.Projects;
using SubTru.Infrastructure.Persistence.Postgres.ProviderConfiguration;
using SubTru.Infrastructure.Persistence.Postgres.Subscriptions;
using SubTru.Infrastructure.Persistence.Postgres.WebhookEvents;
using SubTru.Infrastructure.Persistence.Postgres.WebhookKeys;
using SubTru.Infrastructure.Providers.Apple;
using SubTru.Infrastructure.Providers.Google;

namespace SubTru.Api.Bootstrap;

internal static class ServiceRegistrationExtensions
{
    public static WebApplicationBuilder AddSubTruServices(this WebApplicationBuilder builder)
    {
        ConfigureLogging(builder);

        builder.Services.AddSubTruOpenTelemetry(builder.Configuration);
        builder.Services.AddMetrics();
        builder.Services.AddSingleton<IBusinessMetrics, BusinessMetrics>();

        var connectionString =
            builder.Configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Missing connection string: Postgres");

        var adminApiKey = builder.Configuration["SUBTRU_ADMIN_API_KEY"];
        if (string.IsNullOrWhiteSpace(adminApiKey))
        {
            throw new InvalidOperationException(
                "Missing required configuration: SUBTRU_ADMIN_API_KEY");
        }

        builder.Services.AddSingleton(new NpgsqlConnectionFactory(connectionString));
        builder.Services.AddSingleton<IPostgresReadinessProbe, NpgsqlPostgresReadinessProbe>();
        builder.Services.AddSingleton<IApiReadinessCheck, ApiReadinessCheck>();

        builder.Services.AddScoped<ISubscriptionReadRepository, PostgresSubscriptionReadRepository>();
        builder.Services.AddScoped<ISubscriptionWriteRepository, PostgresSubscriptionWriteRepository>();
        builder.Services.AddScoped<IGoogleSubscriptionWriteRepository, PostgresSubscriptionWriteRepository>();
        builder.Services.AddScoped<IWebhookEventLogRepository, PostgresWebhookEventLogRepository>();
        builder.Services.AddScoped<IProcessedEventRepository, PostgresProcessedEventRepository>();

        builder.Services.AddScoped<IGoogleServiceAccountValidator, GoogleServiceAccountValidator>();
        builder.Services.AddScoped<IGooglePubSubPushJwtValidator, GooglePubSubPushJwtValidator>();
        builder.Services.AddScoped<IGooglePurchaseRemediationAuditRepository, PostgresGooglePurchaseRemediationAuditRepository>();
        builder.Services.AddHttpClient<IGooglePlaySubscriptionClient, GooglePlaySubscriptionClient>();

        builder.Services.AddOptions<WebhookUrlOptions>()
            .BindConfiguration("WebhookUrls")
            .PostConfigure(options =>
            {
                options.PublicBaseUrl = WebhookUrlBuilder.NormalizeConfiguredBaseUrl(options.PublicBaseUrl);
            });

        builder.Services.AddOptions<AppleVerificationOptions>()
            .BindConfiguration("AppleVerification")
            .PostConfigure(options =>
            {
                if (string.IsNullOrWhiteSpace(options.RootCertificateDirectory))
                {
                    throw new InvalidOperationException(
                        "Missing required configuration: AppleVerification__RootCertificateDirectory");
                }

                if (options.OnlineRevocationTimeout <= TimeSpan.Zero)
                {
                    throw new InvalidOperationException(
                        "AppleVerification__OnlineRevocationTimeout must be greater than zero.");
                }
            });

        builder.Services.AddSingleton(sp =>
            AppleTrustStore.LoadFromDirectory(
                sp.GetRequiredService<IOptions<AppleVerificationOptions>>().Value));

        builder.Services.AddScoped<IAppleSignedPayloadVerifier>(sp =>
            new AppleSignedPayloadVerifier(
                sp.GetRequiredService<IOptions<AppleVerificationOptions>>().Value,
                sp.GetRequiredService<AppleTrustStore>()));

        builder.Services.AddScoped<IProjectSummaryRepository, PostgresProjectSummaryRepository>();
        builder.Services.AddScoped<IProjectManagementRepository, PostgresProjectManagementRepository>();

        var apiKeyPepper = builder.Configuration["API_KEY_PEPPER"]
            ?? throw new InvalidOperationException("Missing required configuration: API_KEY_PEPPER");

        builder.Services.AddSingleton<IProjectAuthService>(sp =>
            new PostgresProjectAuthService(
                sp.GetRequiredService<NpgsqlConnectionFactory>(),
                apiKeyPepper));

        builder.Services.AddScoped<IApiKeyManagementRepository>(sp =>
            new PostgresApiKeyManagementRepository(
                sp.GetRequiredService<NpgsqlConnectionFactory>(),
                apiKeyPepper));

        var encryptionKey = builder.Configuration["SUBTRU_ENCRYPTION_KEY"]
            ?? throw new InvalidOperationException(
                "Missing required configuration: SUBTRU_ENCRYPTION_KEY");

        builder.Services.AddScoped<IGoogleSubscriptionSnapshotRepository>(sp =>
            new PostgresGoogleSubscriptionSnapshotRepository(
                sp.GetRequiredService<NpgsqlConnectionFactory>(),
                encryptionKey));

        builder.Services.AddScoped<IProviderConfigRepository, PostgresProviderConfigRepository>(sp =>
            new PostgresProviderConfigRepository(
                sp.GetRequiredService<NpgsqlConnectionFactory>(),
                encryptionKey));

        builder.Services.AddScoped<IWebhookKeyRepository>(sp =>
            new PostgresWebhookKeyRepository(
                sp.GetRequiredService<NpgsqlConnectionFactory>(),
                apiKeyPepper,
                encryptionKey));

        builder.Services.AddScoped<IWebhookKeyManagementRepository>(sp =>
            new PostgresWebhookKeyManagementRepository(
                sp.GetRequiredService<NpgsqlConnectionFactory>(),
                apiKeyPepper,
                encryptionKey));

        builder.Services.AddHttpClient();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IProjectResolver>(new AdminProjectResolver(adminApiKey));
        builder.Services.AddScoped<IProjectResolver, ApiKeyProjectResolver>();

        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        return builder;
    }

    private static void ConfigureLogging(WebApplicationBuilder builder)
    {
        if (!builder.Environment.IsDevelopment())
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddJsonConsole(options =>
            {
                options.IncludeScopes = true;
                options.UseUtcTimestamp = true;
                options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
                options.JsonWriterOptions = new JsonWriterOptions { Indented = false };
            });
        }

        builder.Logging.AddFilter("System.Net.Http.HttpClient.OtlpTraceExporter", LogLevel.Warning);
        builder.Logging.AddFilter("System.Net.Http.HttpClient.OtlpMetricExporter", LogLevel.Warning);
    }
}
