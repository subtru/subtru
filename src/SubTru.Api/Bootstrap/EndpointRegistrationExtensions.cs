using SubTru.Api.Features.Access;
using SubTru.Api.Features.ApiKeys;
using SubTru.Api.Features.Projects;
using SubTru.Api.Features.ProviderConfiguration;
using SubTru.Api.Features.Subscriptions;
using SubTru.Api.Features.Webhooks.Apple;
using SubTru.Api.Features.Webhooks.Events;
using SubTru.Api.Features.Webhooks.Google;
using SubTru.Api.Features.Webhooks.Keys;
using SubTru.Api.Features.Webhooks.Stripe;
using SubTru.Api.Platform.Authentication;
using SubTru.Api.Platform.Health;

namespace SubTru.Api.Bootstrap;

internal static class EndpointRegistrationExtensions
{
    public static WebApplication MapSubTruEndpoints(this WebApplication app)
    {
        app.MapHealthEndpoints();
        app.MapStripeWebhookEndpoints();
        app.MapAppleWebhookEndpoints();
        app.MapGoogleWebhookEndpoints();

        app.UseMiddleware<ApiKeyAuthMiddleware>();

        app.MapAccessEndpoints();
        app.MapSubscriptionEndpoints();
        app.MapProjectManagementEndpoints();
        app.MapProjectSummaryEndpoints();
        app.MapApiKeysEndpoints();
        app.MapWebhookEventsEndpoints();
        app.MapWebhookKeysEndpoints();
        app.MapProviderConfigEndpoints();
        app.MapGooglePurchaseRemediationEndpoints();

        return app;
    }
}
