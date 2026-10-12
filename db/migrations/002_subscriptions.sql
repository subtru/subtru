-- Migration: Subscriptions
-- Description: Normalized subscription state by project, provider, environment, and external user.

CREATE TABLE subscriptions (
    id                        UUID PRIMARY KEY DEFAULT extensions.gen_random_uuid(),
    project_id                UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    external_user_id          TEXT NOT NULL,
    provider                  TEXT NOT NULL,
    environment               TEXT NOT NULL DEFAULT 'production',
    provider_subscription_id  TEXT NOT NULL,
    product_id                TEXT NOT NULL,
    price_id                  TEXT,
    status                    TEXT NOT NULL,
    provider_status           TEXT,
    current_period_end        TIMESTAMPTZ NOT NULL,
    access_ends_at            TIMESTAMPTZ,
    cancel_at_period_end      BOOLEAN NOT NULL DEFAULT false,
    provider_event_created_at TIMESTAMPTZ,
    created_at                TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at                TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT subscriptions_environment_chk
        CHECK (environment IN ('production', 'test'))
);

CREATE INDEX idx_subscriptions_project_user
ON subscriptions (project_id, environment, external_user_id);

CREATE UNIQUE INDEX uniq_subscriptions_provider_sub_env
ON subscriptions (project_id, provider, environment, provider_subscription_id);

CREATE INDEX idx_subscriptions_project_status
ON subscriptions (project_id, environment, status);

CREATE INDEX idx_subscriptions_project_period_end
ON subscriptions (project_id, environment, current_period_end);

CREATE INDEX idx_subscriptions_project_access_end
ON subscriptions (project_id, environment, access_ends_at);

CREATE TRIGGER update_subscriptions_updated_at
    BEFORE UPDATE ON subscriptions
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

COMMENT ON TABLE subscriptions IS
    'Normalized subscription state by provider. Access is derived from this table and never stored directly.';

COMMENT ON COLUMN subscriptions.external_user_id IS
    'Customer-defined user identifier used for access lookups.';

COMMENT ON COLUMN subscriptions.provider_subscription_id IS
    'Provider-native subscription ID, unique per project/provider/environment.';

COMMENT ON COLUMN subscriptions.environment IS
    'SubTru subscription environment: production for live provider state, test for sandbox/test provider state.';

COMMENT ON COLUMN subscriptions.price_id IS
    'Provider-specific price identifier when available. May be null for providers that do not expose an equivalent value.';

COMMENT ON COLUMN subscriptions.provider_status IS
    'Exact provider-native status string when available. Used with normalized state for provider-specific access decisions.';

COMMENT ON COLUMN subscriptions.current_period_end IS
    'Provider-native end of the current billing period; this can differ from the entitlement access end.';

COMMENT ON COLUMN subscriptions.access_ends_at IS
    'When SubTru expects entitlement access to end. Null means access continues until a later provider state update.';

COMMENT ON COLUMN subscriptions.provider_event_created_at IS
    'Ordering timestamp used to prevent older provider state from overwriting newer state. Stripe and Apple use provider event time; Google reconciliations and manual refreshes use observation time.';
