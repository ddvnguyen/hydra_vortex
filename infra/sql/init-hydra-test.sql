-- init-hydra-test.sql — creates the hydra_test database (N1 isolation).
-- Idempotent: safe to run multiple times; concurrent runs are handled via
-- duplicate_database exception.
--
-- Mount strategy: NOT auto-mounted into the pg container via
-- infra/docker-compose.infra.yml (to avoid touching prod compose).
-- Instead, this file is applied idempotently by scripts/hydra-test/up.sh:
--   podman exec pg psql -U hydra -f /path/to/init-hydra-test.sql
-- or directly:
--   psql "Host=localhost;Database=postgres;Username=hydra;Password=hydra" -f infra/sql/init-hydra-test.sql
-- Verify: podman exec pg psql -U hydra -l  # should list hydra and hydra_test
--
-- NOTE: CREATE DATABASE cannot run inside an explicit transaction block in
-- Postgres. The DO block below relies on Postgres' exception handling for
-- idempotency; if the server rejects CREATE DATABASE inside DO, fall back to:
--   SELECT 'CREATE DATABASE hydra_test OWNER hydra' WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname='hydra_test') \gexec
-- The up.sh script handles both paths.

DO $$
BEGIN
    PERFORM 1 FROM pg_database WHERE datname = 'hydra_test';
    IF NOT FOUND THEN
        -- Use dblink to run CREATE DATABASE outside the current transaction
        -- when available; otherwise try direct CREATE DATABASE.
        BEGIN
            PERFORM dblink_exec('dbname=postgres', 'CREATE DATABASE hydra_test OWNER hydra');
        EXCEPTION WHEN undefined_function THEN
            -- dblink extension not installed; direct CREATE DATABASE
            -- (may fail inside DO on some Postgres versions — up.sh has fallback)
            EXECUTE 'CREATE DATABASE hydra_test OWNER hydra';
        END;
    END IF;
EXCEPTION WHEN duplicate_database THEN
    RAISE NOTICE 'database hydra_test already exists, skipping';
END
$$;
