#!/usr/bin/env bash
# Usage: scripts/check-db-scripts.sh <base-ref> [allow-fix]
# CI guard for the release package (docs/features/db-release-automation.md, rules R5/R6, decision D10):
# one-time data scripts in backend/db/scripts that already exist on <base-ref> must not be edited, renamed or deleted —
# environments that applied them would fail the next deploy with "applied script modified". Add a new script instead.
# Also: files under backend/src/Sellora.Infrastructure/Data/Migrations/Sql (embedded migration SQL) may only be added, never modified.
# A script that FAILED everywhere it ran may be fixed in place: pass allow-fix (CI does this when the PR has the
# `db-script-fix` label) after checking the Database releases page. File naming is checked by the migrator itself
# (integration test Repository_release_package_applies_to_the_current_schema_and_reruns_cleanly).
set -euo pipefail
base="${1:?usage: $0 <base-ref> [allow-fix]}"
allow_fix="${2:-}"

# Frozen embedded migration SQL (seed/backfill read by MigrationSql.Read): additions are fine, any edit, rename or delete is rejected —
# an applied migration must keep running the exact text it was released with (no allow-fix: a wrong resource needs a NEW migration).
sql_changed="$(git diff --name-status "$base"...HEAD -- backend/src/Sellora.Infrastructure/Data/Migrations/Sql | awk '$1 != "A" { print }')"
if [[ -n "$sql_changed" ]]; then
  echo "Existing embedded migration SQL changed since $base:"
  echo "$sql_changed" | sed 's/^/  /'
  echo "::error::Embedded migration SQL under Data/Migrations/Sql is frozen once merged. Restore it and add a new migration."
  exit 1
fi

changed="$(git diff --name-status "$base"...HEAD -- backend/db/scripts | awk '$1 != "A" { print }')"
if [[ -z "$changed" ]]; then
  echo "✔ No existing data script was modified."
  exit 0
fi

echo "Existing data scripts changed since $base:"
echo "$changed" | sed 's/^/  /'
if [[ "$allow_fix" == "allow-fix" ]]; then
  echo "::warning::Allowed by the db-script-fix label. Only valid for scripts that failed in every environment that ran them."
  exit 0
fi
echo "::error::Data scripts are final once merged. Restore them and add a new script, or label the PR db-script-fix if the script never succeeded anywhere."
exit 1
