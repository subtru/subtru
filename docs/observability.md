# Observability

This document defines the SubTru observability conventions for logs, traces, metrics, and health checks.

## Logging

Production logs use the built-in `ILogger` JSON console provider. Scopes are included so request-level fields can be attached to every log emitted during a request.

Development keeps the default developer-friendly logging configuration.

## Request Correlation

Every response includes `x-request-id`.

Incoming `x-request-id` values are accepted only when exactly one value is present, the value is not blank, the value is at most 128 characters, and every character is one of `A-Z`, `a-z`, `0-9`, `.`, `_`, `:`, or `-`.

When the incoming value is missing or invalid, the API generates a new request ID.

Request logs should include:

- `request_id`: the accepted or generated request ID.
- `trace_id`: the current W3C activity trace ID when one exists.

## Health And Readiness

`/health/live` is the unauthenticated liveness endpoint and returns `200` with `{ "status": "ok" }`.

`/health/ready` is the unauthenticated readiness endpoint for deploy checks, synthetics, and alerting. It returns `200` with `{ "status": "ready" }` when required configuration is present and PostgreSQL responds to a cheap `SELECT 1`. Otherwise, it returns `503` with `{ "status": "not_ready" }`.

Readiness does not call Stripe, Google, Apple, or other provider APIs. PostgreSQL readiness uses a 2-second timeout and caches the readiness result for 5 seconds. Health responses must not include secrets, connection strings, provider configuration, or exception details.

## OpenTelemetry

SubTru.Api exports OpenTelemetry traces and base metrics through OTLP when an OTLP endpoint is configured. Production logs are emitted as JSON to standard output so each operator can collect them with their platform of choice.

Information-level `HttpClient` logs from the OTLP trace and metric exporters are suppressed so successful telemetry export does not flood Loki. Exporter warnings and errors should still be logged.

Traces include ASP.NET Core requests, outgoing HttpClient calls, and Npgsql database spans. Base metrics include ASP.NET Core request metrics plus .NET runtime and process metrics. Business metrics use the `SubTru.Business` meter and are exported through the same OTLP metrics pipeline.

The app uses W3C trace context and a parent-based trace-id ratio sampler. `OTEL_TRACES_SAMPLER_ARG` controls the root sample ratio and must be between `0.0` and `1.0`.

Configure OTLP with environment variables or your deployment platform's secret manager:

```text
OTEL_EXPORTER_OTLP_ENDPOINT
OTEL_EXPORTER_OTLP_HEADERS
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_SERVICE_NAME=subtru-api
OTEL_RESOURCE_ATTRIBUTES=deployment.environment=<development|staging|production>
OTEL_TRACES_SAMPLER_ARG=<1.0|0.1>
```

`OTEL_EXPORTER_OTLP_ENDPOINT` and `OTEL_EXPORTER_OTLP_HEADERS` should not be committed. Without an OTLP endpoint, local and test startup still works and no OTLP trace or metric exporter is registered.

Because production may sample traces, logs may contain `trace_id` values that do not have matching stored Tempo traces.

## Business Metrics

Business metrics are explicit `System.Diagnostics.Metrics` instruments for product and provider workflows that are useful to graph and alert on. They must use bounded tags only. Do not add project IDs, external user IDs, event IDs, subscription IDs, purchase tokens, or any other high-cardinality identifiers as metric tags.

Current business metrics:

- `subtru.webhook.events`: counter for durable webhook processing outcomes.
- `subtru.webhook.processing.duration`: histogram for webhook processing duration in seconds.
- `subtru.webhook.acknowledgements`: counter for provider acknowledgement outcomes, currently Google Play subscription acknowledgements.
- `subtru.access_checks`: counter for central access decision outcomes.

Webhook metrics may use only `provider`, `environment`, `status`, and `error_code` tags. Providers are bounded to known providers such as `apple`, `google`, and `stripe`. Environments are subscription environments such as `test` and `production`. Status values are bounded workflow outcomes such as `success`, `failed`, `ignored`, `duplicate`, or `unknown`. Error codes are normalized against a finite list of codes emitted by SubTru; an unrecognized value is bucketed as `other`.

Access check metrics may use only `environment`, `status`, and `reason` tags. Status is `granted`, `denied`, or `unknown`. Reason is bounded to access states such as `active`, `trial`, `grace`, `paused`, `expired`, `none`, or `unknown`.

Normal UI navigation is observed through request traces and HTTP metrics, not business event logs or per-page navigation metrics.

## Log Taxonomy

Use stable, low-cardinality fields where possible:

- `request_id`
- `trace_id`
- `project_id`
- `provider`
- `environment`
- `event_id`
- `error_code`
- `status`

Use `Information` for expected lifecycle events and successful operational milestones. Use `Warning` for caller or provider failures that are expected to happen in normal operation, such as invalid signatures, authorization failures, unsupported webhook events, or retryable provider responses. Use `Error` for platform failures that require operator attention, such as persistence failures, missing required server configuration, or unhandled exceptions.

## Product Event Logs

Meaningful product and security lifecycle actions use the `SubTru.ProductEvents` log category. Each product event log includes a stable `product_event` action name such as `project.created`, `api_key.created`, `webhook_key.secret_rotated`, or `provider_config.upserted`.

Product event logs are intentionally low-noise. Normal UI navigation and ordinary successful API reads should be observed through traces and metrics, not logs.

Product event logs may include operational identifiers and bounded fields such as `project_id`, `api_key_id`, `webhook_key_id`, `actor`, `provider`, `environment`, `status`, `is_active`, and `service_account_credentials_updated`.

Product event logs must not include raw API keys, webhook signing secrets, generated webhook key values, webhook URLs, provider credential JSON, service account emails, service account key IDs, bundle IDs, package names, or project names/slugs.

## Secret Policy

Logs must not include bearer tokens, API keys, webhook keys, webhook signing secrets, provider credential JSON, raw request or response payloads, Google purchase tokens, or Apple signed payloads.

This policy covers SubTru's own telemetry only. Because a provider webhook URL carries the webhook key in its path, any reverse proxy, load balancer, CDN, or APM agent in front of the API can record that key in its access logs. Disable or redact `/webhooks/` path logging in those components as well.

When an operator needs to correlate repeated sensitive values without seeing the original value, log only a deterministic `*_fingerprint` generated from the sensitive value. Fingerprints are for correlation only and must not be treated as credentials.
