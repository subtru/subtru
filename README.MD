# SubTru

SubTru is an open source, self-hostable API that makes it easy to manage your customers' subscriptions and is designed for web or mobile apps that sell recurring subscriptions through any combination of Stripe, the Apple App Store, and/or Google Play. Whenever one of your users purchases or changes their subscription, your subscription provider will send an event to the SubTru API. SubTru processes these events, stores or updates a subscription row for your user in a PostgreSQL database, and provides an endpoint for your app to fetch any user's subscription status so you can make product access decisions for your users.

Example GET /access response:

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

Subscription handling can be very complicated and catastrophic if not done properly. SubTru intends to do that one, complicated thing really well. SubTru integrates in only a few lines of code, so you can get set up quickly and stay in control of your codebase.

This repository contains the self-hostable API and database migrations. You can run SubTru for free or use [SubTru Cloud](https://subtru.com/orgs) if you would prefer a fully managed service with a dashboard, organization management, and analytics.

SubTru supports recurring Stripe subscriptions, Apple auto-renewable subscriptions, and Google Play auto-renewing plans. Current limits include Apple Family Sharing, subscriptions with multiple Stripe or Google items, Apple non-renewing subscriptions, and Google prepaid or installment plans.

## Self-host with Docker

SubTru includes a `Dockerfile` so you can easily build and deploy its image to a container.

You will also need a PostgreSQL database with `pgcrypto`, a public HTTPS origin for provider subscription change events, and `psql` or another way to apply the database migration files. The database must be reachable from the API container.

### 1. Database setup

Set `DATABASE_URL` for the empty database you will use for SubTru. Use a database owner or migration account that can create the required objects, and apply migrations `001` through `008` in order:

```bash
export DATABASE_URL='postgresql://DB_USER:PASSWORD@POSTGRES_HOST:5432/DATABASE_NAME'

for file in db/migrations/00[1-8]_*.sql; do
  psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f "$file"
done

unset DATABASE_URL
```

Migration `009_runtime_database_security.sql` is optional but recommended when your PostgreSQL provider permits role management. It creates a restricted account for the running API so the migration account does not have to be used at runtime. See the [database migration guide](db/migrations/README.md) for more details.

### 2. Set the API secrets/variables

Store the following variables in your hosting platform's secret or configuration system:

```dotenv
ConnectionStrings__Postgres=Host=POSTGRES_HOST;Port=5432;Database=DATABASE_NAME;Username=RUNTIME_DB_USER;Password=RUNTIME_DB_PASSWORD
SUBTRU_ADMIN_API_KEY=subtru_admin_GENERATED_VALUE
API_KEY_PEPPER=GENERATED_64_CHARACTER_HEX_VALUE
SUBTRU_ENCRYPTION_KEY=GENERATED_64_CHARACTER_HEX_VALUE
WebhookUrls__PublicBaseUrl=https://subtru.example.com
```

`API_KEY_PEPPER` and `SUBTRU_ENCRYPTION_KEY` must be separate, 64 character hexadecimal values.

Replacing the encryption key makes existing encrypted provider credentials, webhook signing secrets, and diagnostic snapshots unreadable.

`SUBTRU_ADMIN_API_KEY` must start with `subtru_admin_` and be followed by at least 32 characters. For authentication details, see the [API endpoint reference](docs/api-reference.md).

### 3. Build and deploy the image

Hosting platforms differ in where the image is built. Some build the `Dockerfile` for you, either from a local checkout through their CLI or from a connected Git repository. Others expect an image you have already pushed to a container registry.

If your platform builds the image, deploy it with the settings from step 2. Where the platform lets you choose a build context, set it to the repository root, because the `Dockerfile` copies paths relative to it.

To build and push the image yourself, replacing `OWNER` with your registry namespace and `v0.1.0` with the release you are deploying:

```bash
git checkout v0.1.0
docker build --pull --tag ghcr.io/OWNER/subtru:v0.1.0 .
docker push ghcr.io/OWNER/subtru:v0.1.0
```

Then deploy that image using your hosting platform's normal container workflow.

The platform must:

- Route public HTTPS traffic to container port `3000`.
- Supply the API secrets/variables to the container.
- Allow the container to connect to PostgreSQL.
- Keep at least one instance running if you want to avoid webhook-processing cold starts.
- Use `/health/live` for liveness and `/health/ready` for readiness.

### 4. Verify the deployment

Set `SUBTRU_URL` to the public HTTPS origin configured in `WebhookUrls__PublicBaseUrl`:

```bash
export SUBTRU_URL='https://subtru.example.com'
curl --fail --show-error "$SUBTRU_URL/health/live"
curl --fail --show-error "$SUBTRU_URL/health/ready"
```

### 5. Create a project

The [API endpoint reference](docs/api-reference.md#projects) has the full request and response shapes for this step and the ones that follow.

Load the admin key from your secure store, then create a project for your app:

```bash
curl -X POST "$SUBTRU_URL/v1/projects" \
  -H "Authorization: Bearer $SUBTRU_ADMIN_API_KEY" \
  -H 'Content-Type: application/json' \
  -d '{"name":"My App"}'
```

SubTru stores subscriptions by project and environment. Environment is specified when creating webhook keys and project API keys.

Webhook events use the environment of the webhook key, and access checks use the environment of the API key, so a test key is only used for test subscriptions and a production key is only used for live/production subscriptions.

Save the response's `id` as `PROJECT_ID`.

### 6. Create a test API key

Create a `test` API key:

```bash
curl -X POST "$SUBTRU_URL/v1/projects/$PROJECT_ID/api-keys" \
  -H "Authorization: Bearer $SUBTRU_ADMIN_API_KEY" \
  -H 'Content-Type: application/json' \
  -d '{"name":"Staging backend","environment":"test"}'
```

Save the returned `apiKey` as `SUBTRU_API_KEY` in your staging backend's secret store. To confirm the key works, load it into `SUBTRU_API_KEY` in this shell and call the access endpoint. No provider is connected yet, so a successful call returns an empty `entitlements` array:

```bash
curl "$SUBTRU_URL/v1/projects/$PROJECT_ID/users/user-123/access" \
  -H "Authorization: Bearer $SUBTRU_API_KEY"
```

An empty array confirms the key authenticates and the project resolves. Entitlements appear once a provider event creates a subscription for that user.

### 7. Create a production API key

When you go live, create a separate `production` API key under the same `PROJECT_ID`:

```bash
curl -X POST "$SUBTRU_URL/v1/projects/$PROJECT_ID/api-keys" \
  -H "Authorization: Bearer $SUBTRU_ADMIN_API_KEY" \
  -H 'Content-Type: application/json' \
  -d '{"name":"Production backend","environment":"production"}'
```

Save its returned `apiKey` as `SUBTRU_API_KEY` in your production backend's secret store. Your staging and production deployments can use the same secret name with different values. The request's `name` field labels each key inside SubTru. Test keys read test subscription state and production keys read production subscription state. Raw API keys are shown only when created.

## Provider setup

Each provider (Stripe, the Apple App Store, and Google Play) needs a webhook URL to send subscription change events to the SubTru API. This webhook URL is constructed alongside the webhook key that you must create using the `SUBTRU_ADMIN_API_KEY`.

The [API endpoint reference](docs/api-reference.md) has request bodies and routes for provider configuration, webhook keys, diagnostics, and access checks.

### 1. Create test webhook keys

[Create a `test` webhook key](docs/api-reference.md#webhook-keys) for each provider you want to test. The returned `webhookUrl` contains the webhook key, so store it like a credential. Exposing one does not let anyone forge subscription events, because provider signatures are verified separately, but it does let them reach your endpoint, so you may want to create a new webhook key if a URL leaks.

### 2. Connect providers

- **Stripe:** Register the URL as a Stripe test-mode webhook destination for subscription events. Copy Stripe's `whsec_...` signing secret and save it with the webhook-key `PATCH` request.
- **Apple:** Set the URL as the Sandbox Server URL in App Store Connect, and save the project's [Apple settings](docs/api-reference.md#apple-configuration) in SubTru.
- **Google:** Set the URL as the push endpoint for a Google Cloud Pub/Sub subscription that receives Google Play notifications, and save the project's [Google settings](docs/api-reference.md#google-configuration) in SubTru.

### 3. Associate purchases with users

In your app's purchase flow, provide the ID SubTru should use to link the purchase to a user: `subtru_external_user_id` in Stripe subscription metadata, a UUID `appAccountToken` for Apple, or `setObfuscatedAccountId` in Google Play Billing. Use that same ID for user product access checks. If it differs from your app's user ID, keep the mapping in your backend.

### 4. Verify a test subscription

Send a provider test subscription and confirm its webhook event, subscription state, and access result.

### 5. Go live

When you go live, create separate `production` webhook keys under the same project and register their URLs with the providers. Use a `production` API key in your live backend, as shown in the self-hosting steps above.

## Configuration and deployment

### Required settings

Provide these settings when running the Docker image:

| Setting | Secret | Purpose |
| --- | --- | --- |
| `ConnectionStrings__Postgres` | Yes | PostgreSQL connection string for the running API. Contains the database password. |
| `SUBTRU_ADMIN_API_KEY` | Yes | Admin API key for managing all projects. Treat it as a root credential. |
| `API_KEY_PEPPER` | Yes | Secret used to HMAC project API keys and webhook routing keys. Keep it stable so existing keys continue to work. |
| `SUBTRU_ENCRYPTION_KEY` | Yes | 64-character hex key for webhook signing secrets, provider credentials, and diagnostic snapshots. |
| `WebhookUrls__PublicBaseUrl` | No | Public HTTPS origin used to generate provider webhook URLs. |

Store every setting marked secret in your platform's secret storage, or in a protected environment file kept outside this repository. Do not put them in source control, container images, build logs, or infrastructure files.

### Deployment notes

Run required migrations `001` through `008` before starting the API. Migration `009_runtime_database_security.sql` is optional and creates a restricted runtime database role, which is recommended for a production deployment. See the [migration guide](db/migrations/README.md) for more details.

It is recommended that you keep PostgreSQL storage persistent, back it up, and restrict database network access.

Only you and your providers should know a project's webhook URLs. SubTru stores only a hashed version of each webhook key, so the full URL cannot be recovered from the database, and it keeps the key out of its own logs, traces, and metrics. Components in front of the API often record full request paths, though, so redact or suppress the `/webhooks/` path wherever it is logged.

If a webhook URL is exposed, create a new webhook key and register the new URL with the provider. Exposure alone does not allow forged subscription events, because provider signatures are verified separately.

OpenTelemetry export is optional. See the [observability guide](docs/observability.md) for OTLP variables, logs, traces, metrics, and the policy for keeping secrets out of telemetry.

## Documentation

- [API endpoint reference](docs/api-reference.md): authentication, management requests, webhook routes, access checks, and health.
- [Database migrations](db/migrations/README.md): migration order and the production runtime role.
- [Source architecture](docs/architecture.md): project boundaries and where new code belongs.
- [Observability](docs/observability.md): logs, metrics, traces, and health checks.
- [Provider support and limits](https://subtru.com/docs/subscription-handling): supported subscription flows and current limits.

## Development

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on your machine if you want to build, test, or run the API with `dotnet` instead of Docker. The Docker build already supplies its own SDK.

To run from source, set the configuration above and start the development profile:

```bash
dotnet run --project src/SubTru.Api/SubTru.Api.csproj --launch-profile 'SubTru.Api (http)'
```

The development profile listens on `http://localhost:5240` and enables Swagger UI at `/swagger`. It supplies Apple trust-store settings and a placeholder HTTPS webhook origin.

```bash
dotnet build src/SubTru.sln -c Release
dotnet test src/SubTru.sln -c Release
```

Database integration tests use `SUBTRU_TEST_DB_CONNECTION`. The scripts under `scripts/` can prepare and tear down a local integration database. See [source architecture](docs/architecture.md) for understanding code organization.

## Contributing

Bug reports, documentation fixes, and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for how to report an issue, run tests, and propose a change.

Report suspected vulnerabilities privately by following [SECURITY.md](SECURITY.md), not through a public issue.

## License

The code in this repository is licensed under the [Apache License, Version 2.0](LICENSE).
