#!/usr/bin/env bash
# Opens / closes a temporary Postgres Flexible Server firewall rule for this GitHub runner (D2, D14).
#   postgres-firewall.sh open  <job-suffix>
#   postgres-firewall.sh close <job-suffix>
# Rule name: gh-migrate-${GITHUB_RUN_ID}-${GITHUB_RUN_ATTEMPT}-<job-suffix>, so parallel jobs never share or delete each other's rule.
# Needs an Azure CLI login and the environment variables AZURE_POSTGRES_RESOURCE_GROUP and AZURE_POSTGRES_SERVER.
set -euo pipefail

action="${1:-}"
suffix="${2:-}"
if [[ ( "$action" != "open" && "$action" != "close" ) || -z "$suffix" ]]; then
  echo "usage: $0 open|close <job-suffix>" >&2
  exit 64
fi
: "${AZURE_POSTGRES_RESOURCE_GROUP:?AZURE_POSTGRES_RESOURCE_GROUP is required}"
: "${AZURE_POSTGRES_SERVER:?AZURE_POSTGRES_SERVER is required}"
: "${GITHUB_RUN_ID:?GITHUB_RUN_ID is required}"
: "${GITHUB_RUN_ATTEMPT:?GITHUB_RUN_ATTEMPT is required}"

rule="gh-migrate-${GITHUB_RUN_ID}-${GITHUB_RUN_ATTEMPT}-${suffix}"

case "$action" in
  open)
    ip="$(curl --fail --silent --show-error --retry 3 https://api.ipify.org)"
    # Written before create: the close step must run even if create half-succeeds.
    if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
      echo "rule=$rule" >> "$GITHUB_OUTPUT"
    fi
    az postgres flexible-server firewall-rule create \
      --resource-group "$AZURE_POSTGRES_RESOURCE_GROUP" --name "$AZURE_POSTGRES_SERVER" \
      --rule-name "$rule" --start-ip-address "$ip" --end-ip-address "$ip" --output none
    # No wait here: a new rule can take a few seconds to apply, and Sellora.DbMigrator retries its first connection (5 x 10 s).
    echo "Firewall rule $rule opened."
    ;;
  close)
    # A rule that does not exist (open never ran, or already removed) is not an error.
    az postgres flexible-server firewall-rule delete \
      --resource-group "$AZURE_POSTGRES_RESOURCE_GROUP" --name "$AZURE_POSTGRES_SERVER" \
      --rule-name "$rule" --yes --output none || echo "Firewall rule $rule was not present or could not be removed."
    ;;
esac
