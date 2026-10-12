-- Migration: Runtime database security
-- Description: Optional least-privilege roles for self-hosted SubTru.
-- Run this after all schema migrations. It requires permission to create roles.

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'subtru_api_runtime') THEN
        CREATE ROLE subtru_api_runtime NOLOGIN;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'subtru_api_app') THEN
        CREATE ROLE subtru_api_app LOGIN INHERIT;
    ELSE
        ALTER ROLE subtru_api_app LOGIN INHERIT;
    END IF;

    GRANT subtru_api_runtime TO subtru_api_app;
END;
$$;

COMMENT ON ROLE subtru_api_runtime IS
    'Privilege role for SubTru API runtime connections.';

COMMENT ON ROLE subtru_api_app IS
    'Login role for SubTru API runtime connections. Configure its credential outside migrations.';

DO $$
DECLARE
    database_name TEXT := current_database();
BEGIN
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO subtru_api_runtime', database_name);
END;
$$;

REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO subtru_api_runtime;

REVOKE ALL ON ALL TABLES IN SCHEMA public FROM PUBLIC;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA public FROM PUBLIC;

ALTER DEFAULT PRIVILEGES FOR ROLE CURRENT_USER IN SCHEMA public
    REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE CURRENT_USER IN SCHEMA public
    REVOKE ALL ON SEQUENCES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE CURRENT_USER IN SCHEMA public
    REVOKE ALL ON FUNCTIONS FROM PUBLIC;

GRANT SELECT ON ALL TABLES IN SCHEMA public TO subtru_api_runtime;

GRANT INSERT, UPDATE ON TABLE
    api_keys,
    google_purchase_remediations,
    google_subscription_snapshots,
    processed_events,
    projects,
    provider_configs,
    provider_credentials,
    subscriptions,
    webhook_events,
    webhook_keys
TO subtru_api_runtime;

GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO subtru_api_runtime;
GRANT EXECUTE ON FUNCTION update_updated_at_column() TO subtru_api_runtime;

GRANT USAGE ON SCHEMA extensions TO subtru_api_runtime;
GRANT EXECUTE ON FUNCTION extensions.gen_random_uuid() TO subtru_api_runtime;

ALTER DEFAULT PRIVILEGES FOR ROLE CURRENT_USER IN SCHEMA public
    GRANT SELECT ON TABLES TO subtru_api_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE CURRENT_USER IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO subtru_api_runtime;
