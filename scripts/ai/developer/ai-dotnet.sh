#!/usr/bin/env bash
# The Developer agent's only shell command (docs/governance/ai-engineering-pipeline.md §13).
# Runs `dotnet build` or `dotnet test` on the working tree with:
#   - no network (own network namespace, loopback only; packages were restored before the agent ran)
#   - no environment (env -i: CURSOR_API_KEY and every runner variable are gone)
#   - its own PID namespace (the agent's /proc/<pid>/environ is not visible) and private mounts
#     that hide the runner temp dir (staged tooling, agent input) and the CLI config and make .git
#     read-only
#   - the runner's uid with no_new_privs, no capabilities and no supplementary groups (no sudo, no
#     docker), so build output lands in the tree as usual and nothing inside can undo the above
# The workflow stages this file outside the working tree and fills in the @…@ values, so nothing the
# caller sets (HOME, PATH, the working directory) changes what it runs; the agent cannot edit it (its
# Write allow-list covers source folders only).
#   ai-dotnet build                      the whole solution, Release
#   ai-dotnet test <test project> [--filter <expr>]
set -euo pipefail

DOTNET_ROOT_DIR='@DOTNET_ROOT@'
RUNNER_TEMP_DIR='@RUNNER_TEMP@'
WORKSPACE='@WORKSPACE@'
HOME_DIR='@HOME@'
TIMEOUT_SECONDS=1800

cmd="${1:-}"
shift || true
case "$cmd" in
  build | test) ;;
  *) echo "ai-dotnet: only 'build' and 'test' are allowed" >&2; exit 2 ;;
esac
for a in "$@"; do
  case "$a" in
    /* | *..*) echo "ai-dotnet: arguments must be relative to the repository (got: $a)" >&2; exit 2 ;;
  esac
done
[ "$PWD" = "$WORKSPACE" ] || cd "$WORKSPACE"

if [ "$cmd" = build ]; then
  set -- build ./BBT.Workflow.slnx --configuration Release --no-restore -nodeReuse:false "$@"
else
  [ $# -ge 1 ] || { echo "ai-dotnet: test needs a test project, e.g. test/BBT.Workflow.Domain.Tests" >&2; exit 2; }
  set -- test "$@" --configuration Release --no-restore -nodeReuse:false
fi

uid="$(id -u)"
gid="$(id -g)"
exec sudo -n /usr/bin/unshare --net --pid --fork --kill-child --mount --propagation private -- \
  /bin/sh -c '
    set -e
    uid=$1 gid=$2 home=$3 droot=$4 rtemp=$5 limit=$6 ws=$7
    shift 7
    /usr/sbin/ip link set lo up
    mount -t proc proc /proc
    mount -t tmpfs tmpfs "$rtemp"
    # .git read-only: agent-written code cannot plant hooks, fsmonitor or config for later git calls.
    mount --bind "$ws/.git" "$ws/.git" && mount -o remount,bind,ro "$ws/.git"
    for d in "$home/.cursor" "$home/.config"; do if [ -d "$d" ]; then mount -t tmpfs tmpfs "$d"; fi; done
    if [ -S /run/docker.sock ]; then mount --bind /dev/null /run/docker.sock; fi
    # no_new_privs + no capabilities + no supplementary groups: setuid sudo cannot elevate, so nothing
    # inside can undo these mounts; without the docker group the socket is out of reach as well.
    exec /usr/bin/setpriv --no-new-privs --inh-caps=-all --bounding-set=-all --clear-groups \
      --reuid="$uid" --regid="$gid" -- \
      /usr/bin/timeout "$limit" /usr/bin/env -i \
        HOME="$home" PATH="$droot:/usr/bin:/bin" DOTNET_ROOT="$droot" NUGET_PACKAGES="$home/.nuget/packages" \
        DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 MSBUILDDISABLENODEREUSE=1 \
        "$droot/dotnet" "$@"
  ' ai-dotnet "$uid" "$gid" "$HOME_DIR" "$DOTNET_ROOT_DIR" "$RUNNER_TEMP_DIR" "$TIMEOUT_SECONDS" "$WORKSPACE" "$@"
