-- Migration: Webhook keys
-- Description: Project-scoped provider webhook routing keys and encrypted signing secrets.

CREATE TABLE webhook_keys (
    id                 UUID PRIMARY KEY DEFAULT extensions.gen_random_uuid(),
    project_id         UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    key_id             TEXT NOT NULL,
    webhook_key_hash   BYTEA NOT NULL,
    provider           TEXT NOT NULL,
    environment        TEXT NOT NULL DEFAULT 'production',
    signing_secret_enc BYTEA,
    is_active          BOOLEAN NOT NULL DEFAULT true,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at         TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT webhook_keys_provider_chk
        CHECK (provider IN ('stripe', 'apple', 'google')),
    CONSTRAINT webhook_keys_environment_chk
        CHECK (environment IN ('production', 'test'))
);

CREATE UNIQUE INDEX ux_webhook_keys_project_provider_env_active
ON webhook_keys (project_id, provider, environment)
WHERE is_active = true;

CREATE UNIQUE INDEX ux_webhook_keys_key_id_active
ON webhook_keys (key_id)
WHERE is_active = true;

CREATE INDEX idx_webhook_keys_project_id
ON webhook_keys (project_id);

CREATE INDEX idx_webhook_keys_project_environment
ON webhook_keys (project_id, environment);

CREATE TRIGGER update_webhook_keys_updated_at
    BEFORE UPDATE ON webhook_keys
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

COMMENT ON TABLE webhook_keys IS
    'Provider webhook routing keys. Key material is hashed; signing secrets are encrypted when a provider needs one.';

COMMENT ON COLUMN webhook_keys.environment IS
    'Expected provider environment for this webhook routing key.';

COMMENT ON COLUMN webhook_keys.signing_secret_enc IS
    'AES-256-GCM encrypted webhook signing secret. NULL for providers that do not use signing secrets.';
