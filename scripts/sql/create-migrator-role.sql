-- Creates (or updates the password of) the sellora_migrator login used by the deploy pipeline's migrate-db job
-- (docs/features/db-release-automation.md, OQ1). Run ONCE per environment by an operator, as the server admin:
--   psql "$ADMIN_DATABASE_URL" -v ON_ERROR_STOP=1 -v password='<generated>' -v owner_role=<role that owns the Sellora tables> \
--        -f scripts/sql/create-migrator-role.sql
-- `owner_role` is the login the API used while it migrated on startup (it owns every table today). Membership in it lets the
-- migrator ALTER those tables and own new ones. Put the resulting connection string in the GitHub environment secret
-- SELLORA_DB_MIGRATOR_CONNECTION (never in the repository):
--   Host=<server>.postgres.database.azure.com;Database=<db>;Username=sellora_migrator;Password=<generated>;Ssl Mode=Require
-- Privilege model: sellora_migrator logs in, then works AS owner_role (ALTER ROLE ... SET role), so every object it creates — the
-- ops journal included — is owned by owner_role exactly like the tables the API created. Grants therefore go to owner_role.
-- Known trade-off: the API login (owner_role) can also write the ops journal. A dedicated journal owner can come with the prod setup.
-- NEVER pass a superuser / azure_pg_admin role as owner_role: the migrator would inherit it.
-- The admin running this needs ADMIN OPTION on owner_role (PostgreSQL 16 gives it to the role's creator).
-- Idempotent: re-running only changes the password and re-applies the grants.
\if :{?password}
\else
  \echo 'usage: psql -v ON_ERROR_STOP=1 -v password=<generated> -v owner_role=<owner> -f scripts/sql/create-migrator-role.sql'
  \quit
\endif
\if :{?owner_role}
\else
  \echo 'owner_role is required (the role that owns the Sellora tables, e.g. the API login)'
  \quit
\endif

-- Refuse an unknown owner role, or one with server-admin rights (the migrator would inherit them).
SELECT NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'owner_role') AS missing,
       EXISTS (SELECT 1 FROM pg_roles r WHERE r.rolname = :'owner_role' AND (r.rolsuper OR EXISTS (
           SELECT 1 FROM pg_auth_members m JOIN pg_roles a ON a.oid = m.roleid
           WHERE a.rolname = 'azure_pg_admin' AND m.member = r.oid))) AS privileged \gset owner_
\if :owner_missing
  \echo 'owner_role does not exist'
  \quit
\endif
\if :owner_privileged
  \echo 'owner_role must not be a superuser or azure_pg_admin member'
  \quit
\endif

SELECT format('CREATE ROLE sellora_migrator LOGIN PASSWORD %L', :'password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'sellora_migrator') \gexec
SELECT format('ALTER ROLE sellora_migrator WITH LOGIN PASSWORD %L', :'password') \gexec

-- Owner privileges on existing objects. CREATE on the database for owner_role: the migrator creates schema ops (as owner_role).
SELECT format('GRANT %I TO sellora_migrator', :'owner_role') \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO sellora_migrator', current_database()) \gexec
SELECT format('GRANT CREATE ON DATABASE %I TO %I', current_database(), :'owner_role') \gexec

-- New objects are created as the owner role, so the API keeps its access exactly as before.
ALTER ROLE sellora_migrator SET role = :'owner_role';

SELECT 'sellora_migrator ready; member of ' || :'owner_role' AS result;
