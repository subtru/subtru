using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SubTru.Api.Features.Webhooks.Google;
using SubTru.Domain.Subscriptions;
using SubTru.Infrastructure.Providers.Google;
using SubTru.Infrastructure.Persistence.Postgres.Subscriptions;
using SubTru.Infrastructure.Persistence.Postgres.ProviderConfiguration;
using SubTru.Infrastructure.Persistence.Postgres.Google;
using SubTru.Infrastructure.Persistence.Postgres.WebhookEvents;
using SubTru.Infrastructure.Persistence.Postgres.WebhookKeys;

namespace SubTru.Tests.Features.Webhooks.Google;

public sealed class GoogleWebhookEndpointTests : IClassFixture<GoogleWebhookWebApplicationFactory>
{
    private readonly GoogleWebhookWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public GoogleWebhookEndpointTests(GoogleWebhookWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.ResetState();
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PostGoogleWebhook_InvalidWebhookKey_ReturnsOk()
    {
        var response = await _client.PostAsync("/webhooks/google/invalid-key", CreateJsonContent(new { }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostGoogleWebhook_MissingBearerToken_ReturnsOk()
    {
        var response = await _client.PostAsync(
            "/webhooks/google/subtru_wh_google.secret",
            CreatePubSubRequest(new
            {
                packageName = "com.example.app",
                eventTimeMillis = "1712200000000",
                subscriptionNotification = new
                {
                    notificationType = 4,
                    purchaseToken = "token-active"
                }
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_factory.WebhookEventLogRepository.Models, x => x.ErrorCode == "missing_google_pubsub_bearer_token");
    }

    [Fact]
    public async Task PostGoogleWebhook_InvalidBearerToken_ReturnsOk()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/webhooks/google/subtru_wh_google.secret")
        {
            Content = CreatePubSubRequest(new
            {
                packageName = "com.example.app",
                eventTimeMillis = "1712200000000",
                subscriptionNotification = new
                {
                    notificationType = 4,
                    purchaseToken = "token-active"
                }
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-google-push-token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_factory.WebhookEventLogRepository.Models, x => x.ErrorCode == "invalid_google_pubsub_bearer_token");
    }

    [Fact]
    public async Task PostGoogleWebhook_ValidBearerTokenWithOversizedPayload_ReturnsPayloadTooLarge()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/webhooks/google/subtru_wh_google.secret")
        {
            Content = new StringContent(
                new string('x', WebhookRequestBodyReader.MaxBodySizeBytes + 1),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid-google-push-token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task PostGoogleWebhook_TransientJwtValidationFailure_ReturnsServiceUnavailable()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/webhooks/google/subtru_wh_google.secret")
        {
            Content = CreatePubSubRequest(new
            {
                packageName = "com.example.app",
                eventTimeMillis = "1712200000000",
                subscriptionNotification = new
                {
                    notificationType = 4,
                    purchaseToken = "token-active"
                }
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "transient-google-push-token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains(_factory.WebhookEventLogRepository.Models, x => x.ErrorCode == "google_pubsub_jwt_validation_unavailable");
    }

    [Fact]
    public async Task PostGoogleWebhook_PushAuthServiceAccountMismatch_ReturnsServiceUnavailable()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/webhooks/google/subtru_wh_google.secret")
        {
            Content = CreatePubSubRequest(new
            {
                packageName = "com.example.app",
                eventTimeMillis = "1712200000000",
                subscriptionNotification = new
                {
                    notificationType = 4,
                    purchaseToken = "token-active"
                }
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-google-push-email-token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains(_factory.WebhookEventLogRepository.Models, x => x.ErrorCode == "invalid_google_pubsub_token_email");
    }

    [Fact]
    public async Task PostGoogleWebhook_MissingProviderConfig_ReturnsServiceUnavailable()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/google/subtru_wh_google_missing_config.secret")
        {
            Content = CreatePubSubRequest(new
            {
                packageName = "com.example.app",
                eventTimeMillis = "1712200000000",
                subscriptionNotification = new
                {
                    notificationType = 4,
                    purchaseToken = "token-active"
                }
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid-google-push-token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains(_factory.WebhookEventLogRepository.Models, x => x.ErrorCode == "missing_google_provider_config");
    }

    [Fact]
    public async Task PostGoogleWebhook_MissingProviderCredentials_ReturnsServiceUnavailable()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/google/subtru_wh_google_missing_credentials.secret")
        {
            Content = CreatePubSubRequest(new
            {
                packageName = "com.example.app",
                eventTimeMillis = "1712200000000",
                subscriptionNotification = new
                {
                    notificationType = 4,
                    purchaseToken = "token-active"
                }
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid-google-push-token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains(_factory.WebhookEventLogRepository.Models, x => x.ErrorCode == "missing_google_provider_credentials");
    }

    [Fact]
    public async Task PostGoogleWebhook_TestNotification_ReturnsOk()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/google/subtru_wh_google.secret")
        {
            Content = CreatePubSubRequest(new
            {
                packageName = "com.example.app",
                eventTimeMillis = "1712200000000",
                testNotification = new { version = "1.0" }
            }, messageId: "msg-test")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid-google-push-token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_factory.WebhookEventLogRepository.Models, x => x.EventId == "msg-test" && x.Status == WebhookEventStatus.Ignored);
        Assert.Contains(_factory.PushJwtValidator.Calls, x =>
            x.ExpectedAudience == "https://api.test.subtru.com/webhooks/google/subtru_wh_google.secret"
            && x.ExpectedEmail == "push-auth@example.iam.gserviceaccount.com");
    }

    [Fact]
    public async Task PostGoogleWebhook_PendingRefundReview_LogsDistinctIgnoredEventWithoutChangingSubscription()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/google/subtru_wh_google.secret")
        {
            Content = CreatePubSubRequest(new
            {
                packageName = "com.example.app",
                eventTimeMillis = "1712200000000",
                pendingRefundReviewNotification = new
                {
                    version = "1.0",
                    pendingRefundToken = "pending-refund-token",
                    orderId = "GPA.1234-5678-9012-34567",
                    refundReason = 7,
                    obfuscatedAccountId = "test-user"
                }
            }, messageId: "msg-pending-refund-review")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid-google-push-token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var eventLog = Assert.Single(
            _factory.WebhookEventLogRepository.Models.Where(
                model => model.EventId == "msg-pending-refund-review"));
        Assert.Equal("google.pending_refund_review", eventLog.EventType);
        Assert.Equal(WebhookEventStatus.Ignored, eventLog.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1712200000000), eventLog.ProviderCreatedAt);
        Assert.Empty(_factory.GooglePlayClient.FetchCalls);
        Assert.Empty(_factory.GooglePlayClient.AcknowledgeCalls);
        Assert.Empty(_factory.GoogleWriteRepository.Writes);
    }

    [Fact]
    public async Task PostGoogleWebhook_InvalidEventTime_LogsFailureWithoutFetchingOrWriting()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/google/subtru_wh_google.secret")
        {
            Content = CreatePubSubRequest(new
            {
                packageName = "com.example.app",
                eventTimeMillis = "not-a-timestamp",
                subscriptionNotification = new
                {
                    notificationType = 4,
                    purchaseToken = "token-active"
                }
            }, messageId: "msg-invalid-event-time")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid-google-push-token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_factory.GooglePlayClient.FetchCalls);
        Assert.Empty(_factory.GoogleWriteRepository.Writes);
        Assert.Contains(_factory.WebhookEventLogRepository.Models, x =>
            x.EventId == "msg-invalid-event-time"
            && x.Status == WebhookEventStatus.Failed
            && x.ErrorCode == "missing_or_invalid_google_event_time");
    }

    [Fact]
    public async Task PostGoogleWebhook_UsesFetchedSnapshotTimeForStateOrdering()
    {
        var rtdnEventTime = DateTimeOffset.FromUnixTimeMilliseconds(1712200000000);
        var beforeRequest = DateTimeOffset.UtcNow;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/google/subtru_wh_google.secret")
        {
            Content = CreatePubSubRequest(new
            {
                packageName = "com.example.app",
                eventTimeMillis = "1712200000000",
                subscriptionNotification = new
                {
                    notificationType = 4,
                    purchaseToken = "token-active"
                }
            }, messageId: "msg-snapshot-time")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid-google-push-token");

        var response = await _client.SendAsync(request);
        var afterRequest = DateTimeOffset.UtcNow;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var write = Assert.Single(
            _factory.GoogleWriteRepository.Writes.Where(
                write => write.EventId == "msg-snapshot-time"));
        Assert.NotNull(write.Subscription.ProviderEventCreatedAt);
        Assert.InRange(write.Subscription.ProviderEventCreatedAt.Value, beforeRequest, afterRequest);

        var eventLog = Assert.Single(
            _factory.WebhookEventLogRepository.Models.Where(
                model => model.EventId == "msg-snapshot-time"
                    && model.Status == WebhookEventStatus.Success));
        Assert.Equal(rtdnEventTime, eventLog.ProviderCreatedAt);

        var snapshot = Assert.Single(
            _factory.GoogleSnapshotRepository.Snapshots.Where(
                item => item.EventId == "msg-snapshot-time"));
        Assert.Equal(1, snapshot.FetchSequence);
        Assert.Equal("token-active", snapshot.PurchaseToken);
        Assert.Equal(
            "{\"subscriptionState\":\"SUBSCRIPTION_STATE_ACTIVE\",\"lineItems\":[{\"productId\":\"premium_monthly\"}]}",
            snapshot.RawResponseJson);
    }

    [Fact]
    public async Task PostGoogleWebhook_SnapshotPersistenceFailure_DoesNotAffectSubscriptionProcessing()
    {
        _factory.GoogleSnapshotRepository.SaveException = new InvalidOperationException("snapshot unavailable");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/google/subtru_wh_google.secret")
        {
            Content = CreatePubSubRequest(new
            {
                packageName = "com.example.app",
                eventTimeMillis = "1712200000000",
                subscriptionNotification = new
                {
                    notificationType = 4,
                    purchaseToken = "token-active"
                }
            }, messageId: "msg-snapshot-storage-failure")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid-google-push-token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            _factory.GoogleWriteRepository.Writes,
            write => write.EventId == "msg-snapshot-storage-failure");
        Assert.Contains(
            _factory.WebhookEventLogRepository.Models,
            model => model.EventId == "msg-snapshot-storage-failure"
                && model.Status == WebhookEventStatus.Success);
    }

    [Fact]
    public async Task PostGoogleWebhook_StaleFetchedSnapshot_LogsIgnored()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/google/subtru_wh_google.secret")
        {
            Content = CreatePubSubRequest(new
            {
                packageName = "com.example.app",
                eventTimeMillis = "1712200000000",
                subscriptionNotification = new
                {
                    notificationType = 4,
                    purchaseToken = "token-active"
                }
            }, messageId: "msg-stale")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid-google-push-token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_factory.WebhookEventLogRepository.Models, x =>
            x.EventId == "msg-stale"
            && x.Status == WebhookEventStatus.Ignored
            && x.ErrorCode == "stale_provider_snapshot");
        Assert.DoesNotContain(
            _factory.GooglePlayClient.AcknowledgeCalls,
            x => x.PurchaseToken == "token-active");
    }

    [Fact]
    public async Task PostGoogleWebhook_TestPurchaseSentToProductionEndpoint_LogsIgnored()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/google/subtru_wh_google.secret")
        {
            Content = CreatePubSubRequest(new
            {
                packageName = "com.example.app",
                eventTimeMillis = "1712200000000",
                subscriptionNotification = new
                {
                    notificationType = 4,
                    purchaseToken = "token-test"
                }
            }, messageId: "msg-environment-mismatch")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid-google-push-token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var eventLog = Assert.Single(_factory.WebhookEventLogRepository.Models.Where(x =>
            x.EventId == "msg-environment-mismatch"));
        Assert.Equal(WebhookEventStatus.Ignored, eventLog.Status);
        Assert.Equal("environment_mismatch", eventLog.ErrorCode);
        Assert.NotNull(eventLog.ProcessedAt);
        Assert.DoesNotContain(
            _factory.GoogleWriteRepository.Writes,
            x => x.EventId == "msg-environment-mismatch");
        Assert.DoesNotContain(
            _factory.GooglePlayClient.AcknowledgeCalls,
            x => x.PurchaseToken == "token-test");
    }

    [Fact]
    public async Task PostGoogleWebhook_PackageMismatch_ReturnsServiceUnavailable()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/google/subtru_wh_google.secret")
        {
            Content = CreatePubSubRequest(new
            {
                packageName = "com.other.app",
                eventTimeMillis = "1712200000000",
                subscriptionNotification = new
                {
                    notificationType = 4,
                    purchaseToken = "token-active"
                }
            }, messageId: "msg-package")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid-google-push-token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains(_factory.WebhookEventLogRepository.Models, x => x.EventId == "msg-package" && x.ErrorCode == "google_package_name_mismatch");
    }

    [Fact]
    public async Task PostGoogleWebhook_SubscriptionNotFound_ReturnsOk()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/google/subtru_wh_google.secret")
        {
            Content = CreatePubSubRequest(new
            {
                packageName = "com.example.app",
                eventTimeMillis = "1712200000000",
                subscriptionNotification = new
                {
                    notificationType = 4,
                    purchaseToken = "token-not-found"
                }
            }, messageId: "msg-not-found")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid-google-push-token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_factory.WebhookEventLogRepository.Models, x => x.EventId == "msg-not-found" && x.ErrorCode == "google_subscription_not_found");
    }

    [Fact]
    public async Task PostGoogleWebhook_GooglePlayFetchForbidden_ReturnsServiceUnavailable()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/google/subtru_wh_google.secret")
        {
            Content = CreatePubSubRequest(new
            {
                packageName = "com.example.app",
                eventTimeMillis = "1712200000000",
                subscriptionNotification = new
                {
                    notificationType = 4,
                    purchaseToken = "token-forbidden"
                }
            }, messageId: "msg-forbidden")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid-google-push-token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains(_factory.WebhookEventLogRepository.Models, x => x.EventId == "msg-forbidden" && x.ErrorCode == "google_subscription_fetch_configuration_error");
    }

    [Fact]
    public async Task PostGoogleWebhook_TransientGoogleFetchFailure_ReturnsServiceUnavailable()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/google/subtru_wh_google.secret")
        {
            Content = CreatePubSubRequest(new
            {
                packageName = "com.example.app",
                eventTimeMillis = "1712200000000",
                subscriptionNotification = new
                {
                    notificationType = 4,
                    purchaseToken = "token-transient-fetch"
                }
            }, messageId: "msg-transient-fetch")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid-google-push-token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains(_factory.WebhookEventLogRepository.Models, x => x.EventId == "msg-transient-fetch" && x.ErrorCode == "google_subscription_fetch_unavailable");
    }

    [Fact]
    public async Task PostGoogleWebhook_UnresolvedBinding_ReturnsOkAndDoesNotAcknowledge()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/google/subtru_wh_google.secret")
        {
            Content = CreatePubSubRequest(new
            {
                packageName = "com.example.app",
                eventTimeMillis = "1712200000000",
                subscriptionNotification = new
                {
                    notificationType = 4,
                    purchaseToken = "token-unbound"
                }
            }, messageId: "msg-unbound")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid-google-push-token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(_factory.GooglePlayClient.AcknowledgeCalls, x => x.PurchaseToken == "token-unbound");
        Assert.Contains(_factory.WebhookEventLogRepository.Models, x => x.EventId == "msg-unbound" && x.ErrorCode == "google_unbound_purchase");
    }

    [Fact]
    public async Task PostGoogleWebhook_PendingState_ReturnsOkWithoutWriteOrAcknowledge()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/google/subtru_wh_google.secret")
        {
            Content = CreatePubSubRequest(new
            {
                packageName = "com.example.app",
                eventTimeMillis = "1712200000000",
                subscriptionNotification = new
                {
                    notificationType = 4,
                    purchaseToken = "token-pending"
                }
            }, messageId: "msg-pending")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid-google-push-token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_factory.GoogleWriteRepository.Writes);
        Assert.Empty(_factory.GooglePlayClient.AcknowledgeCalls);
        Assert.Contains(_factory.WebhookEventLogRepository.Models, x => x.EventId == "msg-pending" && x.ErrorCode == "unsupported_google_pending_subscription_state");
    }

    [Fact]
    public async Task PostGoogleWebhook_PendingPurchaseCanceledWithLinkedToken_ProcessesLinkedToken()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/google/subtru_wh_google.secret")
        {
            Content = CreatePubSubRequest(new
            {
                packageName = "com.example.app",
                eventTimeMillis = "1712200000000",
                subscriptionNotification = new
                {
                    notificationType = 4,
                    purchaseToken = "token-pending-canceled"
                }
            }, messageId: "msg-linked")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid-google-push-token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_factory.GooglePlayClient.FetchCalls, x => x.PurchaseToken == "token-pending-canceled");
        Assert.Contains(_factory.GooglePlayClient.FetchCalls, x => x.PurchaseToken == "token-linked-active");
        Assert.Contains(
            _factory.GoogleWriteRepository.Writes,
            x => x.Subscription.ProviderSubscriptionId == "token-linked-active");
        Assert.Contains(_factory.GooglePlayClient.AcknowledgeCalls, x => x.PurchaseToken == "token-linked-active");
        var snapshots = _factory.GoogleSnapshotRepository.Snapshots
            .Where(item => item.EventId == "msg-linked")
            .OrderBy(item => item.FetchSequence)
            .ToArray();
        Assert.Equal(2, snapshots.Length);
        Assert.Equal("token-pending-canceled", snapshots[0].PurchaseToken);
        Assert.Equal("token-linked-active", snapshots[1].PurchaseToken);
    }

    [Fact]
    public async Task PostGoogleWebhook_ExpiredPurchaseTokenFallback_UsesStoredBinding()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/google/subtru_wh_google.secret")
        {
            Content = CreatePubSubRequest(new
            {
                packageName = "com.example.app",
                eventTimeMillis = "1712200000000",
                subscriptionNotification = new
                {
                    notificationType = 4,
                    purchaseToken = "token-expired-fallback"
                }
            }, messageId: "msg-expired-fallback")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid-google-push-token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_factory.GoogleWriteRepository.Writes, x => x.Subscription.ExternalUserId == "recovered-user");
    }

    [Fact]
    public async Task PostGoogleWebhook_MissingExpiredPurchaseBinding_FallsBackToLinkedPurchaseToken()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/google/subtru_wh_google.secret")
        {
            Content = CreatePubSubRequest(new
            {
                packageName = "com.example.app",
                eventTimeMillis = "1712200000000",
                subscriptionNotification = new
                {
                    notificationType = 4,
                    purchaseToken = "token-expired-then-linked-fallback"
                }
            }, messageId: "msg-expired-then-linked-fallback")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid-google-push-token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            _factory.GoogleWriteRepository.Writes,
            write => write.EventId == "msg-expired-then-linked-fallback"
                && write.Subscription.ExternalUserId == "linked-user");
    }

    [Fact]
    public async Task PostGoogleWebhook_CurrentPurchaseTokenFallback_UsesStoredBinding()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/google/subtru_wh_google.secret")
        {
            Content = CreatePubSubRequest(new
            {
                packageName = "com.example.app",
                eventTimeMillis = "1712200000000",
                subscriptionNotification = new
                {
                    notificationType = 4,
                    purchaseToken = "token-current-fallback"
                }
            }, messageId: "msg-current-fallback")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid-google-push-token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_factory.GoogleWriteRepository.Writes, x => x.Subscription.ExternalUserId == "current-fallback-user");
    }

    [Fact]
    public async Task PostGoogleWebhook_CurrentPurchaseTokenBinding_TakesPriorityOverLinkedPurchaseToken()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/google/subtru_wh_google.secret")
        {
            Content = CreatePubSubRequest(new
            {
                packageName = "com.example.app",
                eventTimeMillis = "1712200000000",
                subscriptionNotification = new
                {
                    notificationType = 4,
                    purchaseToken = "token-current-and-linked"
                }
            }, messageId: "msg-current-and-linked")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid-google-push-token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            _factory.GoogleWriteRepository.Writes,
            write => write.EventId == "msg-current-and-linked"
                && write.Subscription.ExternalUserId == "current-token-user");
    }

    [Fact]
    public async Task PostGoogleWebhook_DeferredReplacement_UsesLinkedBindingAndCurrentEntitlement()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/google/subtru_wh_google.secret")
        {
            Content = CreatePubSubRequest(new
            {
                packageName = "com.example.app",
                eventTimeMillis = "1712200000000",
                subscriptionNotification = new
                {
                    notificationType = 4,
                    purchaseToken = "token-deferred-replacement"
                }
            }, messageId: "msg-deferred-replacement")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid-google-push-token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            _factory.GoogleWriteRepository.Writes,
            write => write.EventId == "msg-deferred-replacement"
                && write.LinkedPurchaseToken == "old-deferred-purchase-token"
                && write.Subscription.ExternalUserId == "deferred-user"
                && write.Subscription.ProviderSubscriptionId == "token-deferred-replacement"
                && write.Subscription.ProductId == "premium_monthly");
        Assert.Contains(
            _factory.GooglePlayClient.AcknowledgeCalls,
            call => call.PurchaseToken == "token-deferred-replacement"
                && call.SubscriptionId == "premium_monthly");
    }

    [Fact]
    public async Task PostGoogleWebhook_ExpiredDeferredReplacement_ProcessesTerminalPurchaseShape()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/google/subtru_wh_google.secret")
        {
            Content = CreatePubSubRequest(new
            {
                packageName = "com.example.app",
                eventTimeMillis = "1712300000000",
                subscriptionNotification = new
                {
                    notificationType = 13,
                    purchaseToken = "token-expired-deferred-replacement"
                }
            }, messageId: "msg-expired-deferred-replacement")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid-google-push-token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var write = Assert.Single(
            _factory.GoogleWriteRepository.Writes,
            model => model.EventId == "msg-expired-deferred-replacement");
        Assert.Equal("synthetic-subscriber", write.Subscription.ExternalUserId);
        Assert.Equal("token-expired-deferred-replacement", write.Subscription.ProviderSubscriptionId);
        Assert.Equal("starter_monthly", write.Subscription.ProductId);
        Assert.Equal("starter-monthly-base", write.Subscription.PriceId);
        Assert.Equal("expired", write.Subscription.Status);
        Assert.Equal(
            DateTimeOffset.Parse("2025-04-12T10:15:00Z"),
            write.Subscription.CurrentPeriodEnd);
        Assert.Equal("token-before-deferred-change", write.LinkedPurchaseToken);
        Assert.Contains(
            _factory.WebhookEventLogRepository.Models,
            model => model.EventId == "msg-expired-deferred-replacement"
                && model.Status == WebhookEventStatus.Success);
    }

    [Fact]
    public async Task PostGoogleWebhook_GooglePlayAcknowledgementForbidden_ReturnsServiceUnavailable()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/google/subtru_wh_google.secret")
        {
            Content = CreatePubSubRequest(new
            {
                packageName = "com.example.app",
                eventTimeMillis = "1712200000000",
                subscriptionNotification = new
                {
                    notificationType = 4,
                    purchaseToken = "token-ack-forbidden"
                }
            }, messageId: "msg-ack-forbidden")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid-google-push-token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains(_factory.WebhookEventLogRepository.Models, x => x.EventId == "msg-ack-forbidden" && x.ErrorCode == "google_subscription_acknowledge_configuration_error");
    }

    [Fact]
    public async Task PostGoogleWebhook_DuplicateDeliveryWithPendingAck_RetriesAcknowledgement()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/google/subtru_wh_google.secret")
        {
            Content = CreatePubSubRequest(new
            {
                packageName = "com.example.app",
                eventTimeMillis = "1712200000000",
                subscriptionNotification = new
                {
                    notificationType = 4,
                    purchaseToken = "token-duplicate-ack"
                }
            }, messageId: "msg-duplicate")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid-google-push-token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_factory.GoogleWriteRepository.Writes, x => x.EventId == "msg-duplicate");
        Assert.Contains(_factory.GooglePlayClient.AcknowledgeCalls, x => x.PurchaseToken == "token-duplicate-ack");
        Assert.Contains(_factory.WebhookEventLogRepository.Models, x =>
            x.EventId == "msg-duplicate"
            && x.Status == WebhookEventStatus.Success
            && x.EventType == "google.subscription_notification:SUBSCRIPTION_PURCHASED");
    }

    private static StringContent CreatePubSubRequest(object developerNotification, string messageId = "msg-001")
    {
        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(developerNotification)));
        return CreateJsonContent(new
        {
            message = new
            {
                messageId,
                data
            }
        });
    }

    private static StringContent CreateJsonContent(object body)
        => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
}

public sealed class GoogleWebhookWebApplicationFactory : WebApplicationFactory<Program>
{
    public GoogleWebhookWebApplicationFactory()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", "Host=localhost;Port=5432;Database=subtru_test;Username=test;Password=test");
        Environment.SetEnvironmentVariable("API_KEY_PEPPER", "test-pepper");
        Environment.SetEnvironmentVariable("SUBTRU_ENCRYPTION_KEY", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
        Environment.SetEnvironmentVariable("AppleVerification__RootCertificateDirectory", TestAppleTrustStoreDirectory.DirectoryPath);
    }

    internal FakeGooglePlaySubscriptionClient GooglePlayClient { get; } = new();
    internal FakeGoogleSubscriptionWriteRepository GoogleWriteRepository { get; } = new();
    internal FakeGoogleWebhookEventLogRepository WebhookEventLogRepository { get; } = new();
    internal FakeGoogleSubscriptionSnapshotRepository GoogleSnapshotRepository { get; } = new();
    internal FakeGoogleProviderConfigRepository ProviderConfigRepository { get; } = new();
    internal FakeGooglePubSubPushJwtValidator PushJwtValidator { get; } = new();
    internal FakeGoogleSubscriptionReadRepository SubscriptionReadRepository { get; } = new();

    public void ResetState()
    {
        GooglePlayClient.Reset();
        GoogleWriteRepository.Reset();
        WebhookEventLogRepository.Reset();
        GoogleSnapshotRepository.Reset();
        ProviderConfigRepository.Reset();
        PushJwtValidator.Reset();
        SubscriptionReadRepository.Reset();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        TestWebHostConfiguration.UseAdminApiKey(builder);
        builder.UseEnvironment("Development");
        builder.UseSetting("Logging:EventLog:LogLevel:Default", "None");
        builder.ConfigureLogging(logging => logging.ClearProviders());

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            var config = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=localhost;Port=5432;Database=subtru_test;Username=test;Password=test",
                ["API_KEY_PEPPER"] = "test-pepper",
                ["SUBTRU_ENCRYPTION_KEY"] = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                ["WebhookUrls:PublicBaseUrl"] = "https://api.test.subtru.com",
                ["AppleVerification:RootCertificateDirectory"] = TestAppleTrustStoreDirectory.DirectoryPath
            };

            configBuilder.AddInMemoryCollection(config);
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IWebhookKeyRepository>();
            services.RemoveAll<IProviderConfigRepository>();
            services.RemoveAll<IGooglePubSubPushJwtValidator>();
            services.RemoveAll<IGooglePlaySubscriptionClient>();
            services.RemoveAll<IGoogleSubscriptionWriteRepository>();
            services.RemoveAll<ISubscriptionReadRepository>();
            services.RemoveAll<IWebhookEventLogRepository>();
            services.RemoveAll<IGoogleSubscriptionSnapshotRepository>();

            services.AddSingleton<IWebhookKeyRepository, FakeGoogleWebhookKeyRepository>();
            services.AddSingleton<IProviderConfigRepository>(ProviderConfigRepository);
            services.AddSingleton<IGooglePubSubPushJwtValidator>(PushJwtValidator);
            services.AddSingleton<IGooglePlaySubscriptionClient>(GooglePlayClient);
            services.AddSingleton<IGoogleSubscriptionWriteRepository>(GoogleWriteRepository);
            services.AddSingleton<ISubscriptionReadRepository>(SubscriptionReadRepository);
            services.AddSingleton<IWebhookEventLogRepository>(WebhookEventLogRepository);
            services.AddSingleton<IGoogleSubscriptionSnapshotRepository>(GoogleSnapshotRepository);
        });
    }
}

internal sealed class FakeGoogleWebhookKeyRepository : IWebhookKeyRepository
{
    public Task<WebhookKeyInfo?> GetByKeyAsync(string webhookKey, string provider, CancellationToken ct = default)
    {
        if (provider != "google")
        {
            return Task.FromResult<WebhookKeyInfo?>(null);
        }

        return webhookKey switch
        {
            "subtru_wh_google.secret" => Task.FromResult<WebhookKeyInfo?>(new WebhookKeyInfo(Guid.Parse("11111111-1111-1111-1111-111111111111"), SubscriptionEnvironment.Production, null)),
            "subtru_wh_google_missing_config.secret" => Task.FromResult<WebhookKeyInfo?>(new WebhookKeyInfo(Guid.Parse("22222222-2222-2222-2222-222222222222"), SubscriptionEnvironment.Production, null)),
            "subtru_wh_google_missing_credentials.secret" => Task.FromResult<WebhookKeyInfo?>(new WebhookKeyInfo(Guid.Parse("33333333-3333-3333-3333-333333333333"), SubscriptionEnvironment.Production, null)),
            _ => Task.FromResult<WebhookKeyInfo?>(null)
        };
    }
}

internal sealed class FakeGoogleProviderConfigRepository : IProviderConfigRepository
{
    public void Reset()
    {
    }

    public Task<AppleProviderConfigReadModel?> GetAppleProviderConfigAsync(Guid projectId, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<AppleProviderConfigReadModel> UpsertAppleProviderConfigAsync(Guid projectId, AppleProviderConfigWriteModel writeModel, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<GoogleProviderConfigReadModel?> GetGoogleProviderConfigAsync(Guid projectId, CancellationToken ct = default)
    {
        if (projectId == Guid.Parse("22222222-2222-2222-2222-222222222222"))
        {
            return Task.FromResult<GoogleProviderConfigReadModel?>(null);
        }

        return Task.FromResult<GoogleProviderConfigReadModel?>(new GoogleProviderConfigReadModel(
            PackageName: "com.example.app",
            PubSubAuthServiceAccountEmail: "push-auth@example.iam.gserviceaccount.com",
            HasServiceAccountCredentials: projectId != Guid.Parse("33333333-3333-3333-3333-333333333333"),
            ServiceAccountClientEmail: "svc-play@example.iam.gserviceaccount.com",
            ServiceAccountKeyId: "key-123",
            ServiceAccountCreatedAt: DateTimeOffset.UtcNow.AddDays(-7),
            ServiceAccountLastValidatedAt: DateTimeOffset.UtcNow.AddHours(-2),
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-7),
            UpdatedAt: DateTimeOffset.UtcNow));
    }

    public Task<GoogleProviderCredentialReadModel?> GetGoogleProviderCredentialAsync(Guid projectId, CancellationToken ct = default)
    {
        if (projectId == Guid.Parse("33333333-3333-3333-3333-333333333333"))
        {
            return Task.FromResult<GoogleProviderCredentialReadModel?>(null);
        }

        return Task.FromResult<GoogleProviderCredentialReadModel?>(new GoogleProviderCredentialReadModel(
            ServiceAccountJson: FakeGoogleServiceAccountValidator.ValidJson,
            ClientEmail: "svc-play@example.iam.gserviceaccount.com",
            PrivateKeyId: "key-123",
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-7),
            LastValidatedAt: DateTimeOffset.UtcNow.AddHours(-2)));
    }

    public Task<GoogleProviderConfigReadModel> UpsertGoogleProviderConfigAsync(Guid projectId, GoogleProviderConfigWriteModel writeModel, ValidatedGoogleServiceAccount? validatedServiceAccount, CancellationToken ct = default)
        => throw new NotSupportedException();
}

internal sealed class FakeGooglePubSubPushJwtValidator : IGooglePubSubPushJwtValidator
{
    public List<(string BearerToken, string ExpectedAudience, string ExpectedEmail)> Calls { get; } = [];

    public void Reset()
    {
        Calls.Clear();
    }

    public Task<GooglePubSubPushTokenClaims> ValidateAsync(string bearerToken, string expectedAudience, string expectedEmail, CancellationToken ct = default)
    {
        Calls.Add((bearerToken, expectedAudience, expectedEmail));

        return bearerToken switch
        {
            "valid-google-push-token" => Task.FromResult(new GooglePubSubPushTokenClaims(
                Audience: expectedAudience,
                Email: expectedEmail,
                EmailVerified: true,
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5))),
            "transient-google-push-token" => Task.FromException<GooglePubSubPushTokenClaims>(new GooglePubSubPushJwtValidationException(
                "google_pubsub_jwt_validation_unavailable",
                "Google Pub/Sub push JWT could not be validated right now.",
                isTransient: true)),
            "wrong-google-push-email-token" => Task.FromException<GooglePubSubPushTokenClaims>(new GooglePubSubPushJwtValidationException(
                "invalid_google_pubsub_token_email",
                "Google Pub/Sub push bearer token email is invalid.")),
            _ => Task.FromException<GooglePubSubPushTokenClaims>(new GooglePubSubPushJwtValidationException(
                "invalid_google_pubsub_bearer_token",
                "Google Pub/Sub push bearer token is invalid."))
        };
    }
}

internal sealed class FakeGooglePlaySubscriptionClient : IGooglePlaySubscriptionClient
{
    public List<(string PackageName, string PurchaseToken)> FetchCalls { get; } = [];
    public List<(string PackageName, string PurchaseToken, string? SubscriptionId)> AcknowledgeCalls { get; } = [];

    public void Reset()
    {
        FetchCalls.Clear();
        AcknowledgeCalls.Clear();
    }

    public Task<GooglePlaySubscriptionPurchase> GetSubscriptionAsync(string packageName, string purchaseToken, string serviceAccountJson, CancellationToken ct = default)
    {
        FetchCalls.Add((packageName, purchaseToken));

        return purchaseToken switch
        {
            "token-active" => Task.FromResult(CreateActivePurchase("user-123")),
            "token-test" => Task.FromResult(CreateActivePurchase("test-user") with
            {
                TestPurchase = new GooglePlayTestPurchase()
            }),
            "token-unbound" => Task.FromResult(CreateActivePurchase(externalUserId: null)),
            "token-pending" => Task.FromResult(CreatePurchase(
                subscriptionState: GoogleSubscriptionMapper.SubscriptionStatePending,
                acknowledgementState: GoogleSubscriptionMapper.AcknowledgementStatePending,
                externalUserId: "user-pending")),
            "token-pending-canceled" => Task.FromResult(CreatePurchase(
                subscriptionState: GoogleSubscriptionMapper.SubscriptionStatePendingPurchaseCanceled,
                acknowledgementState: GoogleSubscriptionMapper.AcknowledgementStatePending,
                externalUserId: null,
                linkedPurchaseToken: "token-linked-active")),
            "token-linked-active" => Task.FromResult(CreateActivePurchase("linked-user")),
            "token-expired-fallback" => Task.FromResult(CreatePurchase(
                subscriptionState: GoogleSubscriptionMapper.SubscriptionStateActive,
                acknowledgementState: GoogleSubscriptionMapper.AcknowledgementStatePending,
                externalUserId: null,
                expiredPurchaseToken: "expired-google-token")),
            "token-expired-then-linked-fallback" => Task.FromResult(CreatePurchase(
                subscriptionState: GoogleSubscriptionMapper.SubscriptionStateActive,
                acknowledgementState: GoogleSubscriptionMapper.AcknowledgementStatePending,
                externalUserId: null,
                linkedPurchaseToken: "token-linked-active",
                expiredPurchaseToken: "unknown-expired-token")),
            "token-current-fallback" => Task.FromResult(CreatePurchase(
                subscriptionState: GoogleSubscriptionMapper.SubscriptionStateActive,
                acknowledgementState: GoogleSubscriptionMapper.AcknowledgementStatePending,
                externalUserId: null)),
            "token-current-and-linked" => Task.FromResult(CreatePurchase(
                subscriptionState: GoogleSubscriptionMapper.SubscriptionStateActive,
                acknowledgementState: GoogleSubscriptionMapper.AcknowledgementStatePending,
                externalUserId: null,
                linkedPurchaseToken: "token-linked-active")),
            "token-deferred-replacement" => Task.FromResult(CreateDeferredReplacementPurchase()),
            "token-expired-deferred-replacement" => Task.FromResult(CreateExpiredDeferredReplacementPurchase()),
            "token-duplicate-ack" => Task.FromResult(CreateActivePurchase("duplicate-user")),
            "token-ack-forbidden" => Task.FromResult(CreateActivePurchase("ack-user")),
            "token-not-found" => Task.FromException<GooglePlaySubscriptionPurchase>(new GooglePlayApiException(
                "google_subscription_fetch_failed",
                "Google Play subscription fetch failed.",
                HttpStatusCode.NotFound)),
            "token-forbidden" => Task.FromException<GooglePlaySubscriptionPurchase>(new GooglePlayApiException(
                "google_subscription_fetch_failed",
                "Google Play subscription fetch was forbidden.",
                HttpStatusCode.Forbidden)),
            "token-transient-fetch" => Task.FromException<GooglePlaySubscriptionPurchase>(new GooglePlayApiException(
                "google_subscription_fetch_unavailable",
                "Google Play subscription fetch failed right now.",
                (HttpStatusCode)429,
                isTransient: true)),
            _ => Task.FromResult(CreateActivePurchase("user-default"))
        };
    }

    public Task AcknowledgeAsync(string packageName, string purchaseToken, string serviceAccountJson, string? subscriptionId = null, CancellationToken ct = default)
    {
        AcknowledgeCalls.Add((packageName, purchaseToken, subscriptionId));

        return purchaseToken == "token-ack-forbidden"
            ? Task.FromException(new GooglePlayApiException(
                "google_subscription_acknowledge_failed",
                "Google Play subscription acknowledgement was forbidden.",
                HttpStatusCode.Forbidden))
            : Task.CompletedTask;
    }

    private static GooglePlaySubscriptionPurchase CreateActivePurchase(string? externalUserId)
        => CreatePurchase(
            subscriptionState: GoogleSubscriptionMapper.SubscriptionStateActive,
            acknowledgementState: GoogleSubscriptionMapper.AcknowledgementStatePending,
            externalUserId: externalUserId);

    private static GooglePlaySubscriptionPurchase CreateDeferredReplacementPurchase()
        => new(
            SubscriptionState: GoogleSubscriptionMapper.SubscriptionStateActive,
            AcknowledgementState: GoogleSubscriptionMapper.AcknowledgementStatePending,
            LinkedPurchaseToken: "old-deferred-purchase-token",
            LineItems:
            [
                new GooglePlaySubscriptionLineItem(
                    ProductId: "premium_monthly",
                    ExpiryTime: DateTimeOffset.UtcNow.AddDays(30).ToString("O"),
                    AutoRenewingPlan: new GooglePlayAutoRenewingPlan(
                        AutoRenewEnabled: false,
                        InstallmentDetails: null),
                    PrepaidPlan: null,
                    OfferDetails: new GooglePlayOfferDetails(
                        BasePlanId: "premium-monthly-base",
                        OfferId: null),
                    DeferredItemReplacement: new GooglePlayDeferredItemReplacement("basic_monthly"),
                    ItemReplacement: null,
                    OfferPhase: null),
                new GooglePlaySubscriptionLineItem(
                    ProductId: "basic_monthly",
                    ExpiryTime: null,
                    AutoRenewingPlan: new GooglePlayAutoRenewingPlan(
                        AutoRenewEnabled: true,
                        InstallmentDetails: null),
                    PrepaidPlan: null,
                    OfferDetails: new GooglePlayOfferDetails(
                        BasePlanId: "basic-monthly-base",
                        OfferId: null),
                    DeferredItemReplacement: null,
                    ItemReplacement: null,
                    OfferPhase: null)
            ],
            ExternalAccountIdentifiers: null,
            OutOfAppPurchaseContext: null)
        {
            RawResponseJson = "{\"subscriptionState\":\"SUBSCRIPTION_STATE_ACTIVE\",\"lineItems\":[{\"productId\":\"premium_monthly\"},{\"productId\":\"basic_monthly\"}]}"
        };

    private static GooglePlaySubscriptionPurchase CreateExpiredDeferredReplacementPurchase()
        => new(
            SubscriptionState: GoogleSubscriptionMapper.SubscriptionStateExpired,
            AcknowledgementState: "ACKNOWLEDGEMENT_STATE_ACKNOWLEDGED",
            LinkedPurchaseToken: "token-before-deferred-change",
            LineItems:
            [
                new GooglePlaySubscriptionLineItem(
                    ProductId: "starter_monthly",
                    ExpiryTime: null,
                    AutoRenewingPlan: new GooglePlayAutoRenewingPlan(
                        AutoRenewEnabled: null,
                        InstallmentDetails: null),
                    PrepaidPlan: null,
                    OfferDetails: new GooglePlayOfferDetails(
                        BasePlanId: "starter-monthly-base",
                        OfferId: null),
                    DeferredItemReplacement: null,
                    ItemReplacement: new GooglePlayItemReplacement(
                        ProductId: "standard_monthly",
                        ReplacementMode: "DEFERRED"),
                    OfferPhase: new GooglePlayOfferPhase(
                        ProrationPeriod: null,
                        FreeTrial: null,
                        IntroductoryPrice: null,
                        BasePrice: new GooglePlayBasePriceOfferPhase())),
                new GooglePlaySubscriptionLineItem(
                    ProductId: "standard_monthly",
                    ExpiryTime: "2025-04-12T10:15:00Z",
                    AutoRenewingPlan: new GooglePlayAutoRenewingPlan(
                        AutoRenewEnabled: null,
                        InstallmentDetails: null),
                    PrepaidPlan: null,
                    OfferDetails: new GooglePlayOfferDetails(
                        BasePlanId: "standard-monthly-base",
                        OfferId: null),
                    DeferredItemReplacement: null,
                    ItemReplacement: new GooglePlayItemReplacement(
                        ProductId: "standard_monthly",
                        ReplacementMode: "DEFERRED"),
                    OfferPhase: new GooglePlayOfferPhase(
                        ProrationPeriod: new GooglePlayProrationPeriodOfferPhase(),
                        FreeTrial: null,
                        IntroductoryPrice: null,
                        BasePrice: null))
            ],
            ExternalAccountIdentifiers: new GooglePlayExternalAccountIdentifiers(
                ExternalAccountId: null,
                ObfuscatedExternalAccountId: "synthetic-subscriber",
                ObfuscatedExternalProfileId: null),
            OutOfAppPurchaseContext: null,
            CanceledStateContext: new GooglePlayCanceledStateContext(
                ReplacementCancellation: new GooglePlayReplacementCancellation()))
        {
            RawResponseJson = "{\"subscriptionState\":\"SUBSCRIPTION_STATE_EXPIRED\",\"lineItems\":[{\"productId\":\"starter_monthly\"},{\"productId\":\"standard_monthly\"}]}"
        };

    private static GooglePlaySubscriptionPurchase CreatePurchase(
        string subscriptionState,
        string acknowledgementState,
        string? externalUserId,
        string? linkedPurchaseToken = null,
        string? expiredPurchaseToken = null)
        => new(
            SubscriptionState: subscriptionState,
            AcknowledgementState: acknowledgementState,
            LinkedPurchaseToken: linkedPurchaseToken,
            LineItems:
            [
                new GooglePlaySubscriptionLineItem(
                    ProductId: "premium_monthly",
                    ExpiryTime: DateTimeOffset.UtcNow.AddDays(30).ToString("O"),
                    AutoRenewingPlan: new GooglePlayAutoRenewingPlan(
                        AutoRenewEnabled: true,
                        InstallmentDetails: null),
                    PrepaidPlan: null,
                    OfferDetails: new GooglePlayOfferDetails(
                        BasePlanId: "monthly-base",
                        OfferId: null),
                    DeferredItemReplacement: null,
                    ItemReplacement: null,
                    OfferPhase: null)
            ],
            ExternalAccountIdentifiers: externalUserId is null
                ? null
                : new GooglePlayExternalAccountIdentifiers(
                    ExternalAccountId: null,
                    ObfuscatedExternalAccountId: externalUserId,
                    ObfuscatedExternalProfileId: null),
            OutOfAppPurchaseContext: expiredPurchaseToken is null
                ? null
                : new GooglePlayOutOfAppPurchaseContext(
                    ExpiredExternalAccountIdentifiers: null,
                    ExpiredPurchaseToken: expiredPurchaseToken))
        {
            RawResponseJson = "{\"subscriptionState\":\"SUBSCRIPTION_STATE_ACTIVE\",\"lineItems\":[{\"productId\":\"premium_monthly\"}]}"
        };
}

internal sealed class FakeGoogleSubscriptionSnapshotRepository
    : IGoogleSubscriptionSnapshotRepository
{
    public List<GoogleSubscriptionSnapshotWriteModel> Snapshots { get; } = [];
    public Exception? SaveException { get; set; }

    public void Reset()
    {
        Snapshots.Clear();
        SaveException = null;
    }

    public Task SaveAsync(
        GoogleSubscriptionSnapshotWriteModel snapshot,
        CancellationToken ct = default)
    {
        if (SaveException is not null)
        {
            return Task.FromException(SaveException);
        }

        Snapshots.Add(snapshot);
        return Task.CompletedTask;
    }

}

internal sealed class FakeGoogleSubscriptionWriteRepository : IGoogleSubscriptionWriteRepository
{
    public List<(NormalizedSubscription Subscription, string EventId, string? LinkedPurchaseToken)> Writes { get; } = [];
    public List<(NormalizedSubscription Subscription, string? LinkedPurchaseToken)> ManualWrites { get; } = [];

    public void Reset()
    {
        Writes.Clear();
        ManualWrites.Clear();
    }

    public Task<SubscriptionWriteOutcome> UpsertGoogleWithEventAsync(
        NormalizedSubscription sub,
        string eventId,
        string? linkedPurchaseToken,
        CancellationToken ct)
    {
        Writes.Add((sub, eventId, linkedPurchaseToken));
        return Task.FromResult(eventId switch
        {
            "msg-duplicate" => SubscriptionWriteOutcome.Duplicate,
            "msg-stale" => SubscriptionWriteOutcome.Stale,
            _ => SubscriptionWriteOutcome.Applied
        });
    }

    public Task UpsertGoogleManualAsync(NormalizedSubscription sub, string? linkedPurchaseToken, CancellationToken ct)
    {
        ManualWrites.Add((sub, linkedPurchaseToken));
        return Task.CompletedTask;
    }
}

internal sealed class FakeGoogleSubscriptionReadRepository : ISubscriptionReadRepository
{
    public void Reset()
    {
    }

    public Task<IReadOnlyList<SubscriptionReadModel>> GetByUserAsync(Guid projectId, string environment, string externalUserId, CancellationToken ct)
        => throw new NotSupportedException();

    public Task<string?> GetExternalUserIdByProviderSubscriptionIdAsync(Guid projectId, string environment, string provider, string providerSubscriptionId, CancellationToken ct)
        => Task.FromResult<string?>(providerSubscriptionId switch
        {
            "expired-google-token" => "recovered-user",
            "token-linked-active" => "linked-user",
            "token-current-fallback" => "current-fallback-user",
            "token-current-and-linked" => "current-token-user",
            "old-deferred-purchase-token" => "deferred-user",
            _ => null
        });
}

internal sealed class FakeGoogleWebhookEventLogRepository : IWebhookEventLogRepository
{
    public List<WebhookEventLogWriteModel> Models { get; } = [];

    public void Reset()
    {
        Models.Clear();
    }

    public Task UpsertAsync(WebhookEventLogWriteModel writeModel, CancellationToken ct = default)
    {
        Models.Add(writeModel);
        return Task.CompletedTask;
    }

    public Task<WebhookEventListPageReadModel> ListRecentAsync(Guid projectId, string environment, int limit, WebhookEventCursorReadModel? cursor, string? provider = null, WebhookEventStatusFilter? statusFilter = null, CancellationToken ct = default)
        => throw new NotSupportedException();
}
