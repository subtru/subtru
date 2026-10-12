-- Migration: Projects
-- Description: Shared helpers and the self-hosted project's top-level resource.

CREATE SCHEMA IF NOT EXISTS extensions;
CREATE EXTENSION IF NOT EXISTS pgcrypto WITH SCHEMA extensions;

DO $$
BEGIN
    IF to_regprocedure('extensions.gen_random_uuid()') IS NULL THEN
        RAISE EXCEPTION 'Expected extensions.gen_random_uuid() to exist. Install pgcrypto before applying SubTru migrations.';
    END IF;
END;
$$;

CREATE OR REPLACE FUNCTION update_updated_at_column()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = CURRENT_TIMESTAMP;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TABLE projects (
    id         UUID PRIMARY KEY DEFAULT extensions.gen_random_uuid(),
    name       TEXT NOT NULL,
    slug       TEXT,
    is_active  BOOLEAN NOT NULL DEFAULT true,
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT projects_name_not_blank_chk
        CHECK (BTRIM(name) <> ''),
    CONSTRAINT projects_slug_format_chk
        CHECK (slug IS NULL OR slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$')
);

CREATE INDEX idx_projects_active
ON projects (is_active)
WHERE is_active = true;

CREATE TRIGGER update_projects_updated_at
    BEFORE UPDATE ON projects
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

COMMENT ON TABLE projects IS
    'Top-level self-hosted SubTru projects. Subscriptions, API keys, webhook keys, and provider configuration are project-scoped.';

COMMENT ON COLUMN projects.is_active IS
    'Controls project access. Inactive projects reject API keys and provider webhooks.';
