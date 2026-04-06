#!/usr/bin/env bash
# set-domain.sh — Switch debug domain across all launchSettings.json and appsettings.json files.
#
# Usage:
#   ./scripts/set-domain.sh set <domain>   — Set APP_DOMAIN and Database=vNext_<domain>
#   ./scripts/set-domain.sh reset          — Reset to defaults (core / Aether_WorkflowDb)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

DEFAULT_DOMAIN="core"
DEFAULT_DB="Aether_WorkflowDb"

LAUNCH_SETTINGS=(
  "orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Properties/launchSettings.json"
  "execution/BBT.Workflow.Execution.HttpApi.Host/Properties/launchSettings.json"
  "workers/BBT.Workflow.Workers.Inbox/Properties/launchSettings.json"
  "workers/BBT.Workflow.Workers.Outbox/Properties/launchSettings.json"
  "workers/BBT.Workflow.DbMigrator/Properties/launchSettings.json"
)

APP_SETTINGS=(
  "orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json"
  "workers/BBT.Workflow.Workers.Inbox/appsettings.json"
  "workers/BBT.Workflow.Workers.Outbox/appsettings.json"
  "workers/BBT.Workflow.DbMigrator/appsettings.json"
)

usage() {
  echo "Usage:"
  echo "  $0 set <domain>   Set APP_DOMAIN=<domain> and Database=vNext_<domain>"
  echo "  $0 reset          Reset to defaults (APP_DOMAIN=core, Database=Aether_WorkflowDb)"
  exit 1
}

apply() {
  local domain="$1"
  local db_name="$2"

  echo "APP_DOMAIN  → $domain"
  echo "Database    → $db_name"
  echo ""

  for rel in "${LAUNCH_SETTINGS[@]}"; do
    local path="$ROOT/$rel"
    if [ -f "$path" ]; then
      sed -i '' "s/\"APP_DOMAIN\": \"[^\"]*\"/\"APP_DOMAIN\": \"$domain\"/g" "$path"
      echo "  [launchSettings] $rel"
    else
      echo "  [SKIP] Not found: $rel"
    fi
  done

  for rel in "${APP_SETTINGS[@]}"; do
    local path="$ROOT/$rel"
    if [ -f "$path" ]; then
      sed -i '' "/Port=5432/s/Database=[^;]*/Database=$db_name/g" "$path"
      echo "  [appsettings]   $rel"
    else
      echo "  [SKIP] Not found: $rel"
    fi
  done

  echo ""
  echo "Done."
}

case "${1:-}" in
  set)
    [ -z "${2:-}" ] && { echo "ERROR: domain name is required."; echo ""; usage; }
    apply "$2" "vNext_$2"
    ;;
  reset)
    apply "$DEFAULT_DOMAIN" "$DEFAULT_DB"
    ;;
  *)
    usage
    ;;
esac
