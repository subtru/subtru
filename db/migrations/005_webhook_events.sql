-- Migration: Webhook event logs and processed-event idempotency
-- Description: Operational webhook history plus provider/environment-aware processed event ledger.

CREATE TABLE webhook_events (
    project_id               UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    provider                 TEXT NOT NULL,
    environment              TEXT NOT NULL DEFAULT 'production',
    event_id                 TEXT NOT NULL,
    event_type               TEXT,
    event_change             TEXT,
    external_user_id         TEXT,
    provider_subscription_id TEXT,
    status                   TEXT NOT NULL,
    error_code               TEXT,
    error_message            TEXT,
    provider_created_at      TIMESTAMPTZ,
    received_at              TIMESTAMPTZ NOT NULL,
    processed_at             TIMESTAMPTZ,
    last_attempted_at        TIMESTAMPTZ NOT NULL,
    attempt_count            INTEGER NOT NULL DEFAULT 1,
    created_at               TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at               TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT webhook_events_pkey
        PRIMARY KEY (project_id, provider, environment, event_id),
    CONSTRAINT webhook_events_provider_chk
        CHECK (provider IN ('stripe', 'apple', 'google')),
    CONSTRAINT webhook_events_environment_chk
        CHECK (environment IN ('production', 'test')),
    CONSTRAINT webhook_events_status_chk
        CHECK (status IN ('success', 'failed', 'ignored', 'duplicate')),
    CONSTRAINT webhook_events_attempt_count_chk
        CHECK (attempt_count >= 1)
);

CREATE INDEX idx_webhook_events_project_received
ON webhook_events (project_id, environment, received_at DESC, event_id DESC);

CREATE INDEX idx_webhook_events_project_provider_status
ON webhook_events (project_id, environment, provider, status, received_at DESC);

CREATE INDEX idx_webhook_events_project_provider_subscription
ON webhook_events (project_id, environment, provider, provider_subscription_id)
WHERE provider_subscription_id IS NOT NULL;

CREATE TRIGGER update_webhook_events_updated_at
    BEFORE UPDATE ON webhook_events
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

CREATE TABLE processed_events (
    project_id          UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    provider            TEXT NOT NULL,
    environment         TEXT NOT NULL DEFAULT 'production',
    event_id            TEXT NOT NULL,
    provider_created_at TIMESTAMPTZ,
    processed_at        TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT processed_events_pkey
        PRIMARY KEY (project_id, provider, environment, event_id),
    CONSTRAINT processed_events_provider_chk
        CHECK (provider IN ('stripe', 'apple', 'google')),
    CONSTRAINT processed_events_environment_chk
        CHECK (environment IN ('production', 'test'))
);

CREATE INDEX idx_processed_events_project_processed_at
ON processed_events (project_id, environment, processed_at DESC);

CREATE INDEX idx_processed_events_project_provider_processed_at
ON processed_events (project_id, environment, provider, processed_at DESC);

COMMENT ON TABLE webhook_events IS
    'Operational webhook event logs used by protected UI pages and debugging workflows.';

COMMENT ON COLUMN webhook_events.event_change IS
    'Optional provider-derived change used for display. It is not used in subscription normalization or access decisions.';

COMMENT ON COLUMN webhook_events.provider_subscription_id IS
    'Provider subscription identifier associated with the webhook event when available.';

COMMENT ON COLUMN webhook_events.environment IS
    'SubTru subscription environment associated with the webhook event.';

COMMENT ON COLUMN webhook_events.provider_created_at IS
    'Provider-side event creation timestamp when available.';

COMMENT ON COLUMN webhook_events.received_at IS
    'Timestamp when the webhook request was first received by SubTru.';

COMMENT ON COLUMN webhook_events.processed_at IS
    'Timestamp when SubTru completed successful processing for this event.';

COMMENT ON COLUMN webhook_events.last_attempted_at IS
    'Timestamp of the latest processing attempt for this event.';

COMMENT ON TABLE processed_events IS
    'Successfully processed provider webhook events used for transactional idempotency and project-scoped metrics.';

COMMENT ON COLUMN processed_events.provider IS
    'Provider that emitted the processed event.';

COMMENT ON COLUMN processed_events.environment IS
    'SubTru subscription environment associated with the processed provider event.';

COMMENT ON COLUMN processed_events.event_id IS
    'Provider event identifier, unique per project, provider, and environment.';
