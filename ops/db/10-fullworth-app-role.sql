-- Least-privilege application DB role (SECURITY_ARCHITECTURE "Secrets"/"Backup", work item P0.6).
--
-- Topology:
--   * POSTGRES_USER (the container owner/admin role) owns the schema and RUNS MIGRATIONS.
--   * fullworth_app is the ROLE THE RUNNING APP CONNECTS AS: table CRUD only — no DDL, no role
--     management, no superuser, no backup rights.
--
-- Deploy sequence (operator):
--   1. Run this script once as POSTGRES_USER (e.g. mount it into /docker-entrypoint-initdb.d, or run
--      it manually against the fullworth database).
--   2. Set the fullworth_app password from a secret (do NOT keep the placeholder below).
--   3. Run EF migrations with the OWNER connection string (owner has DDL rights).
--   4. Point the app runtime ConnectionStrings:FullWorth (Security:...__FILE / env) at fullworth_app.
--
-- The app currently auto-migrates at startup; for a least-privilege runtime, run migrations as the
-- owner first (step 3) and start the app afterwards so its fullworth_app connection never needs DDL.

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fullworth_app') THEN
    -- Replace the password from a secret before use (see ops/deploy/README.md).
    CREATE ROLE fullworth_app LOGIN PASSWORD 'REPLACE_WITH_APP_ROLE_SECRET';
  END IF;
END
$$;

GRANT CONNECT ON DATABASE fullworth TO fullworth_app;
GRANT USAGE ON SCHEMA public TO fullworth_app;

-- Rights on already-existing tables/sequences.
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO fullworth_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO fullworth_app;

-- Rights on tables/sequences created later by migrations (run by the owner role). Default privileges
-- attach to objects created by the role that executes this statement, i.e. the owner.
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO fullworth_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO fullworth_app;

-- Explicitly deny schema mutation to the app role (belt and suspenders; it has no DDL grants anyway).
REVOKE CREATE ON SCHEMA public FROM fullworth_app;
