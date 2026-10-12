# API reference

This API reference file summarizes the HTTP endpoints used to set up and run SubTru.

## Prerequisite: Create the admin API key

A self-hosted SubTru deployment requires an admin API key in the `SUBTRU_ADMIN_API_KEY` environment variable. The key must begin with `subtru_admin_` and contain at least 32 characters after the prefix.

For example, a `SUBTRU_ADMIN_API_KEY` can be generated as shown below:

macOS or Linux with OpenSSL:

```bash
printf 'subtru_admin_%s\n' "$(openssl rand -hex 32)"
```

Windows PowerShell 5.1 or PowerShell 7:

```powershell
$bytes = New-Object byte[] 32
$random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$random.GetBytes($bytes)
$random.Dispose()
$hex = -join ($bytes | ForEach-Object { $_.ToString("x2") })
"subtru_admin_$hex"
```

Store the generated value as a deployment secret and expose that secret to your self-hosted SubTru server as the `SUBTRU_ADMIN_API_KEY` environment variable. SubTru refuses to start when the key is missing or invalid.

## Security conventions

- Never put admin keys, project API keys, provider credentials, webhook keys, or signing secrets in source control.
- Do not embed secrets in URLs, application logs, shell scripts, or copied support output. Generated webhook URLs are the exception because they contain the webhook key and should therefore be treated as secrets. See [Webhook keys](#webhook-keys) for what exposing one does and does not allow.
- Run administrative commands only from a trusted machine and shell. Other processes running as the same operating-system user may be able to inspect temporary command data.
- Send project API keys only from a trusted backend. Do not ship them in browser or mobile application code.
- The raw `apiKey` and `webhookKey` values are returned only when created. Save them immediately in a secret manager.
- Do not enable shell tracing, PowerShell transcription, or HTTP header logging while handling secrets.

## Authentication

| Request type | Authentication |
| --- | --- |
| Management and diagnostics | `Authorization: Bearer <admin-api-key>` |
| User product access check | `Authorization: Bearer <project-api-key>` |
| Provider webhook delivery | Verified automatically |
| Health checks | None |

The admin API key can manage every project in the deployment. A project API key is restricted to the project and environment selected when it is created. Incoming provider events use the environment of the webhook key in their URL. SubTru checks it against the provider event's environment.

## Error responses

Authenticated API endpoints return errors as a JSON object with an `error` code and a `message`. For example, using a project API key on an admin-only endpoint returns `403 Forbidden`:

```json
{
  "error": "forbidden",
  "message": "Project summary access requires the installation admin API key."
}
```

Authentication failures use the same shape:

| Status | `error` | Cause |
| --- | --- | --- |
| `401` | `missing_api_key` | The `Authorization` header is missing or does not use the `Bearer` scheme. |
| `401` | `invalid_api_key` | The key does not match an admin key or an active project API key. |
| `403` | `forbidden` | A project API key was used on another project's ID, or on an admin-only endpoint. |

Validation and not-found responses use the same shape with endpoint-specific codes and messages. Provider webhook delivery routes follow provider retry semantics and may intentionally return an empty response body.

## Example request

The following examples make the same authenticated request to list projects. They assume the stored admin key has been loaded into `SUBTRU_ADMIN_API_KEY` for Bash or `$env:SUBTRU_ADMIN_API_KEY` for PowerShell. Replace `https://subtru.example.com` with the public origin of your deployment.

macOS or Linux with Bash and `curl`:

```bash
SUBTRU_URL="${SUBTRU_URL:-https://subtru.example.com}"

curl "$SUBTRU_URL/v1/projects" \
  -H "Authorization: Bearer $SUBTRU_ADMIN_API_KEY"
```

Windows PowerShell:

```powershell
$SubtruUrl = "https://subtru.example.com"

Invoke-RestMethod `
    -Method Get `
    -Uri "$SubtruUrl/v1/projects" `
    -Headers @{ Authorization = "Bearer $env:SUBTRU_ADMIN_API_KEY" } |
    ConvertTo-Json -Depth 10
```

Both commands reference a variable, so the literal key is not saved in command history. `Invoke-RestMethod` parses the response into PowerShell objects. `ConvertTo-Json` displays the complete nested response instead of PowerShell's abbreviated default view. The Bash example briefly exposes the expanded header to local process inspection while `curl` runs. Use an application HTTP client or secret-manager integration for production automation.

## Projects

| Method | Path | Authentication | Purpose |
| --- | --- | --- | --- |
| `GET` | `/v1/projects` | Admin API key | Lists all projects. |
| `POST` | `/v1/projects` | Admin API key | Creates a project. |
| `PUT` | `/v1/projects/{projectId}` | Admin API key | Updates a project. |
| `GET` | `/v1/projects/{projectId}/summary?environment={environment}` | Admin API key | Returns access and subscription counts for one environment. |

The same project can have separate `test` and `production` API keys and webhook keys.

`GET /v1/projects` response (`200 OK`):

```json
{
  "items": [
    {
      "id": "11111111-1111-1111-1111-111111111111",
      "name": "My App",
      "slug": null,
      "isActive": true,
      "createdAt": "2026-01-01T00:00:00+00:00",
      "updatedAt": "2026-01-01T00:00:00+00:00"
    }
  ]
}
```

`POST /v1/projects` request body:

```json
{
  "name": "My App"
}
```

`POST /v1/projects` response (`201 Created`):

```json
{
  "id": "11111111-1111-1111-1111-111111111111",
  "name": "My App",
  "slug": null,
  "isActive": true,
  "createdAt": "2026-01-01T00:00:00+00:00",
  "updatedAt": "2026-01-01T00:00:00+00:00"
}
```

Save the returned `id` as the project ID.

`PUT /v1/projects/{projectId}` request body:

```json
{
  "name": "My App",
  "isActive": true
}
```

`PUT` replaces the whole project, so include every field you want to keep. Omitting `isActive` sets it to `true`. Set `isActive` to `false` to deactivate the project. A successful `PUT` returns `200 OK` with the updated project object in the same shape as the `POST` response above.

The summary environment must be `test` or `production` and defaults to `production` when omitted.

`GET /v1/projects/{projectId}/summary` response (`200 OK`):

```json
{
  "usersWithAccess": 38,
  "activeSubscriptions": 40,
  "expiredSubscriptions": 7,
  "lastWebhookReceivedAt": "2026-01-01T00:00:00+00:00"
}
```

`usersWithAccess` counts each user once. `activeSubscriptions` counts subscriptions that currently grant access, so a user with two subscriptions adds 2. `expiredSubscriptions` counts every subscription that does not grant access, whatever its status. `lastWebhookReceivedAt` is `null` until the project successfully processes its first provider webhook.

## Project API keys

| Method | Path | Authentication | Purpose |
| --- | --- | --- | --- |
| `GET` | `/v1/projects/{projectId}/api-keys` | Admin API key | Lists API keys without their secrets. |
| `POST` | `/v1/projects/{projectId}/api-keys` | Admin API key | Creates an API key. |
| `DELETE` | `/v1/projects/{projectId}/api-keys/{id}` | Admin API key | Revokes an API key. |

`GET /v1/projects/{projectId}/api-keys` response (`200 OK`):

```json
{
  "items": [
    {
      "id": "22222222-2222-2222-2222-222222222222",
      "name": "Application backend",
      "keyId": "subtru_test_<key-id>",
      "environment": "test",
      "createdAt": "2026-01-01T00:00:00+00:00",
      "lastUsedAt": null,
      "expiresAt": "2030-01-01T00:00:00+00:00",
      "isRevoked": false,
      "revokedAt": null
    }
  ],
  "nextCursor": null
}
```

This endpoint returns every key in one response. `nextCursor` is reserved for future pagination and is currently always `null`.

`POST /v1/projects/{projectId}/api-keys` request body:

```json
{
  "name": "Application backend",
  "environment": "test",
  "expiresAt": "2030-01-01T00:00:00Z"
}
```

`environment` must be `test` or `production` and defaults to `production` when omitted. `expiresAt` is optional and must be a future ISO 8601 timestamp when supplied. The generated `apiKey` is returned only by the create response.

`POST /v1/projects/{projectId}/api-keys` response (`201 Created`):

```json
{
  "id": "22222222-2222-2222-2222-222222222222",
  "name": "Application backend",
  "keyId": "subtru_test_<key-id>",
  "apiKey": "subtru_test_<key-id>.<secret>",
  "environment": "test",
  "createdAt": "2026-01-01T00:00:00+00:00"
}
```

Save `apiKey` for your backend. Use `id` for later revocation. `keyId` is an identifier, not the credential. `GET` lists key records without their `apiKey` values.

`DELETE /v1/projects/{projectId}/api-keys/{id}` response (`200 OK`):

```json
{
  "id": "22222222-2222-2222-2222-222222222222",
  "isRevoked": true,
  "revokedAt": "2026-01-02T00:00:00+00:00",
  "success": true
}
```

## Webhook keys

Two different credentials appear in this section. The **webhook key** is generated by SubTru and embedded in the webhook URL. It identifies the project, environment, and provider for an incoming event, which is how SubTru knows whose event it is and which provider configuration to verify it against. Treat the webhook key, and any URL containing it, as a credential. Someone who obtains it cannot forge subscription events or read subscription data, because SubTru verifies every provider signature independently, but they can reach your webhook endpoint. Keep it out of access logs, and create a new webhook key if it is exposed.

The **Stripe signing secret** is the `whsec_...` value Stripe generates for your endpoint, which you supply to SubTru so it can verify Stripe's signature. Neither Apple nor Google uses a shared signing secret. Apple notifications verify against Apple's own certificates, and Google Pub/Sub tokens verify against Google's public keys. Both still need project configuration, and Google additionally needs service-account credentials for calling the Google Play API. See [Apple configuration](#apple-configuration) and [Google configuration](#google-configuration).

| Method | Path | Authentication | Purpose |
| --- | --- | --- | --- |
| `GET` | `/v1/projects/{projectId}/webhook-keys` | Admin API key | Lists webhook keys without their full keys, URLs, or signing secrets. |
| `POST` | `/v1/projects/{projectId}/webhook-keys` | Admin API key | Creates a webhook URL for a provider and environment. |
| `PATCH` | `/v1/projects/{projectId}/webhook-keys/{id}` | Admin API key | Sets the Stripe signing secret. |
| `DELETE` | `/v1/projects/{projectId}/webhook-keys/{id}` | Admin API key | Deactivates a webhook URL. |

`GET /v1/projects/{projectId}/webhook-keys` response (`200 OK`):

```json
{
  "items": [
    {
      "id": "33333333-3333-3333-3333-333333333333",
      "keyId": "subtru_wh_<key-id>",
      "provider": "stripe",
      "environment": "test",
      "hasSigningSecret": true,
      "isActive": true,
      "createdAt": "2026-01-01T00:00:00+00:00",
      "updatedAt": "2026-01-01T00:00:00+00:00"
    }
  ]
}
```

`POST /v1/projects/{projectId}/webhook-keys` request body:

```json
{
  "provider": "stripe",
  "environment": "test"
}
```

`provider` must be `stripe`, `apple`, or `google`. `environment` must be `test` or `production` and defaults to `production` when omitted. The create response contains the generated `webhookKey` and complete `webhookUrl`.

`POST /v1/projects/{projectId}/webhook-keys` response (`201 Created`):

```json
{
  "id": "33333333-3333-3333-3333-333333333333",
  "keyId": "subtru_wh_<key-id>",
  "webhookKey": "subtru_wh_<key-id>.<secret>",
  "provider": "stripe",
  "environment": "test",
  "webhookUrl": "https://subtru.example.com/webhooks/stripe/subtru_wh_<key-id>.<secret>",
  "createdAt": "2026-01-01T00:00:00+00:00"
}
```

Register `webhookUrl` with the provider. Creating a new key deactivates the previous active key for the same provider and environment. `GET` lists key records without revealing their `webhookKey` or `webhookUrl`. The `webhookKey` and `webhookUrl` are only returned when the key is created.

Stripe setup normally starts without a signing secret. Register the returned URL in Stripe, reveal its `whsec_...` signing secret, and then update the webhook key record.

`PATCH /v1/projects/{projectId}/webhook-keys/{id}` request body:

```json
{
  "signingSecret": "whsec_..."
}
```

`PATCH /v1/projects/{projectId}/webhook-keys/{id}` response (`200 OK`):

```json
{
  "success": true
}
```

The signing secret is never returned. Only Stripe webhook keys use a signing secret, and SubTru reads it only when verifying Stripe events. Setting it on an Apple or Google webhook key is accepted and returns `200 OK`, but the stored value is never used.

`DELETE /v1/projects/{projectId}/webhook-keys/{id}` response (`200 OK`):

```json
{
  "id": "33333333-3333-3333-3333-333333333333",
  "isActive": false,
  "success": true
}
```

Deactivating a key keeps subscription and event history. Remove the endpoint from the provider separately.

## Provider webhook delivery

| Method | Path | Authentication | Purpose |
| --- | --- | --- | --- |
| `POST` | `/webhooks/stripe/{webhookKey}` | Verified automatically | Receives Stripe subscription events. |
| `POST` | `/webhooks/apple/{webhookKey}` | Verified automatically | Receives App Store Server Notifications. |
| `POST` | `/webhooks/google/{webhookKey}` | Verified automatically | Receives Google Play notifications through Pub/Sub. |

You do not call these routes yourself. After you register the generated webhook URL with Stripe, Apple, or Google, the provider sends requests to it and includes verification information that SubTru checks automatically, so no admin or project API key is used.

A normally accepted delivery returns `200 OK` with no JSON response body. Use the webhook diagnostics endpoint to inspect processing results.

## Apple configuration

| Method | Path | Authentication | Purpose |
| --- | --- | --- | --- |
| `GET` | `/v1/projects/{projectId}/provider-configs/apple` | Admin API key | Returns the Apple configuration. |
| `PUT` | `/v1/projects/{projectId}/provider-configs/apple` | Admin API key | Sets the Apple configuration. |

Until the first `PUT`, `GET` returns `404 Not Found` with the error code `provider_config_not_found`.

`GET /v1/projects/{projectId}/provider-configs/apple` response (`200 OK`):

```json
{
  "provider": "apple",
  "bundleId": "com.example.app",
  "appAppleId": 1234567890,
  "createdAt": "2026-01-01T00:00:00+00:00",
  "updatedAt": "2026-01-01T00:00:00+00:00"
}
```

`PUT /v1/projects/{projectId}/provider-configs/apple` request body:

```json
{
  "bundleId": "com.example.app",
  "appAppleId": 1234567890
}
```

A successful `PUT` returns `200 OK` with the saved configuration in the same shape as the `GET` response above.

## Google configuration

| Method | Path | Authentication | Purpose |
| --- | --- | --- | --- |
| `GET` | `/v1/projects/{projectId}/provider-configs/google` | Admin API key | Returns the Google configuration without credentials. |
| `PUT` | `/v1/projects/{projectId}/provider-configs/google` | Admin API key | Sets the Google configuration. |
| `POST` | `/v1/projects/{projectId}/google/purchases/{purchaseToken}:bind-and-acknowledge?environment={environment}` | Admin API key | Assigns an unmatched Play purchase to a user and acknowledges it. |

As with Apple, `GET` returns `404 Not Found` with `provider_config_not_found` until the first `PUT`.

`GET /v1/projects/{projectId}/provider-configs/google` response (`200 OK`):

```json
{
  "provider": "google",
  "packageName": "com.example.app",
  "pubSubAuthServiceAccountEmail": "google-pubsub@project.iam.gserviceaccount.com",
  "hasServiceAccountCredentials": true,
  "serviceAccountClientEmail": "google-play@project.iam.gserviceaccount.com",
  "serviceAccountKeyId": "example-key-id",
  "serviceAccountCreatedAt": "2026-01-01T00:00:00+00:00",
  "serviceAccountLastValidatedAt": "2026-01-01T00:00:00+00:00",
  "createdAt": "2026-01-01T00:00:00+00:00",
  "updatedAt": "2026-01-01T00:00:00+00:00"
}
```

The response shows whether credentials are stored but never returns `serviceAccountJson`.

`PUT /v1/projects/{projectId}/provider-configs/google` request body:

```json
{
  "packageName": "com.example.app",
  "pubSubAuthServiceAccountEmail": "google-pubsub@project.iam.gserviceaccount.com",
  "serviceAccountJson": "<complete service-account JSON document as a string>"
}
```

Supplying `null` or omitting `serviceAccountJson` preserves the currently stored credentials. A successful `PUT` returns `200 OK` with the saved configuration in the same shape as the `GET` response above.

`POST /v1/projects/{projectId}/google/purchases/{purchaseToken}:bind-and-acknowledge` request body:

```json
{
  "externalUserId": "user-123"
}
```

Use bind-and-acknowledge only to remediate a purchase that could not be associated with a user automatically. The `environment` query parameter defaults to `production` and must match the purchase's Google test status.

Only Google has a remediation endpoint. All three providers need a user identifier that your app supplies at purchase time, and an event without one is recorded as a failed webhook event. Google Play refunds purchases that are not acknowledged in time, so binding and acknowledgement are combined into one call.

`POST /v1/projects/{projectId}/google/purchases/{purchaseToken}:bind-and-acknowledge` response (`200 OK`):

```json
{
  "requestedPurchaseToken": "requested-token",
  "effectivePurchaseToken": "effective-token",
  "environment": "test",
  "externalUserId": "user-123",
  "status": "active",
  "providerStatus": "SUBSCRIPTION_STATE_ACTIVE",
  "currentPeriodEnd": "2030-01-01T00:00:00+00:00",
  "acknowledgedAt": "2026-01-01T00:00:00+00:00"
}
```

## Webhook diagnostics

| Method | Path | Authentication | Purpose |
| --- | --- | --- | --- |
| `GET` | `/v1/projects/{projectId}/webhook-events` | Admin API key | Lists webhook processing results. |

Supported query parameters:

| Parameter | Values |
| --- | --- |
| `environment` | `test` or `production`. Defaults to `production`. |
| `provider` | `stripe`, `apple`, or `google` |
| `status` | `succeeded` or `failed` |
| `limit` | Page size limit of `1` through `100`. Defaults to `50`. |
| `cursor` | The `nextCursor` value returned by the previous page |

`GET /v1/projects/{projectId}/webhook-events` response (`200 OK`):

```json
{
  "items": [
    {
      "id": "evt_example",
      "provider": "stripe",
      "environment": "test",
      "type": "customer.subscription.created",
      "change": null,
      "externalUserId": "user-123",
      "providerSubscriptionId": "sub_example",
      "status": "succeeded",
      "errorCode": null,
      "receivedAt": "2026-01-01T00:00:00+00:00",
      "processedAt": "2026-01-01T00:00:01+00:00"
    }
  ],
  "nextCursor": "MTc2NzIyNTYwMDAwMHxldnRfZXhhbXBsZQ"
}
```

When `nextCursor` is non-null, pass it as `cursor` to fetch the next page.

A failed event records a code in `errorCode`:

```json
{
  "items": [
    {
      "id": "evt_example",
      "provider": "google",
      "environment": "production",
      "type": "SUBSCRIPTION_RENEWED",
      "change": null,
      "externalUserId": null,
      "providerSubscriptionId": null,
      "status": "failed",
      "errorCode": "missing_google_provider_credentials",
      "receivedAt": "2026-01-01T00:00:00+00:00",
      "processedAt": "2026-01-01T00:00:00+00:00"
    }
  ],
  "nextCursor": null
}
```

### Retry behavior

SubTru classifies each webhook failure as retryable or terminal. A retryable failure returns `503 Service Unavailable` so the provider delivers the event again, and the event can succeed on a later attempt without any action from you. A terminal failure returns `200 OK` so the provider stops retrying, and the event stays `failed` until you correct the cause and the provider sends a new event.

### Error codes

#### Project configuration

Correct the project's provider settings in SubTru or the matching value in the provider console.

| `errorCode` | Meaning |
| --- | --- |
| `missing_apple_provider_config` | The project has no saved Apple settings. |
| `missing_google_provider_config` | The project has no saved Google settings. |
| `missing_google_provider_credentials` | The project has no saved Google service-account credentials. |
| `missing_google_service_account_credentials` | The Google service-account credentials needed for this call are absent. |
| `google_provider_config_lookup_failed` | The project's Google settings could not be read. |
| `bundle_id_mismatch` | The notification `bundleId` does not match the project's configured `bundleId`. Retryable. |
| `app_apple_id_mismatch` | The notification `appAppleId` does not match the project's configured `appAppleId`. Retryable. |
| `google_package_name_mismatch` | The RTDN `packageName` does not match the configured Google package. |
| `google_subscription_fetch_configuration_error` | Google Play rejected the credentials when fetching the subscription. |
| `google_subscription_acknowledge_configuration_error` | Google Play rejected the credentials when acknowledging the purchase. |
| `google_access_token_credential_error` | A Google access token could not be issued from the stored credentials. |
| `google_access_token_token_response_error` | Google returned an unusable access-token response. |
| `stripe_api_version_mismatch` | The Stripe event's API version does not match the version SubTru is built against. |

#### Environment

The environment of the webhook key must match the environment the provider sent from. A test key receives sandbox events and a production key receives live events.

| `errorCode` | Meaning |
| --- | --- |
| `environment_mismatch` | The notification environment did not match the webhook key's environment. |
| `apple_environment_mismatch` | The Apple payloads in one notification disagreed about the environment. |
| `invalid_apple_environment` | The notification contained an unrecognized environment value. |
| `missing_apple_environment` | The notification carried no environment evidence. |

#### Delivery and payload

The request reached SubTru but could not be authenticated or parsed. These point at the Pub/Sub push subscription for Google and at the signed payload for Apple.

| `errorCode` | Meaning |
| --- | --- |
| `missing_google_pubsub_bearer_token` | The Pub/Sub push request carried no bearer token. |
| `invalid_google_pubsub_token_email` | The Pub/Sub token's service-account email was not accepted. Retryable. |
| `invalid_google_pubsub_json` | The Pub/Sub request body could not be parsed. |
| `invalid_google_pubsub_message` | The Pub/Sub envelope was missing its message ID or data. |
| `invalid_google_pubsub_message_data` | The Pub/Sub message data was not valid base64. |
| `invalid_google_rtdn_payload` | The decoded RTDN payload could not be parsed. |
| `missing_or_invalid_google_event_time` | The RTDN notification had no usable `eventTimeMillis`. |
| `missing_google_purchase_token` | The RTDN subscription notification had no `purchaseToken`. |
| `invalid_json` | The Apple request body could not be parsed. |
| `missing_signed_payload` | The Apple request body had no `signedPayload`. |
| `missing_signed_transaction_info` | The Apple notification had no signed transaction info. |
| `invalid_apple_signed_payload` | The `signedPayload` was not a compact three-segment JWS, or contained invalid Base64URL or JSON. |
| `unsupported_apple_jws_algorithm` | The signed payload did not use ES256. |
| `unsupported_apple_jws_critical_header` | The signed payload used critical JWS header parameters SubTru does not support. |
| `missing_apple_certificate_chain` | The signed payload had no `x5c` certificate headers. |
| `invalid_apple_certificate_chain` | The certificate chain failed validation, had an unexpected depth or root, or lacked a required Apple extension. |
| `retryable_apple_certificate_chain_verification` | Certificate validation could not complete because an online revocation check was unavailable. Retryable. |
| `invalid_apple_signature` | The signed payload's signature did not validate, or its certificate carried no ECDSA public key. |
| `invalid_apple_timestamp` | A timestamp in the signed payload was outside the accepted range. |
| `missing_notification_type` | The signed Apple notification had no `notificationType`. |
| `missing_notification_uuid` | The signed Apple notification had no `notificationUUID`. |
| `missing_signed_date` | A signed Apple payload had no `signedDate`. |
| `missing_bundle_id` | A signed Apple payload had no `bundleId`. |
| `missing_environment` | A signed Apple payload had no `environment`. |
| `missing_transaction_id` | The signed Apple transaction had no `transactionId`. |
| `missing_app_apple_id` | A production notification did not include `appAppleId`. |
| `original_transaction_id_mismatch` | The signed transaction and renewal payloads referenced different `originalTransactionId` values. |
| `invalid_google_pubsub_bearer_token` | The Pub/Sub push bearer token was invalid. |
| `invalid_google_pubsub_token_issuer` | The Pub/Sub bearer token had an invalid issuer. |
| `invalid_google_pubsub_token_email_verified` | The Pub/Sub bearer token's email was not verified. |

#### User mapping

SubTru received a valid event but could not link the purchase to one of your users. Fix the purchase flow in your app so it sends the ID, then replay or resend the event.

| `errorCode` | Meaning |
| --- | --- |
| `missing_or_invalid_subtru_external_user_id` | The Stripe subscription metadata had no usable `subtru_external_user_id`. |
| `google_unbound_purchase` | The Google purchase could not be safely bound to a user. See the purchase remediation endpoint. |
| `missing_or_invalid_app_account_token` | The Apple notification had no valid `appAccountToken`. Apple has no remediation endpoint, so the purchase cannot be bound after the fact. |

#### Event contents

The event was verified as authentic, but it did not carry the fields SubTru needs to record subscription state, or it uses a shape SubTru does not support yet. These point at the purchase that produced the event rather than at your configuration.

| `errorCode` | Provider | Meaning |
| --- | --- | --- |
| `missing_notification_data` | Apple | The notification carried no data payload. |
| `missing_original_transaction_id` | Apple | The transaction had no `originalTransactionId`. |
| `missing_or_invalid_refund_subscription_status` | Apple | A refund notification carried no usable subscription status. |
| `missing_refund_renewal_info` | Apple | A refund notification carried no renewal info. |
| `missing_refund_original_transaction_id` | Apple | A refund notification's renewal info had no `originalTransactionId`. |
| `refund_original_transaction_id_mismatch` | Apple | The refund's transaction and renewal info disagreed on `originalTransactionId`. |
| `missing_provider_subscription_id` | Stripe | The Stripe subscription had no ID. |
| `missing_subscription_items` | Stripe | The Stripe subscription had no items. |
| `unsupported_multiple_subscription_items` | Stripe | The Stripe subscription had more than one item, which SubTru does not support yet. |
| `missing_price_id` | Stripe | The subscription item had no price ID. |
| `missing_product_id` | Apple, Stripe | The subscription carried no product ID. |
| `missing_current_period_end` | Apple, Stripe | The subscription had no current period end date. |
| `missing_ended_at` | Stripe | A canceled Stripe subscription had no `ended_at`. |
| `invalid_google_subscription_response` | Google | Google Play returned a subscription response that SubTru could not parse. |
| `unsupported_google_pending_subscription_state` | Google | The subscription is pending and does not grant access. |
| `google_pending_purchase_canceled` | Google | The pending purchase was canceled before becoming active. |
| `unsupported_google_subscription_shape` | Google | The subscription did not contain a supported line-item shape. |
| `unsupported_google_prepaid_subscription` | Google | The subscription uses a prepaid plan, which SubTru does not support yet. |
| `unsupported_google_installment_subscription` | Google | The subscription uses an installment plan, which SubTru does not support yet. |
| `missing_google_product_id` | Google | The subscription line item had no product ID. |
| `invalid_google_expiry_time` | Google | The subscription line item had no usable expiry time. |
| `missing_google_subscription_state` | Google | The subscription response had no subscription state. |
| `unsupported_google_subscription_state` | Google | The subscription state is not one SubTru supports. |

#### Provider state

The subscription or purchase no longer exists in a usable state at the provider. These usually need no action.

| `errorCode` | Meaning |
| --- | --- |
| `google_subscription_not_found` | Google Play did not find the purchase token. |
| `google_subscription_gone` | The purchase token is no longer available from Google Play. |
| `google_subscription_acknowledge_not_found` | The token was not found when acknowledging. |
| `google_subscription_acknowledge_gone` | The token was no longer available when acknowledging. |

#### Provider API failures

SubTru could not complete a provider API call. Network errors and timeouts are retryable; other provider responses may be terminal depending on the returned status.

| `errorCode` | Meaning |
| --- | --- |
| `google_access_token_network_error` | The request to Google for an access token failed. |
| `google_access_token_timeout` | The request to Google for an access token timed out. |
| `google_access_token_empty` | Google returned an empty access token. |
| `google_subscription_fetch_network_error` | The request to Google Play to fetch the subscription failed. |
| `google_subscription_fetch_timeout` | The request to Google Play to fetch the subscription timed out. |
| `google_subscription_fetch_failed` | Google Play returned an error while fetching the subscription. |
| `google_subscription_acknowledge_network_error` | The request to Google Play to acknowledge the purchase failed. |
| `google_subscription_acknowledge_timeout` | The request to Google Play to acknowledge the purchase timed out. |
| `google_subscription_acknowledge_failed` | Google Play returned an error while acknowledging the purchase. |
| `google_pubsub_jwt_validation_network_error` | The request to Google to verify the Pub/Sub token failed. |
| `google_pubsub_jwt_validation_timeout` | The request to Google to verify the Pub/Sub token timed out. |

#### Ignored and unexpected events

| `errorCode` | Meaning |
| --- | --- |
| `stale_provider_event` | The event was older than the stored subscription state, so it was not applied. |
| `stale_provider_snapshot` | The fetched Google subscription state was older than the stored subscription state, so it was not applied. |
| `unsupported_apple_notification_type` | The Apple notification type is not one SubTru handles. |
| `missing_apple_purchase_type` | The notification did not identify a purchase type. |
| `unsupported_apple_purchase_type` | The purchase type is not one SubTru handles. |
| `unexpected_event_object` | The Stripe event's data object was not a subscription. |
| `processing_failed` | An unhandled error occurred while processing the event. Check the server logs. |

## User product access

| Method | Path | Authentication | Purpose |
| --- | --- | --- | --- |
| `GET` | `/v1/projects/{projectId}/users/{externalUserId}/access?environment={environment}` | Project API key or admin API key | Returns the user's access to each product. |
| `GET` | `/v1/projects/{projectId}/users/{externalUserId}/subscriptions?environment={environment}` | Admin API key | Lists the user's subscriptions. |

For the access endpoint, a project API key fixes the project and environment. The optional `environment` query parameter must match the key when supplied. Applications should call it with a project API key. The admin API key also works for debugging, and then `environment` defaults to `production`. For the admin-only subscriptions endpoint, `environment` must be `test` or `production` and defaults to `production` when omitted.

`GET /v1/projects/{projectId}/users/{externalUserId}/access` response (`200 OK`):

```json
{
  "entitlements": [
    {
      "productId": "pro_subscription",
      "hasAccess": true,
      "accessReason": "active_subscription",
      "accessEndsAt": "2030-01-01T00:00:00+00:00"
    }
  ]
}
```

Each entitlement covers one product the user has a subscription record for. You can use the `hasAccess` value to make access decisions for your user in your product. `accessReason` explains the decision and is one of the following.

| `accessReason` | `hasAccess` | Meaning |
| --- | --- | --- |
| `active_subscription` | `true` | The subscription is paid and set to renew. |
| `trialing_subscription` | `true` | The user is in a trial period. |
| `canceled_subscription` | `true` | The user canceled, and access continues until `accessEndsAt`. |
| `payment_failed_in_grace_period` | `true` | A renewal payment failed, and the provider's grace period is still running. |
| `payment_failed_past_grace_period` | `false` | A renewal payment failed, and the grace period has ended. |
| `paused_subscription` | `false` | The subscription is paused. |
| `expired_subscription` | `false` | The subscription has ended or never became active. |

If a user has several subscriptions for the same product, any one that grants access is used.

Before a subscription is recorded for the user, the response is `{"entitlements":[]}`. A product missing from `entitlements` means the user has no access to it.

`GET /v1/projects/{projectId}/users/{externalUserId}/subscriptions` response (`200 OK`):

```json
{
  "items": [
    {
      "provider": "stripe",
      "providerSubscriptionId": "id_example",
      "environment": "test",
      "productId": "pro_subscription",
      "priceId": "price_id_example",
      "status": "active",
      "providerStatus": "active",
      "currentPeriodEnd": "2030-01-01T00:00:00+00:00",
      "accessEndsAt": "2030-01-01T00:00:00+00:00"
    }
  ],
  "nextCursor": null
}
```

This endpoint returns all of the user's subscriptions in one response. `nextCursor` is reserved for future pagination and is currently always `null`.

## Health

Health endpoints do not require authentication.

| Method | Path | Authentication | Purpose |
| --- | --- | --- | --- |
| `GET` | `/health/live` | None | Confirms the API process is running. |
| `GET` | `/health/ready` | None | Confirms the instance is ready for traffic. |

`GET /health/live` response (`200 OK`):

```json
{
  "status": "ok"
}
```

`/health/ready` checks that required configuration is present and PostgreSQL is reachable.

`GET /health/ready` response when the API and database are ready (`200 OK`):

```json
{
  "status": "ready"
}
```

`GET /health/ready` response when a dependency is unavailable (`503 Service Unavailable`):

```json
{
  "status": "not_ready"
}
```
