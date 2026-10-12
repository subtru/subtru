-- Migration: Provider integrations
-- Description: Project-scoped provider configuration and encrypted provider credentials.

CREATE TABLE provider_configs (
    project_id  UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    provider    TEXT NOT NULL,
    config_json JSONB NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT provider_configs_pkey
        PRIMARY KEY (project_id, provider),
    CONSTRAINT provider_configs_provider_chk
        CHECK (provider IN ('stripe', 'apple', 'google'))
);

CREATE INDEX idx_provider_configs_project_id
ON provider_configs (project_id);

CREATE TRIGGER update_provider_configs_updated_at
    BEFORE UPDATE ON provider_configs
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

CREATE TABLE provider_credentials (
    id                UUID PRIMARY KEY DEFAULT extensions.gen_random_uuid(),
    project_id        UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    provider          TEXT NOT NULL,
    credential_type   TEXT NOT NULL,
    credential_enc    BYTEA NOT NULL,
    client_email      TEXT NOT NULL,
    private_key_id    TEXT,
    is_active         BOOLEAN NOT NULL DEFAULT true,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    rotated_at        TIMESTAMPTZ,
    last_validated_at TIMESTAMPTZ,

    CONSTRAINT provider_credentials_provider_chk
        CHECK (provider = 'google'),
    CONSTRAINT provider_credentials_type_chk
        CHECK (credential_type IN ('google_service_account_json'))
);

CREATE UNIQUE INDEX ux_provider_credentials_project_provider_active
ON provider_credentials (project_id, provider)
WHERE is_active = true;

CREATE INDEX idx_provider_credentials_project_provider
ON provider_credentials (project_id, provider, created_at DESC);

COMMENT ON TABLE provider_configs IS
    'Project-scoped provider configuration used for webhook verification and provider-specific server integration settings.';

COMMENT ON COLUMN provider_configs.config_json IS
    'Provider-specific configuration payload stored as JSONB.';

COMMENT ON TABLE provider_credentials IS
    'Encrypted project-scoped Google provider credentials stored separately from non-secret provider configuration.';

COMMENT ON COLUMN provider_credentials.credential_enc IS
    'AES-256-GCM encrypted provider credential payload.';

COMMENT ON COLUMN provider_credentials.private_key_id IS
    'Provider credential key identifier when available.';
