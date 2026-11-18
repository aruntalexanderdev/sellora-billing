#!/usr/bin/env bash
# Usage: scripts/check-db-scripts.sh <base-ref> [allow-fix]
# CI guard for the release package (docs/features/db-release-automation.md, rules R5/R6, decision D10):
# one-time data scripts in backend/db/scripts that already exist on <base-ref> must not be edited, renamed or deleted —
# environments that applied them would fail the next deploy with "applied script modified". Add a new script instead.
# A script that FAILED everywhere it ran may be fixed in place: pass allow-fix (CI does this when the PR has the
# `db-script-fix` label) after checking the Database releases page. File naming is checked by the migrator itself
# (integration test Repository_release_package_applies_to_the_current_schema_and_reruns_cleanly).
set -euo pipefail
base="${1:?usage: $0 <base-ref> [allow-fix]}"
allow_fix="${2:-}"

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
