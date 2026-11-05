#!/usr/bin/env bash
# Usage: scripts/verify.sh [unit|backend|frontend|all]  — builds and tests with compact output.
# unit = backend build + unit tests (no Docker); backend = + Testcontainers integration tests; all = backend + frontend.
set -uo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
target="${1:-all}"; status=0
[[ "$target" =~ ^(unit|backend|frontend|all)$ ]] || { echo "usage: $0 [unit|backend|frontend|all]"; exit 2; }
log="$(mktemp)"; trap 'rm -f "$log"' EXIT

run() { # name, dir, command...
  local name="$1" dir="$2"; shift 2
  if (cd "$dir" && NO_COLOR=1 "$@") >"$log" 2>&1; then
    sed -i.bak $'s/\x1b\\[[0-9;]*m//g' "$log" && rm -f "$log.bak"
    echo "✔ $name"; grep -E '^(Passed!|Failed!)|^[[:space:]]*Tests[[:space:]]+[0-9]+' "$log" | tail -3
  else
    sed -i.bak $'s/\x1b\\[[0-9;]*m//g' "$log" && rm -f "$log.bak"
    echo "✘ $name"; status=1
    grep -E 'error|Error|FAIL|Failed|✗|×|Assert' "$log" | grep -v '^[[:space:]]*$' | awk '!seen[$0]++' | head -40
  fi
}

if [[ "$target" == unit ]]; then
  run "backend build" "$root/backend" dotnet build Sellora.slnx -nologo -v q -clp:ErrorsOnly
  [[ $status -eq 0 ]] && run "backend unit tests" "$root/backend" dotnet test tests/Sellora.Tests/Sellora.Tests.csproj --no-build -nologo -v q
fi
if [[ "$target" == backend || "$target" == all ]]; then
  run "backend build" "$root/backend" dotnet build Sellora.slnx -nologo -v q -clp:ErrorsOnly
  [[ $status -eq 0 ]] && run "backend tests" "$root/backend" dotnet test Sellora.slnx --no-build -nologo -v q
fi
if [[ "$target" == frontend || "$target" == all ]]; then
  [[ -d "$root/frontend/node_modules" ]] || (cd "$root/frontend" && npm ci --silent)
  run "frontend build" "$root/frontend" npx ng build --configuration development
  run "frontend tests" "$root/frontend" npx ng test --watch=false
fi
exit $status
