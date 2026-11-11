#!/usr/bin/env bash
# The Developer agent's only shell command (docs/governance/ai-engineering-pipeline.md §13).
# Runs `dotnet build` or `dotnet test` on the working tree with:
#   - no network (own network namespace, loopback only; packages were restored before the agent ran)
#   - no environment (env -i: CURSOR_API_KEY and every runner variable are gone)
#   - its own PID namespace (the agent's /proc/<pid>/environ is not visible) and private mounts
#     that hide the runner temp dir (staged tooling, agent input) and the CLI config, make the home
#     directory and .git read-only (the workspace stays writable) and give a private /tmp
#   - the runner's uid with no_new_privs, no capabilities and no supplementary groups (no sudo, no
#     docker), so build output lands in the tree as usual and nothing inside can undo the above
# The workflow stages this file outside the working tree and fills in the @…@ values, so nothing the
# caller sets (HOME, PATH, the working directory) changes what it runs; the agent cannot edit it (its
# Write allow-list covers source folders only).
#   ai-dotnet build                      the whole solution, Release
#   ai-dotnet test test/<Project>.Tests [--filter <expr>]   nothing else is accepted
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
# Arguments are an allow-list, not a filter: `build` takes none; `test` takes one test project under
# test/ and at most one `--filter <expression>`. Nothing else reaches dotnet (-p:, --settings, -r, …).
usage='usage: ai-dotnet build | ai-dotnet test test/<Project>.Tests [--filter <expression>]'
if [ "$cmd" = build ]; then
  [ $# -eq 0 ] || { echo "ai-dotnet: build takes no arguments. $usage" >&2; exit 2; }
  set -- build ./BBT.Workflow.slnx --configuration Release --no-restore -nodeReuse:false
else
  project="${1:-}"
  shift || true
  case "$project" in
    test/BBT.Workflow.*.Tests | test/BBT.Workflow.*.Tests/) project="${project%/}" ;;
    *) echo "ai-dotnet: test needs a test project, e.g. test/BBT.Workflow.Domain.Tests. $usage" >&2; exit 2 ;;
  esac
  case "${project#test/BBT.Workflow.}" in
    *[!A-Za-z0-9.]* | *..*) echo "ai-dotnet: not a test project: $project" >&2; exit 2 ;;
  esac
  [ -d "$WORKSPACE/$project" ] || { echo "ai-dotnet: no such test project: $project" >&2; exit 2; }
  if [ $# -gt 0 ]; then
    [ "$1" = --filter ] && [ $# -eq 2 ] || { echo "ai-dotnet: only --filter <expression> may follow the project. $usage" >&2; exit 2; }
    case "$2" in
      '' | -*) echo "ai-dotnet: empty filter or one that starts with '-'" >&2; exit 2 ;;
      *[!A-Za-z0-9_.=~!\&\|\(\)\ ,:-]*) echo "ai-dotnet: the filter may use letters, digits and _ . = ~ ! & | ( ) , : - and spaces only" >&2; exit 2 ;;
    esac
    set -- test "$project" --filter "$2" --configuration Release --no-restore -nodeReuse:false
  else
    set -- test "$project" --configuration Release --no-restore -nodeReuse:false
  fi
fi
[ "$PWD" = "$WORKSPACE" ] || cd "$WORKSPACE"

uid="$(id -u)"
gid="$(id -g)"
exec sudo -n /usr/bin/unshare --net --pid --fork --kill-child --mount --propagation private -- \
  /bin/sh -c '
    set -e
    uid=$1 gid=$2 home=$3 droot=$4 rtemp=$5 limit=$6 ws=$7
    shift 7
    /usr/sbin/ip link set lo up
    mount -t proc proc /proc
    # The home directory (actions, packages, tool caches, the parents of the workspace) read-only; the
    # workspace read-write again, its .git read-only; a private /tmp. Code run by `dotnet test` cannot
    # plant anything a later step of the job executes. The binds come first: a bind drops the mounts
    # below it, so the masks are laid on top afterwards.
    mount --bind "$home" "$home" && mount -o remount,bind,ro "$home"
    mount --bind "$ws" "$ws" && mount -o remount,bind,rw "$ws"
    mount --bind "$ws/.git" "$ws/.git" && mount -o remount,bind,ro "$ws/.git"
    mount -t tmpfs tmpfs /tmp
    mount -t tmpfs tmpfs "$rtemp"
    for d in "$home/.cursor" "$home/.config"; do if [ -d "$d" ]; then mount -t tmpfs tmpfs "$d"; fi; done
    if [ -S /run/docker.sock ]; then mount --bind /dev/null /run/docker.sock; fi
    # The dotnet CLI runs its first-use setup against the fresh /tmp homes below. the NuGet migration
    # step takes a named mutex unless its marker file exists, and that mutex cannot be created in
    # this sandbox on the hosted runner ("cannot open the device or file: NuGet-Migrations"). The
    # marker (the same file NuGet writes once migrated) skips the step; the other first-use steps
    # are switched off through the environment.
    mkdir -p /tmp/xdg-data/NuGet/Migrations /tmp/dotnet-home
    : > /tmp/xdg-data/NuGet/Migrations/1
    chown -R "$uid:$gid" /tmp/xdg-data /tmp/dotnet-home
    # no_new_privs + no capabilities + no supplementary groups: setuid sudo cannot elevate, so nothing
    # inside can undo these mounts; without the docker group the socket is out of reach as well.
    exec /usr/bin/setpriv --no-new-privs --inh-caps=-all --bounding-set=-all --clear-groups \
      --reuid="$uid" --regid="$gid" -- \
      /usr/bin/timeout "$limit" /usr/bin/env -i \
        HOME="$home" PATH="$droot:/usr/bin:/bin" DOTNET_ROOT="$droot" NUGET_PACKAGES="$home/.nuget/packages" \
        DOTNET_CLI_HOME=/tmp/dotnet-home TMPDIR=/tmp XDG_DATA_HOME=/tmp/xdg-data XDG_CACHE_HOME=/tmp/xdg-cache XDG_CONFIG_HOME=/tmp/xdg-config DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_GENERATE_ASPNET_CERTIFICATE=false DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false MSBUILDDISABLENODEREUSE=1 \
        "$droot/dotnet" "$@"
  ' ai-dotnet "$uid" "$gid" "$HOME_DIR" "$DOTNET_ROOT_DIR" "$RUNNER_TEMP_DIR" "$TIMEOUT_SECONDS" "$WORKSPACE" "$@"
