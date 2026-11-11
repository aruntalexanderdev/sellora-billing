-- Grants (or re-activates) a platform administrator (Phase 6 D1, owner decision O3: the ONLY way platform administrators are created;
-- the application never grants the role). Run under change control by an operator, once per operator account:
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -v username=ops.alice -v reason='Platform operator onboarding' -f scripts/sql/grant-platform-admin.sql
-- `username` is matched against Users."Username" or Users."Email" (case-insensitive); `reason` is optional (<= 500 chars).
-- Idempotent: an Active grant is left untouched (and not re-audited); a Revoked one is re-activated; one platform.admin_granted
-- PlatformAuditEvents row is written per effective change, in the same transaction. Fails (no change) when the user does not exist
-- or is Disabled, and when the user is an active company governance member (platform and company governance stay separate).
-- Warns when the user holds a plain company membership: O3 recommends a dedicated operator account without membership.
\if :{?username}
\else
  \echo 'usage: psql -v ON_ERROR_STOP=1 -v username=<username-or-email> [-v reason=<text>] -f scripts/sql/grant-platform-admin.sql'
  \quit
\endif
\if :{?reason}
\else
  \set reason 'Granted by scripts/sql/grant-platform-admin.sql'
\endif

BEGIN;

-- psql does not interpolate variables inside a dollar-quoted body: hand them over as transaction-local settings.
SELECT set_config('sellora.grant_username', :'username', true), set_config('sellora.grant_reason', :'reason', true);

DO $grant$
DECLARE
    v_username text := current_setting('sellora.grant_username');
    v_reason   text := current_setting('sellora.grant_reason');
    v_user_id  uuid;
    v_status   text;
    v_prior    text;
BEGIN
    IF length(v_reason) > 500 THEN
        RAISE EXCEPTION 'grant-platform-admin: reason is longer than 500 characters';
    END IF;

    SELECT u."Id", u."Status" INTO v_user_id, v_status
    FROM "Users" u
    WHERE lower(u."Username") = lower(v_username) OR lower(u."Email") = lower(v_username);
    IF v_user_id IS NULL THEN
        RAISE EXCEPTION 'grant-platform-admin: no user matches "%"', v_username;
    END IF;
    IF v_status = 'Disabled' THEN
        RAISE EXCEPTION 'grant-platform-admin: user "%" is disabled', v_username;
    END IF;
    IF EXISTS (SELECT 1 FROM "CompanyGovernanceMembers" g JOIN "UserCompanyMemberships" gm ON gm."Id" = g."UserCompanyMembershipId"
               WHERE gm."UserId" = v_user_id AND g."Status" = 'Active') THEN
        RAISE EXCEPTION 'grant-platform-admin: user "%" is an active company governance member; platform administrators cannot hold governance roles', v_username;
    END IF;
    IF EXISTS (SELECT 1 FROM "UserCompanyMemberships" m WHERE m."UserId" = v_user_id AND m."Status" <> 'Revoked') THEN
        RAISE WARNING 'grant-platform-admin: user "%" has a company membership; a dedicated operator account without membership is recommended (O3)', v_username;
    END IF;

    SELECT p."Status" INTO v_prior FROM "PlatformAdministrators" p WHERE p."UserId" = v_user_id;
    IF v_prior = 'Active' THEN
        RAISE NOTICE 'grant-platform-admin: "%" is already an active platform administrator; nothing to do', v_username;
        RETURN;
    END IF;

    IF v_prior IS NOT NULL THEN
        UPDATE "PlatformAdministrators"
        SET "Status" = 'Active', "GrantedAt" = now(), "GrantedByUserId" = NULL, "RevokedAt" = NULL, "RevokedByUserId" = NULL, "Reason" = v_reason
        WHERE "UserId" = v_user_id;
    ELSE
        INSERT INTO "PlatformAdministrators" ("UserId", "Status", "GrantedAt", "GrantedByUserId", "RevokedAt", "RevokedByUserId", "Reason")
        VALUES (v_user_id, 'Active', now(), NULL, NULL, NULL, v_reason);
    END IF;

    INSERT INTO "PlatformAuditEvents" ("Id", "OccurredAt", "ActorUserId", "Action", "TargetType", "TargetId", "CompanyId", "Reason", "Before", "After", "CorrelationId")
    VALUES (gen_random_uuid(), now(), NULL, 'platform.admin_granted', 'PlatformAdministrator', v_user_id, NULL, v_reason,
            CASE WHEN v_prior IS NULL THEN NULL ELSE jsonb_build_object('status', v_prior) END,
            jsonb_build_object('status', 'Active', 'via', 'scripts/sql/grant-platform-admin.sql'),
            'ops-script');
END
$grant$;

COMMIT;
