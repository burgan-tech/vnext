#!/usr/bin/env bash
# SessionStart hook — reports which MCP backends are unreachable.
#
# The MCP servers in .mcp.json are started by Claude Code itself; this only checks
# that the services they talk to are up. It deliberately does NOT start anything:
# the docker infra may be owned by another compose file (see CLAUDE.local.md).
# Anything printed on stdout is added to the session context.

set -u

check() { # name host port hint
  (exec 3<>"/dev/tcp/$2/$3") 2>/dev/null && { exec 3>&-; return 0; }
  printf '  - %s (%s:%s) is down — %s\n' "$1" "$2" "$3" "$4"
}

down=$(
  check "postgres"      localhost 5432 "run-docker.sh up <domain>"
  check "redis"         localhost 6379 "run-docker.sh up <domain>"
  check "elasticsearch" localhost 9200 "full docker profile (APM traces)"
  check "openobserve"   localhost 5080 "full docker profile (logs)"
)

[ -n "$down" ] && printf 'MCP backends unreachable:\n%s\nStart with: cd etc/docker && ./run-docker.sh status\n' "$down"
exit 0
