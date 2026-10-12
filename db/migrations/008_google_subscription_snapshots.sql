-- Migration: Encrypted Google subscription snapshots
-- Description: Retains subscriptionsv2 responses for webhook diagnostics.

CREATE TABLE google_subscription_snapshots (
    id                         UUID PRIMARY KEY DEFAULT extensions.gen_random_uuid(),
    project_id                 UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    environment                TEXT NOT NULL,
    event_id                   TEXT NOT NULL,
    fetch_sequence             INTEGER NOT NULL,
    purchase_token_fingerprint TEXT NOT NULL,
    fetched_at                 TIMESTAMPTZ NOT NULL,
    subscription_state         TEXT,
    product_ids                TEXT[] NOT NULL DEFAULT '{}',
    response_enc               BYTEA NOT NULL,
    created_at                 TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT google_subscription_snapshots_environment_chk
        CHECK (environment IN ('production', 'test')),
    CONSTRAINT google_subscription_snapshots_fetch_sequence_chk
        CHECK (fetch_sequence > 0)
);

CREATE INDEX idx_google_subscription_snapshots_event
ON google_subscription_snapshots (
    project_id,
    environment,
    event_id,
    fetched_at DESC
);

COMMENT ON TABLE google_subscription_snapshots IS
    'Encrypted Google subscriptionsv2 snapshots captured during RTDN processing for service diagnostics.';

COMMENT ON COLUMN google_subscription_snapshots.purchase_token_fingerprint IS
    'SHA-256 fingerprint of the requested purchase token; the token itself is not stored.';

COMMENT ON COLUMN google_subscription_snapshots.response_enc IS
    'AES-256-GCM encrypted, versioned bytes containing the exact successful Google API response body.';
