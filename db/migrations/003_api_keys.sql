-- Migration: API keys
-- Description: Project-scoped API key authentication with HMAC-SHA256 hashed secrets.

CREATE TABLE api_keys (
    id                 UUID PRIMARY KEY DEFAULT extensions.gen_random_uuid(),
    project_id         UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    key_id             TEXT NOT NULL,
    api_key_hash       BYTEA NOT NULL,
    name               TEXT NOT NULL,
    environment        TEXT NOT NULL DEFAULT 'production',
    last_used_at       TIMESTAMPTZ,
    expires_at         TIMESTAMPTZ,
    revoked_at         TIMESTAMPTZ,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT api_keys_environment_chk
        CHECK (environment IN ('production', 'test'))
);

CREATE INDEX idx_api_keys_key_id_active
ON api_keys (key_id)
WHERE revoked_at IS NULL;

CREATE UNIQUE INDEX ux_api_keys_key_id_active
ON api_keys (key_id)
WHERE revoked_at IS NULL;

CREATE INDEX idx_api_keys_project_id_last_used
ON api_keys (project_id, last_used_at DESC);

CREATE INDEX idx_api_keys_project_environment
ON api_keys (project_id, environment, created_at DESC);

COMMENT ON TABLE api_keys IS
    'API keys for project-scoped authentication. A key authenticates access to exactly one project. Full keys are shown once at creation; only hashes are stored.';

COMMENT ON COLUMN api_keys.key_id IS
    'Public API key identifier used for indexed lookup without exposing the secret.';

COMMENT ON COLUMN api_keys.api_key_hash IS
    'HMAC-SHA256 hash of the full API key. Stored as BYTEA for constant-time comparison.';

COMMENT ON COLUMN api_keys.environment IS
    'SubTru API key environment. Production keys read live state; test keys read test state.';

COMMENT ON COLUMN api_keys.revoked_at IS
    'Timestamp when key was revoked. NULL means active.';

COMMENT ON COLUMN api_keys.last_used_at IS
    'Updated on successful authentication.';

COMMENT ON COLUMN api_keys.expires_at IS
    'Optional expiration date. NULL means no expiration.';
