-- Migration: Google purchase remediations
-- Description: Audit trail for manual Google purchase bind-and-acknowledge actions.

CREATE TABLE google_purchase_remediations (
    id                       UUID PRIMARY KEY DEFAULT extensions.gen_random_uuid(),
    project_id               UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    environment              TEXT NOT NULL DEFAULT 'production',
    requested_purchase_token TEXT NOT NULL,
    effective_purchase_token TEXT NOT NULL,
    linked_purchase_token    TEXT,
    external_user_id         TEXT NOT NULL,
    actor                    TEXT NOT NULL,
    status                   TEXT NOT NULL,
    provider_status          TEXT,
    current_period_end       TIMESTAMPTZ NOT NULL,
    acknowledged_at          TIMESTAMPTZ NOT NULL,
    created_at               TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT google_purchase_remediations_environment_chk
        CHECK (environment IN ('production', 'test'))
);

CREATE INDEX idx_google_purchase_remediations_project_created_at
ON google_purchase_remediations (project_id, environment, created_at DESC);

CREATE INDEX idx_google_purchase_remediations_effective_token
ON google_purchase_remediations (project_id, environment, effective_purchase_token);

COMMENT ON TABLE google_purchase_remediations IS
    'Audit log of manual Google purchase bind-and-acknowledge remediation actions.';

COMMENT ON COLUMN google_purchase_remediations.requested_purchase_token IS
    'Purchase token supplied by the operator.';

COMMENT ON COLUMN google_purchase_remediations.effective_purchase_token IS
    'Purchase token actually remediated after linked-token resolution.';

COMMENT ON COLUMN google_purchase_remediations.environment IS
    'SubTru subscription environment for the remediated Google purchase.';

COMMENT ON COLUMN google_purchase_remediations.actor IS
    'Authenticated actor that performed the remediation (currently installation_admin).';
