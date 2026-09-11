#!/usr/bin/env bash
# run-docker.sh — the one entry point for running vNext locally.
#
# Docker stacks (unchanged behaviour, attached, Ctrl+C stops them):
#   ./run-docker.sh                     # infrastructure only (postgres, redis, vault, dapr, otel …)  [default]
#   ./run-docker.sh dev   [domain]      # infra + orchestration/execution/workers/migrator built into containers
#   ./run-docker.sh stage [domain]      # same with release images        (dev/stage: one domain at a time)
#
# Local hosts, one or MORE domains side by side (infra in docker, runtime as locally built binaries):
#   ./run-docker.sh up [domain] [--offset N] [--no-build] [--skip-migrate] [--db <name>]
#   ./run-docker.sh plan <domain> [--offset N]     # show ports / app-ids / env, write the sidecar compose — start nothing
#   ./run-docker.sh switch <domain>                # stop every running domain, then `up <domain>`
#   ./run-docker.sh down [domain|--all] [--infra]  # stop one domain's hosts+sidecars (default: all)
#   ./run-docker.sh restart <domain> | status | logs <domain> [host] | domains
#
# Port offsets (app ports follow burgan-tech/vnext-runtime `create-domain.sh`: base + offset):
#   core is offset 0 and uses the sidecars declared in docker-compose.yml, app-ids and Dapr ports
#   unchanged. Any other domain gets an offset (1-99, 5 is reserved for discovery): asked in a
#   terminal, default = next free multiple of 10. The script refuses an offset whose ports collide
#   with core or with another registered domain.
#     host            app port     dapr http / grpc (offset domains)   app-id (offset domains)
#     orchestration   4201+o       50000+app / 55000+app               vnext-<domain>-app
#     execution       4202+o       "                                   vnext-<domain>-execution-app
#     outbox          4401+o       "                                   vnext-<domain>-worker-outbox
#     inbox           4501+o       "                                   vnext-<domain>-worker-inbox
#     db-migrator     (4301+o)     "  (pseudo app port, no listener)   vnext-<domain>-db-migrator
#     init            3005+o       —  package publisher aimed at this domain's orchestration
#   Every sidecar port here is published on localhost, so Dapr ports are derived from the (unique)
#   app port instead of vnext-runtime's base+offset*100, which collides on a shared host
#   (42110+10*100 = execution's 43110; 44110+100 = the migrator's 44210).
#   An offset domain gets its own sidecar set: <service>-<domain> containers generated from the
#   base compose file into .vnext-local/domains/<domain>/sidecars.json, compose project vnext-<domain>.
#
# Domain handling — NO tracked file is edited. Each host gets the environment of its `http`
# launch profile (Properties/launchSettings.json) with APP_DOMAIN, the connection string, the Dapr
# ports/app-ids and the cross-host references (ExecutionApi__AppId, OrchestrationApi__AppId,
# vNextApi__BaseUrl) overridden per process. Database: Aether_WorkflowDb for core, vNext_<domain>
# otherwise, or --db.
#
# Records: every successful `up` writes ai-docs/local-environments/<domain>.md (+ README.md index and
# environments.json) — ports, app-ids, database, logs, git rev — so a session can be reproduced later.
# ai-docs/ is git-ignored. Runtime state (pids, logs) lives in .vnext-local/ (git-ignored).
#
# Requires: docker (compose v2), dotnet SDK, jq, python3, curl, lsof. macOS bash 3.2 compatible.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
STATE="$ROOT/.vnext-local"
DOMAINS="$STATE/domains"
RECORDS="$ROOT/ai-docs/local-environments"
COMPOSE_FILE="$HERE/docker-compose.yml"
DEFAULT_DOMAIN="core"
DEFAULT_DB="Aether_WorkflowDb"
HEALTH_TIMEOUT="${VNEXT_HEALTH_TIMEOUT:-120}"
BASE_SERVICES="postgres redis vault dapr-scheduler otel-collector"

# name|project dir|launch profile|base app port|needs db|base sidecar compose service|base dapr http port|base app-id|extra env for offset domains (; separated; {orch_url} {exec_id} {orch_id} substituted)|listens on app port
HOSTS="orchestration|orchestration/BBT.Workflow.Orchestration.HttpApi.Host|http|4201|1|vnext-orchestration-dapr|42110|vnext-app|ExecutionApi__AppId={exec_id};vNextApi__BaseUrl={orch_url}|1
execution|execution/BBT.Workflow.Execution.HttpApi.Host|http|4202|0|vnext-execution-dapr|43110|vnext-execution-app|OrchestrationApi__AppId={orch_id}|1
inbox|workers/BBT.Workflow.Workers.Inbox|http|4501|1|vnext-worker-inbox-dapr|45110|vnext-worker-inbox|OrchestrationApi__AppId={orch_id}|1
outbox|workers/BBT.Workflow.Workers.Outbox|http|4401|1|vnext-worker-outbox-dapr|44110|vnext-worker-outbox||1"
MIGRATOR="migrator|workers/BBT.Workflow.DbMigrator|DbMigrator|4301|1|vnext-db-migrator-dapr|44210|vnext-db-migrator||0"

# ---------- helpers -------------------------------------------------------------------------

log()  { printf '\033[1;34m▸\033[0m %s\n' "$*"; }
ok()   { printf '\033[1;32m✓\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m!\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31m✗\033[0m %s\n' "$*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || die "'$1' is required but not installed"; }

compose() { docker compose -f "$COMPOSE_FILE" "$@"; }
f() { printf '%s' "$1" | cut -d'|' -f"$2"; }          # f <row> <field>

selected_hosts() { printf '%s\n' "$HOSTS"; }
pid_alive() { [ -n "${1:-}" ] && kill -0 "$1" 2>/dev/null; }
port_busy() { lsof -nP -iTCP:"$1" -sTCP:LISTEN >/dev/null 2>&1; }

dom_dir() { printf '%s/%s' "$DOMAINS" "$1"; }
stored() { cat "$(dom_dir "$1")/$2" 2>/dev/null || true; }   # stored <domain> <key>
registered_domains() { local d; for d in "$DOMAINS"/*/; do [ -f "$d/offset" ] && basename "$d"; done 2>/dev/null; return 0; }
domain_running() {   # any live pid for this domain
  local p; for p in "$(dom_dir "$1")"/pids/*.pid; do [ -f "$p" ] && pid_alive "$(cat "$p")" && return 0; done; return 1
}

db_for_domain() {
  if [ -n "${DB_OVERRIDE:-}" ]; then printf '%s' "$DB_OVERRIDE"
  elif [ "$1" = "$DEFAULT_DOMAIN" ]; then printf '%s' "$DEFAULT_DB"
  else printf 'vNext_%s' "$1"; fi
}

# ports: app_port <base> <offset>; dapr_http/dapr_grpc <row> <offset> (core keeps the compose values)
app_port()  { echo $(( $1 + $2 )); }
dapr_http() { if [ "$2" = 0 ]; then f "$1" 7; else echo $(( 50000 + $(f "$1" 4) + $2 )); fi; }
dapr_grpc() { if [ "$2" = 0 ]; then echo $(( $(f "$1" 7) + 1 )); else echo $(( 55000 + $(f "$1" 4) + $2 )); fi; }
host_url()  { if [ "$(f "$1" 10)" = 1 ]; then printf 'http://localhost:%s' "$(app_port "$(f "$1" 4)" "$2")"; else printf '—'; fi; }
init_port() { echo $(( 3005 + $1 )); }            # init (package publisher) service, vnext-runtime convention
init_name() { if [ "$2" = 0 ]; then printf 'init'; else printf 'init-%s' "$1"; fi; }   # <domain> <offset>
# every localhost port a domain publishes, one per line
domain_ports() {   # <offset>
  local h; while IFS= read -r h; do
    [ "$(f "$h" 10)" = 1 ] && app_port "$(f "$h" 4)" "$1"; dapr_http "$h" "$1"; dapr_grpc "$h" "$1"
  done < <(selected_hosts; printf '%s\n' "$MIGRATOR")
  init_port "$1"
}
# app-id for <base id> <domain> <offset>: offset 0 keeps the base, others insert the domain
app_id() { if [ "$3" = 0 ]; then printf '%s' "$1"; else printf 'vnext-%s-%s' "$2" "${1#vnext-}"; fi; }
sidecar_name() { if [ "$3" = 0 ]; then printf '%s' "$1"; else printf '%s-%s' "$1" "$2"; fi; }   # <base svc> <domain> <offset>

resolve_domain() {   # resolve_domain [domain] → argument, else ask (default: last used, else core)
  local domain="${1:-}" last
  if [ -z "$domain" ]; then
    last="$(cat "$STATE/last" 2>/dev/null || echo "$DEFAULT_DOMAIN")"
    if [ -t 0 ]; then printf 'Domain [%s]: ' "$last" >&2; read -r domain; fi
    domain="${domain:-$last}"
  fi
  [[ "$domain" =~ ^[A-Za-z0-9_-]+$ ]] || die "invalid domain name: '$domain'"
  printf '%s' "$domain"
}

next_free_offset() {
  local used d o cand=10
  used=" "; for d in $(registered_domains); do o="$(stored "$d" offset)"; [ -n "$o" ] && used="$used$o "; done
  while printf '%s' "$used" | grep -q " $cand "; do cand=$((cand+10)); done
  echo "$cand"
}

resolve_offset() {   # resolve_offset <domain> [offset] → explicit, else stored, else ask/next free
  local domain="$1" offset="${2:-}" d o
  if [ "$domain" = "$DEFAULT_DOMAIN" ]; then
    [ -z "$offset" ] || [ "$offset" = 0 ] || die "core is always offset 0 (it owns the sidecars in docker-compose.yml)"
    echo 0; return
  fi
  [ -n "$offset" ] || offset="$(stored "$domain" offset)"
  if [ -z "$offset" ]; then
    local def; def="$(next_free_offset)"
    if [ -t 0 ]; then printf 'Port offset for %s [%s]: ' "$domain" "$def" >&2; read -r offset; fi
    offset="${offset:-$def}"
  fi
  [[ "$offset" =~ ^[0-9]+$ ]] && [ "$offset" -ge 1 ] && [ "$offset" -le 99 ] || die "offset must be 1..99 (core owns 0)"
  [ "$offset" = 5 ] && die "offset 5 is reserved for the discovery domain (vnext-runtime convention)"
  local mine theirs clash
  mine="$(domain_ports "$offset" | sort -u)"
  clash="$(printf '%s\n' "$mine" | grep -xF -f <(domain_ports 0) || true)"
  [ -z "$clash" ] || die "offset $offset collides with core on port(s): $(echo $clash)"
  for d in $(registered_domains); do
    [ "$d" = "$domain" ] && continue
    o="$(stored "$d" offset)"; [ "$o" = "$offset" ] && die "offset $offset is already used by domain '$d'"
    theirs="$(domain_ports "$o")"
    clash="$(printf '%s\n' "$mine" | grep -xF -f <(printf '%s\n' "$theirs") || true)"
    [ -z "$clash" ] || die "offset $offset collides with domain '$d' (offset $o) on port(s): $(echo $clash)"
  done
  echo "$offset"
}

# prints KEY=VALUE lines of a launch profile (+ ASPNETCORE_URLS from applicationUrl)
profile_env() {
  local file="$ROOT/$1/Properties/launchSettings.json"
  [ -f "$file" ] || die "launchSettings not found: $file"
  sed '1s/^\xEF\xBB\xBF//' "$file" | jq -r --arg p "$2" '
    .profiles[$p] as $pr
    | ($pr.environmentVariables // {} | to_entries[] | "\(.key)=\(.value)"),
      (if $pr.applicationUrl then "ASPNETCORE_URLS=\($pr.applicationUrl)" else empty end)'
}

connection_string() {   # <project> <db>
  sed '1s/^\xEF\xBB\xBF//' "$ROOT/$1/appsettings.json" | jq -r '.ConnectionStrings.Default' | sed -E "s/Database=[^;]*/Database=$2/"
}

target_dll() { dotnet msbuild "$ROOT/$1" -getProperty:TargetPath -nologo 2>/dev/null | tr -d '\r'; }

# load_env <row> <domain> <offset> <db> → fills ENVS (array). Profile first, overrides last (env(1) keeps the last one).
load_env() {
  local row="$1" domain="$2" offset="$3" db="$4" line project profile port extra
  project="$(f "$row" 2)"; profile="$(f "$row" 3)"
  ENVS=()
  while IFS= read -r line; do [ -n "$line" ] && ENVS+=("$line"); done < <(profile_env "$project" "$profile")
  ENVS+=("APP_DOMAIN=$domain")
  [ "$(f "$row" 5)" = 1 ] && ENVS+=("ConnectionStrings__Default=$(connection_string "$project" "$db")")
  if [ "$offset" != 0 ]; then
    ENVS+=("DAPR_APP_ID=$(app_id "$(f "$row" 8)" "$domain" "$offset")")
    ENVS+=("DAPR_HTTP_PORT=$(dapr_http "$row" "$offset")")
    ENVS+=("DAPR_GRPC_PORT=$(dapr_grpc "$row" "$offset")")
    ENVS+=("OTEL_SERVICE_NAME=$(app_id "$(f "$row" 8)" "$domain" "$offset")")
    [ "$(f "$row" 10)" = 1 ] && ENVS+=("ASPNETCORE_URLS=http://localhost:$(app_port "$(f "$row" 4)" "$offset")")
    extra="$(f "$row" 9)"
    if [ -n "$extra" ]; then
      extra="${extra//\{orch_url\}/http://localhost:$(app_port 4201 "$offset")}"
      extra="${extra//\{exec_id\}/$(app_id vnext-execution-app "$domain" "$offset")}"
      extra="${extra//\{orch_id\}/$(app_id vnext-app "$domain" "$offset")}"
      while IFS= read -r line; do [ -n "$line" ] && ENVS+=("$line"); done < <(printf '%s\n' "$extra" | tr ';' '\n')
    fi
  fi
  # keep the last value per key so the list reads the way env(1) applies it
  local deduped=()
  while IFS= read -r line; do deduped+=("$line"); done < <(printf '%s\n' "${ENVS[@]}" | awk -F= '{k=$1; v[k]=$0; if (!(k in seen)) {seen[k]=1; order[++n]=k}} END {for (i=1;i<=n;i++) print v[order[i]]}')
  ENVS=("${deduped[@]}")
  return 0
}

# ---------- infra ---------------------------------------------------------------------------

# The sidecars only work next to THIS repo's infra (they resolve `vnext-redis` on bbt-development).
# Another checkout (e.g. vnext-client-sdk-core/backend/vnext/docker) uses the same compose project
# name "docker" and service names, so `compose up` would adopt its containers and then start
# sidecars on the wrong network. Refuse instead of guessing.
check_infra_owner() {
  local svc id files
  for svc in redis postgres; do
    id="$(compose ps -q "$svc" 2>/dev/null | head -1)"; [ -n "$id" ] || continue
    files="$(docker inspect "$id" --format '{{index .Config.Labels "com.docker.compose.project.config_files"}}' 2>/dev/null || true)"
    if [ -n "$files" ] && ! printf '%s' "$files" | grep -qF "$COMPOSE_FILE"; then
      die "docker infra is running from another compose file:
    $files
  This script needs the sidecars from $COMPOSE_FILE (same project name, different network).
  Stop that stack first:  docker compose -f <that file> down
  then run this command again."
    fi
  done
}

wait_sidecar() {   # wait_sidecar <container name> <http port>
  # /v1.0/healthz stays 500 until the APP listens on --app-port, which happens after this step —
  # /v1.0/healthz/outbound turns 204 as soon as the components are loaded, which is what we need here.
  local i=0
  until curl -fs -o /dev/null "http://localhost:$2/v1.0/healthz/outbound"; do
    i=$((i+1))
    if [ "$(docker inspect "$1" --format '{{.State.Running}}' 2>/dev/null)" != "true" ]; then
      docker logs --tail 5 "$1" 2>&1 | grep -i "fatal\|error" | cut -c1-300 >&2 || true
      die "$1 is not running — see: docker logs $1"
    fi
    [ $i -gt 60 ] && die "$1 healthz (port $2) not ready after 60s"
    sleep 1
  done
}

# writes the per-domain sidecar compose (JSON) derived from the base file's sidecar definitions
write_sidecars_json() {   # <domain> <offset> <out file>
  local domain="$1" offset="$2" out="$3" h spec=""
  compose config --format json > "$out.base.json" 2>/dev/null || die "docker compose config failed for $COMPOSE_FILE"
  # one line per sidecar: base service|new container|new app-id|app port ('' = none)|dapr http|dapr grpc
  while IFS= read -r h; do
    spec="$spec$(f "$h" 6)|$(sidecar_name "$(f "$h" 6)" "$domain" "$offset")|$(app_id "$(f "$h" 8)" "$domain" "$offset")|$([ "$(f "$h" 10)" = 1 ] && app_port "$(f "$h" 4)" "$offset")|$(dapr_http "$h" "$offset")|$(dapr_grpc "$h" "$offset")
"
  done < <(selected_hosts; printf '%s\n' "$MIGRATOR")
  python3 - "$out" "$domain" "$spec" "$(app_port 4201 "$offset")" "$(init_port "$offset")" <<'PYGEN'
import json, sys, copy
out, domain, spec, orch_port, init_port = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5]
base = json.load(open(out + ".base.json"))["services"]
svcs = {}
# init (package publisher) for this domain: same image/build as core's, but aimed at this domain's orchestration
init = copy.deepcopy(base["init"])
init["container_name"] = f"init-{domain}"
init["environment"] = {"PACKAGE_API_PORT": "3000", "APP_DOMAIN": domain,
                       "VNEXT_APP_URL": f"http://host.orb.local:{orch_port}"}
init["ports"] = [{"target": 3000, "published": init_port, "protocol": "tcp"}]
init["networks"] = {"bbt-development": None}
init["extra_hosts"] = ["host.orb.local=host-gateway"]
init.pop("depends_on", None)
svcs[f"init-{domain}"] = init
for line in spec.strip().splitlines():
    svc, cname, app_id, app_port, http, grpc = line.split("|")
    s = copy.deepcopy(base[svc]); cmd = s["command"]
    def setarg(flag, val):
        if flag in cmd: cmd[cmd.index(flag) + 1] = str(val)
    setarg("--app-id", app_id); setarg("--dapr-http-port", http); setarg("--dapr-grpc-port", grpc)
    if app_port: setarg("--app-port", app_port)
    s["container_name"] = cname
    s["ports"] = [{"target": int(http), "published": http, "protocol": "tcp"},
                  {"target": int(grpc), "published": grpc, "protocol": "tcp"}]
    s["networks"] = {"bbt-development": {"aliases": [f"{app_id}-dapr"]}}
    s.pop("depends_on", None)
    svcs[cname] = s
json.dump({"name": f"vnext-{domain}", "services": svcs,
           "networks": {"bbt-development": {"external": True}},
           "volumes": {"npm-cache": {}}}, open(out, "w"), indent=1)
PYGEN
  rm -f "$out.base.json"
}

ensure_infra() {   # <domain> <offset>
  local domain="$1" offset="$2" services="$BASE_SERVICES" h
  log "docker infra"
  check_infra_owner
  docker network create bbt-development >/dev/null 2>&1 || true
  if [ "$offset" = 0 ]; then
    services="$services init"
    while IFS= read -r h; do services="$services $(f "$h" 6)"; done < <(selected_hosts)
    [ "${SKIP_MIGRATE:-0}" = "1" ] || services="$services $(f "$MIGRATOR" 6)"
  fi
  # shellcheck disable=SC2086
  compose up -d --no-recreate --quiet-pull $services > "$DDIR/logs/infra.log" 2>&1 \
    || { tail -n 20 "$DDIR/logs/infra.log" >&2; die "docker compose up failed — see $DDIR/logs/infra.log"; }
  local i=0
  until compose exec -T postgres pg_isready -U postgres >/dev/null 2>&1; do
    i=$((i+1)); [ $i -gt 60 ] && die "postgres did not become ready in 60s"; sleep 1
  done
  ok "postgres ready"
  if [ "$offset" != 0 ]; then
    write_sidecars_json "$domain" "$offset" "$DDIR/sidecars.json"
    docker compose -f "$DDIR/sidecars.json" up -d --no-recreate --quiet-pull >> "$DDIR/logs/infra.log" 2>&1 \
      || { tail -n 20 "$DDIR/logs/infra.log" >&2; die "sidecar compose up failed — see $DDIR/logs/infra.log"; }
  fi
  while IFS= read -r h; do wait_sidecar "$(sidecar_name "$(f "$h" 6)" "$domain" "$offset")" "$(dapr_http "$h" "$offset")"; done < <(selected_hosts)
  [ "${SKIP_MIGRATE:-0}" = "1" ] || wait_sidecar "$(sidecar_name "$(f "$MIGRATOR" 6)" "$domain" "$offset")" "$(dapr_http "$MIGRATOR" "$offset")"
  ok "dapr sidecars ready"
}

# ---------- hosts ---------------------------------------------------------------------------

build_all() {
  local h
  log "dotnet build"
  while IFS= read -r h; do dotnet build "$ROOT/$(f "$h" 2)" --nologo -v q "-clp:ErrorsOnly;NoSummary"; done < <(selected_hosts)
  [ "${SKIP_MIGRATE:-0}" = "1" ] || dotnet build "$ROOT/$(f "$MIGRATOR" 2)" --nologo -v q "-clp:ErrorsOnly;NoSummary"
  ok "build ok"
}

migrate() {   # <domain> <offset> <db>
  local dll logf="$DDIR/logs/migrator.log" failed
  log "DbMigrator  domain=$1  database=$3"
  dll="$(target_dll "$(f "$MIGRATOR" 2)")"; [ -f "$dll" ] || die "migrator not built: $dll (drop --no-build)"
  load_env "$MIGRATOR" "$1" "$2" "$3"
  (cd "$ROOT/$(f "$MIGRATOR" 2)" && exec env "${ENVS[@]}" dotnet "$dll") > "$logf" 2>&1 \
    || { tail -n 30 "$logf" >&2; die "migration failed — see $logf"; }
  # SchemaMigrationRunner logs per-schema failures and continues, so the exit code alone lies
  failed="$( { grep -o "Migration failed for schema [A-Za-z0-9_-]*" "$logf" || true; } | sort -u | wc -l | tr -d ' ')"   # grep=1 on "no failures" must not trip pipefail
  if [ "${failed:-0}" -gt 0 ]; then
    grep -m3 -A2 "Migration failed for schema" "$logf" | cut -c1-200 >&2
    die "$failed schema migration(s) failed (usually the migrator sidecar / Dapr lock) — see $logf"
  fi
  grep -q "All migrations completed successfully" "$logf" || die "migrator did not report completion — see $logf"
  ok "migration done ($logf)"
}

check_ports_free() {   # <offset> <domain>
  local h p
  while IFS= read -r h; do
    p="$(app_port "$(f "$h" 4)" "$1")"; port_busy "$p" && die "port $p ($(f "$h" 1)) is already in use — another instance outside this script? run 'down' first"
    if [ "$1" != 0 ]; then   # our own sidecars are not up yet, so these must be free too (unless it is our sidecar)
      for p in "$(dapr_http "$h" "$1")" "$(dapr_grpc "$h" "$1")"; do
        if port_busy "$p" && ! docker ps --format '{{.Names}}' | grep -qx "$(sidecar_name "$(f "$h" 6)" "$2" "$1")"; then
          die "sidecar port $p ($(f "$h" 1)) is already in use"
        fi
      done
    fi
  done < <(selected_hosts)
  return 0
}

start_host() {   # <row> <domain> <offset> <db>
  local row="$1" name dll port
  name="$(f "$row" 1)"; port="$(app_port "$(f "$row" 4)" "$3")"
  dll="$(target_dll "$(f "$row" 2)")"; [ -f "$dll" ] || die "$name not built: $dll (drop --no-build)"
  load_env "$row" "$2" "$3" "$4"
  # the backgrounded subshell exec's into dotnet, so $! is the host's real pid
  (cd "$ROOT/$(f "$row" 2)" && exec env "${ENVS[@]}" nohup dotnet "$dll") > "$DDIR/logs/$name.log" 2>&1 &
  echo $! > "$DDIR/pids/$name.pid"
  ok "$name  pid=$!  http://localhost:$port  log=$DDIR/logs/$name.log"
}

wait_healthy() {   # <offset>
  local h name port deadline=$(( $(date +%s) + HEALTH_TIMEOUT ))
  while IFS= read -r h; do
    name="$(f "$h" 1)"; port="$(app_port "$(f "$h" 4)" "$1")"
    until curl -fs -o /dev/null "http://localhost:$port/health"; do
      pid_alive "$(cat "$DDIR/pids/$name.pid")" || { tail -n 30 "$DDIR/logs/$name.log" >&2; die "$name exited — see $DDIR/logs/$name.log"; }
      [ "$(date +%s)" -ge "$deadline" ] && die "$name not healthy after ${HEALTH_TIMEOUT}s — see $DDIR/logs/$name.log"
      sleep 1
    done
    ok "$name healthy"
  done < <(selected_hosts)
}

stop_domain() {   # <domain> — hosts, then its own sidecars (offset domains only)
  local domain="$1" ddir p name pid i offset
  ddir="$(dom_dir "$domain")"; [ -d "$ddir" ] || { warn "unknown domain '$domain'"; return 0; }
  for p in "$ddir"/pids/*.pid; do
    [ -f "$p" ] || continue
    name="$(basename "${p%.pid}")"; pid="$(cat "$p")"
    if pid_alive "$pid"; then
      kill "$pid" 2>/dev/null || true
      i=0; while pid_alive "$pid" && [ $i -lt 20 ]; do sleep 0.5; i=$((i+1)); done
      pid_alive "$pid" && { warn "$name did not exit, killing"; kill -9 "$pid" 2>/dev/null || true; }
      ok "$domain/$name stopped"
    fi
    rm -f "$p"
  done
  offset="$(stored "$domain" offset)"
  if [ -n "$offset" ] && [ "$offset" != 0 ] && [ -f "$ddir/sidecars.json" ]; then
    docker compose -f "$ddir/sidecars.json" down >/dev/null 2>&1 && ok "$domain sidecars removed" || true
  fi
  write_record "$domain" stopped
}

# vNext CLI (`wf`) keeps its own list of domains with API url + database. Register this one so a
# later `wf domain use <domain> && wf sync` publishes to the right port. Never switch the active domain.
register_wf_domain() {   # <domain> <offset> <db>
  command -v wf >/dev/null 2>&1 || { warn "vNext CLI (wf) not installed — components must be published another way"; return 0; }
  local url="http://localhost:$(app_port 4201 "$2")"
  if wf domain list 2>/dev/null | grep -qE "^[▸ ]*\s*$1( |$)"; then
    ok "wf domain '$1' already registered — verify it points at $url: wf domain list"
  elif wf domain add "$1" --API_BASE_URL "$url" --DB_NAME "$3" >/dev/null 2>&1; then
    ok "wf domain '$1' registered → $url / $3   (activate: wf domain use $1)"
  else
    warn "could not register wf domain '$1' — run: wf domain add $1 --API_BASE_URL $url --DB_NAME $3"
  fi
}

# ---------- records (ai-docs) ---------------------------------------------------------------

write_record() {   # <domain> <running|stopped>
  local domain="$1" status="$2" ddir offset db h name port rows="" now rev branch
  ddir="$(dom_dir "$domain")"; offset="$(stored "$domain" offset)"; db="$(stored "$domain" db)"
  [ -n "$offset" ] || return 0
  mkdir -p "$RECORDS"
  now="$(date '+%Y-%m-%d %H:%M:%S %Z')"; rev="$(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || echo '-')"; branch="$(git -C "$ROOT" rev-parse --abbrev-ref HEAD 2>/dev/null || echo '-')"
  while IFS= read -r h; do
    name="$(f "$h" 1)"
    rows="$rows| $name | $(host_url "$h" "$offset") | $(app_id "$(f "$h" 8)" "$domain" "$offset") | $(dapr_http "$h" "$offset") / $(dapr_grpc "$h" "$offset") | $(sidecar_name "$(f "$h" 6)" "$domain" "$offset") | \`$ddir/logs/$name.log\` |
"
  done < <(selected_hosts; printf '%s\n' "$MIGRATOR")
  cat > "$RECORDS/$domain.md" <<MD
# Local environment — domain \`$domain\`

| | |
|---|---|
| Status | **$status** ($now) |
| Port offset | $offset |
| Database | \`$db\` on localhost:5432 (postgres/postgres) |
| Base URL | http://localhost:$(app_port 4201 "$offset") |
| Init (package publisher) | http://localhost:$(init_port "$offset") (\`$(init_name "$domain" "$offset")\`) |
| Runtime source | \`$branch\` @ \`$rev\` |
| Sidecar compose | $([ "$offset" = 0 ] && echo "\`etc/docker/docker-compose.yml\` (core owns the default sidecars)" || echo "\`$ddir/sidecars.json\` (project \`vnext-$domain\`)") |

## Hosts

| host | url | dapr app-id | dapr http / grpc | sidecar container | log |
|---|---|---|---|---|---|
$rows
## Reproduce

\`\`\`bash
cd etc/docker && ./run-docker.sh up $domain --offset $offset$([ "$db" != "$(DB_OVERRIDE= db_for_domain "$domain")" ] && echo " --db $db")
\`\`\`

## Load components (vNext CLI \`wf\`)

\`\`\`bash
cd <domain package repo, e.g. ../vnext-example>
wf domain add $domain --API_BASE_URL http://localhost:$(app_port 4201 "$offset") --DB_NAME $db   # once
wf domain use $domain && wf check && wf sync      # sync = add missing · update = changed · reset = force
\`\`\`

\`wf\` was registered for this domain by \`up\` (API $(printf 'http://localhost:%s' "$(app_port 4201 "$offset")"), DB \`$db\`). Check \`wf domain active\` before publishing — the CLI keeps one global active domain.

System flows (\`@burgan-tech/vnext-core-runtime\`) go through **this domain's** init service (container \`$(init_name "$domain" "$offset")\`, already aimed at :$(app_port 4201 "$offset")):
\`curl -X POST localhost:$(init_port "$offset")/api/package/runtime/publish -H 'content-type: application/json' -d '{"appDomain":"$domain"}'\`
The call is asynchronous — poll the returned \`statusUrl\` (or \`docker logs $(init_name "$domain" "$offset")\`) until the job is completed, then \`wf sync\`.

Integration tests against this environment: \`VNEXT_BASE_URL=http://localhost:$(app_port 4201 "$offset")\`.
Stop: \`./run-docker.sh down $domain\`.
MD
  # machine-readable summary of every known domain
  python3 - "$RECORDS/environments.json" "$domain" "$status" "$offset" "$db" "$(app_port 4201 "$offset")" "$now" "$rev" <<'PY'
import json, sys, os
path, domain, status, offset, db, port, now, rev = sys.argv[1:]
data = json.load(open(path)) if os.path.exists(path) else {}
data[domain] = {"status": status, "offset": int(offset), "database": db,
                "baseUrl": f"http://localhost:{port}", "updatedAt": now, "runtimeRev": rev}
json.dump(data, open(path, "w"), indent=2)
PY
  # index
  {
    echo "# Local environments"
    echo
    echo "Written by \`etc/docker/run-docker.sh\` on every \`up\` / \`down\`. One file per domain; \`environments.json\` is the machine-readable form."
    echo "Reproduce any of them with the command at the bottom of its page. This folder is git-ignored."
    echo
    echo "| domain | status | offset | base url | database | updated |"
    echo "|---|---|---|---|---|---|"
    python3 -c '
import json,sys
for d,v in sorted(json.load(open(sys.argv[1])).items()):
    print("| [%s](%s.md) | %s | %s | %s | `%s` | %s |" % (d, d, v["status"], v["offset"], v["baseUrl"], v["database"], v["updatedAt"]))' "$RECORDS/environments.json"
  } > "$RECORDS/README.md"
}

# ---------- commands ------------------------------------------------------------------------

print_plan() {   # <domain> <offset> <db>
  local h
  printf '\n\033[1mdomain=%s  offset=%s  database=%s\033[0m\n' "$1" "$2" "$3"
  printf '  %-14s %-24s %-32s %-13s %s\n' host url dapr-app-id dapr-http/grpc sidecar
  while IFS= read -r h; do
    printf '  %-14s %-24s %-32s %-13s %s\n' "$(f "$h" 1)" "$(host_url "$h" "$2")" \
      "$(app_id "$(f "$h" 8)" "$1" "$2")" "$(dapr_http "$h" "$2")/$(dapr_grpc "$h" "$2")" "$(sidecar_name "$(f "$h" 6)" "$1" "$2")"
  done < <(selected_hosts; printf '%s\n' "$MIGRATOR")
  printf '  %-14s %-24s %-32s %-13s %s\n' init "http://localhost:$(init_port "$2")" "(package publisher → :$(app_port 4201 "$2"))" "" "$(init_name "$1" "$2")"
  echo
}

cmd_plan() {
  need docker; need jq; need python3
  local domain offset db; domain="$(resolve_domain "${1:-}")"; offset="$(resolve_offset "$domain" "${OFFSET_ARG:-}")"; db="$(db_for_domain "$domain")"
  print_plan "$domain" "$offset" "$db"
  if [ "$offset" != 0 ]; then
    mkdir -p "$STATE/plans"
    write_sidecars_json "$domain" "$offset" "$STATE/plans/$domain.sidecars.json"
    docker compose -f "$STATE/plans/$domain.sidecars.json" config >/dev/null && ok "sidecar compose valid: $STATE/plans/$domain.sidecars.json"
  fi
  local h; while IFS= read -r h; do
    echo "--- env overrides: $(f "$h" 1)"; load_env "$h" "$domain" "$offset" "$db"
    printf '  %s\n' "${ENVS[@]}" | grep -E "APP_DOMAIN|ConnectionStrings|DAPR_APP_ID|DAPR_HTTP|DAPR_GRPC|ASPNETCORE_URLS|__AppId|BaseUrl" | sed 's/Password=[^;]*/Password=***/'
  done < <(selected_hosts)
}

cmd_up() {
  need docker; need dotnet; need jq; need python3; need curl; need lsof
  local domain offset db h
  domain="$(resolve_domain "${1:-}")"; offset="$(resolve_offset "$domain" "${OFFSET_ARG:-}")"; db="$(db_for_domain "$domain")"
  DDIR="$(dom_dir "$domain")"; mkdir -p "$DDIR/logs" "$DDIR/pids" "$STATE"
  print_plan "$domain" "$offset" "$db"
  if domain_running "$domain"; then log "stopping running $domain hosts"; stop_domain "$domain"; fi
  printf '%s' "$offset" > "$DDIR/offset"; printf '%s' "$db" > "$DDIR/db"
  check_ports_free "$offset" "$domain"
  ensure_infra "$domain" "$offset"
  [ "${NO_BUILD:-0}" = "1" ] || build_all
  [ "${SKIP_MIGRATE:-0}" = "1" ] || migrate "$domain" "$offset" "$db"
  log "starting hosts"
  while IFS= read -r h; do start_host "$h" "$domain" "$offset" "$db"; done < <(selected_hosts)
  log "waiting for /health (timeout ${HEALTH_TIMEOUT}s)"
  wait_healthy "$offset"
  printf '%s' "$domain" > "$STATE/last"
  register_wf_domain "$domain" "$offset" "$db"
  write_record "$domain" running
  printf '\n\033[1;32mready\033[0m  %s  →  http://localhost:%s   record: %s\n\n' "$domain" "$(app_port 4201 "$offset")" "$RECORDS/$domain.md"
}

cmd_down() {
  local d
  if [ "${1:-}" = "--all" ] || [ -z "${1:-}" ]; then
    for d in $(registered_domains); do stop_domain "$d"; done
  else
    stop_domain "$1"
  fi
  [ "${WITH_INFRA:-0}" = "1" ] && { log "stopping docker infra"; compose down; }
  return 0
}

cmd_status() {
  local d h name port pid state offset
  printf 'infra:  postgres %s\n' "$(compose ps --services --status running 2>/dev/null | grep -qx postgres && echo up || echo down)"
  for d in $(registered_domains); do
    offset="$(stored "$d" offset)"; [ -n "$offset" ] || continue
    printf '\n%s  offset=%s  db=%s  base=http://localhost:%s\n' "$d" "$offset" "$(stored "$d" db)" "$(app_port 4201 "$offset")"
    while IFS= read -r h; do
      name="$(f "$h" 1)"; port="$(app_port "$(f "$h" 4)" "$offset")"; pid="$(cat "$(dom_dir "$d")/pids/$name.pid" 2>/dev/null || true)"
      if pid_alive "$pid"; then
        state="$(curl -fs -o /dev/null "http://localhost:$port/health" 2>/dev/null && echo healthy || echo 'up, unhealthy')"
        printf '  %-14s pid=%-6s :%s  %s\n' "$name" "$pid" "$port" "$state"
      else
        printf '  %-14s stopped\n' "$name"
      fi
    done < <(selected_hosts)
  done
  [ -n "$(registered_domains)" ] || echo "no domains registered yet (run: ./run-docker.sh up <domain>)"
}

cmd_logs() {
  local domain; domain="$(resolve_domain "${1:-}")"
  if [ -n "${2:-}" ]; then tail -f "$(dom_dir "$domain")/logs/$2.log"; else tail -f "$(dom_dir "$domain")"/logs/*.log; fi
}

# the original three modes: attached compose stacks. dev/stage take a domain like `up` does;
# the compose files read APP_DOMAIN / VNEXT_DB via ${VAR:-default} interpolation. No offsets here.
cmd_stack() {
  need docker
  docker network create bbt-development >/dev/null 2>&1 || true
  local mode="$1" domain db
  if [ "$mode" = dev ] || [ "$mode" = stage ]; then
    domain="$(resolve_domain "${2:-}")"; db="$(db_for_domain "$domain")"
    mkdir -p "$STATE"; printf '%s' "$domain" > "$STATE/last"
    export APP_DOMAIN="$domain" VNEXT_DB="$db"
    log "$mode mode  domain=$domain  database=$db"
  fi
  case "$mode" in
    stage) exec docker compose -f "$HERE/docker-compose.stage.yml" up --build ;;
    dev)   exec docker compose -f "$HERE/docker-compose.dev.yml" up --build ;;
    *)     log "infrastructure only"; exec docker compose -f "$COMPOSE_FILE" up --build ;;
  esac
}

usage() { sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }

# ---------- arg parsing ---------------------------------------------------------------------

CMD="${1:-}"; [ $# -gt 0 ] && shift
POSITIONAL=()
while [ $# -gt 0 ]; do
  case "$1" in
    --no-build)     NO_BUILD=1 ;;
    --skip-migrate) SKIP_MIGRATE=1 ;;
    --infra)        WITH_INFRA=1 ;;
    --all)          POSITIONAL+=("--all") ;;
    --offset)       shift; OFFSET_ARG="${1:-}"; [ -n "$OFFSET_ARG" ] || die "--offset needs a value" ;;
    --db)           shift; DB_OVERRIDE="${1:-}"; [ -n "$DB_OVERRIDE" ] || die "--db needs a value" ;;
    -h|--help)      usage 0 ;;
    -*)             die "unknown flag: $1" ;;
    *)              POSITIONAL+=("$1") ;;
  esac
  shift
done
set -- ${POSITIONAL[@]+"${POSITIONAL[@]}"}

case "$CMD" in
  ""|inf|infra|dev|stage) cmd_stack "$CMD" "${1:-}" ;;
  up)       cmd_up "${1:-}" ;;
  plan)     cmd_plan "${1:-}" ;;
  switch)   cmd_down --all; cmd_up "${1:-}" ;;
  restart)  d="$(resolve_domain "${1:-}")"; OFFSET_ARG="$(stored "$d" offset)"; cmd_up "$d" ;;
  down)     cmd_down "${1:-}" ;;
  status)   cmd_status ;;
  record)   d="$(resolve_domain "${1:-}")"; [ -f "$(dom_dir "$d")/offset" ] || die "unknown domain '$d'"; write_record "$d" "$(domain_running "$d" && echo running || echo stopped)"; ok "record written: $RECORDS/$d.md" ;;
  logs)     cmd_logs "${1:-}" "${2:-}" ;;
  domains)  for d in $(registered_domains); do printf '%s\toffset=%s\t%s\n' "$d" "$(stored "$d" offset)" "$(domain_running "$d" && echo running || echo stopped)"; done ;;
  -h|--help|help) usage 0 ;;
  *)        die "unknown command: $CMD (try --help)" ;;
esac
