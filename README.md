# vNext Platform

The vNext workflow runtime is a .NET-based orchestration system built with Clean Architecture
and DDD. It ships three API hosts and three workers:

- **Orchestration API**: client-facing workflow/instance operations (port `4201`)
- **Execution API**: internal task execution for a transition (port `4202`)
- **Monitor API**: read-only monitoring endpoints for dashboards (port `4203`)
- **Workers**: Inbox (event consumption), Outbox (transactional outbox publishing), DbMigrator (EF Core schema migrations)

## Prerequisites

- .NET 10 SDK (10.0.101 or later)
- Docker (for the local infrastructure stack and container builds)

### First-Time Setup (.NET 10)

If you're building with .NET 10 for the first time, run the setup script once. It installs the
`NETStandard.Library.Ref` targeting pack required by PostSharp.

**macOS/Linux:**
```bash
./scripts/setup-netstandard-ref.sh
```

**Windows (or macOS with PowerShell installed):**
```powershell
.\scripts\setup-netstandard-ref.ps1
```

If PostSharp still reports a missing targeting pack, see [Troubleshooting](#troubleshooting).

## Quick Start

```bash
dotnet restore
dotnet build
```

`etc/docker/run-docker.sh` is the single entry point. Docker stacks:

```bash
cd etc/docker
./run-docker.sh                   # infrastructure only (default)
./run-docker.sh dev [domain]      # dev mode: apps built into containers, with debugger
./run-docker.sh stage [domain]    # staging mode: release images
```

`dev` and `stage` ask for the domain when none is given (default: last used, else `core`) and pass it
to the containers as `APP_DOMAIN` / `VNEXT_DB` through compose interpolation, so the `.env.*` files stay
untouched.

Local development, one or more domains side by side (infra in docker, runtime as locally built binaries):

```bash
./run-docker.sh up                     # asks for the domain (default: last used, else core), then
                                       # infra + sidecars → DbMigrator → 4 hosts, waits for /health
./run-docker.sh up sales --offset 10   # second domain next to core: ports 4211/4212/4511/4411, own sidecars
./run-docker.sh plan hr --offset 20    # print ports, app-ids, env overrides; start nothing
./run-docker.sh status | domains | logs sales orchestration | down sales | down --all [--infra]
```

Port offsets follow [vnext-runtime](https://github.com/burgan-tech/vnext-runtime): `core` is offset 0 and
keeps the sidecars from `docker-compose.yml`; any other domain gets `base + offset` app ports, its own
`<service>-<domain>` sidecar containers and `vnext-<domain>-…` Dapr app-ids. Offsets that would collide
with core or another registered domain are refused (`--help` has the table). Flags: `--monitor`,
`--no-build`, `--skip-migrate`, `--db <name>`, `--offset N`.

Each host receives its `http` launch profile's environment with `APP_DOMAIN`, the connection string, the
Dapr ports/app-ids and the cross-host references overridden per process, so no tracked file changes.
Every `up` also registers the domain in the vNext CLI (`wf domain add`, right port and database; activate with
`wf domain use <domain>` before `wf sync`) and starts a per-domain `init` publisher on `3005+offset`.
It writes a record of the environment (ports, app-ids, database, logs, reproduce command) to
`ai-docs/local-environments/<domain>.md` — git-ignored, meant for the next session or the next agent.
The script refuses to start when the docker infra belongs to another compose file (e.g. a cross-domain
lab), because the sidecars would land on the wrong network. The manual equivalent of `up core`:

Run the apps against the infrastructure by hand (each in its own terminal; the
`Properties/launchSettings.json` profiles carry the `APP_DOMAIN` / `DAPR_*` / `OTEL_*` environment):

```bash
dotnet run --project workers/BBT.Workflow.DbMigrator          # once, when migrations are pending
dotnet run --project orchestration/BBT.Workflow.Orchestration.HttpApi.Host
dotnet run --project execution/BBT.Workflow.Execution.HttpApi.Host
dotnet run --project workers/BBT.Workflow.Workers.Inbox
dotnet run --project workers/BBT.Workflow.Workers.Outbox
dotnet run --project monitoring/BBT.Workflow.Monitor.HttpApi.Host   # optional
```

Tests:

```bash
dotnet test                                        # everything
dotnet test test/BBT.Workflow.Application.Tests    # one project
dotnet test --filter "FullyQualifiedName~MyTest"   # one test
```

## Repository Layout

- `orchestration/`: Orchestration API host (public-facing)
- `execution/`: Execution API host (internal; task invokers)
- `monitoring/`: Monitor API host and its application layer (read-only)
- `workers/`: Inbox, Outbox and DbMigrator workers
- `src/`: Domain, Application, Infrastructure, Events.Contracts, Execution (+ Abstractions), Tasks.Abstractions, HttpApi.Shared
- `modules/`: Roslyn-based C# scripting module
- `test/`: unit/integration test projects, shared `TestBase`, benchmarks
- `tools/`: `vnext-runtime` MCP server (agents read components, live runtime data and `vnext-meta` over one endpoint)
- `init/`: Node.js init service that downloads component npm packages and publishes them to the runtime
- `samples/`: sample projects (e.g. custom script helpers demo)
- `vnext-meta/`: `@burgan-tech/vnext-meta` npm package — machine-readable runtime metadata (features, deprecations, migrations, known issues, component registry)
- `etc/`: Docker Compose files and per-host Dapr component configs
- `scripts/`: setup helpers
- `docs/`: developer documentation (`docs/agent-onboarding.md` for coding agents). `ai-docs/` is gitignored local scratch, not committed.

## Docs

- [docs/README.md](docs/README.md) — documentation index and reading path
- [Agent onboarding](docs/agent-onboarding.md) — source-of-truth order for coding agents
- [Workflow Execution Pipeline](docs/architecture/workflow-execution-pipeline.md) — ordered steps, admission, inline auto-chain and post-commit boundaries
- [Subflow Execution](docs/architecture/subflow-execution.md) — child start/forward/retry, `S`/`P` semantics and terminal resume
- [AGENTS.md](AGENTS.md) — single bootstrap for coding agents (architecture, domain concepts, AI guidance layout); `CLAUDE.md` imports it
- [.claude/rules/](.claude/rules/) — always-on coding standards and the workflow developer reference; `.cursor/rules/` holds `@` pointers to the same files
- [vnext-meta/README.md](vnext-meta/README.md) — the runtime metadata package

### Working with AI coding agents

Guidance is tool-neutral and lives in one place; every agent (Claude Code, Cursor, Codex, Copilot) reads the same files:

- [AGENTS.md](AGENTS.md) is the bootstrap, [.claude/rules/](.claude/rules/) the always-on rules, [.claude/skills/](.claude/skills/) the on-demand skills. Cursor reads the skills folder directly and reaches the rules through `@` pointers in `.cursor/rules/`; nothing is copied.
- Non-trivial decisions (architecture, cross-service, data model, security, performance) go through the **Agent Council** before any code is written. Run:

  ```
  /agent-council <the decision to make>
  ```

  It also fires on phrases like "council", "karar verelim", "eklemeli miyim", "mimari karar". The council never edits code; it only produces the decision record. Session artifacts are written locally under `ai-docs/agent-council/sessions/` (git-ignored) and indexed in the committed [decision log](docs/agent-council/sessions/README.md), which is the team's decision history — check it before opening a new session. Process, roles and templates: [docs/agent-council/README.md](docs/agent-council/README.md).

## Health Endpoints

Every host maps `/health`, `/ready` and `/live`:

- Orchestration: `http://localhost:4201/health`
- Execution: `http://localhost:4202/health`
- Monitor: `http://localhost:4203/health` (plus `http://localhost:4203/monitor/health/detail`)
- Outbox worker: `http://localhost:4401/health`
- Inbox worker: `http://localhost:4501/health`

---

## Troubleshooting

### PostSharp Targeting Pack Error

If you encounter the following error during compilation:

```
POSTSHARP : error : error: Unhandled exception (PostSharp.Compiler.Hosting.CommandLine.dll 2025.1.10 release | .NET 9.0.11 (Arm64)): Requested targeting pack NETStandard.Library.Ref, version=2.1.0 is not installed in
```

**Solution:**

1. First, clean the `bin` and `obj` folders. You can use one of the following methods:

   **Option A - Using shell command (Linux/macOS):**
   ```bash
   find . -type d \( -name bin -o -name obj \) -exec rm -rf {} + 2>/dev/null
   ```

   **Option B - Using PowerShell script (Windows, or macOS with [PowerShell installed](https://docs.microsoft.com/en-us/powershell/scripting/install/installing-powershell-core-on-macos)):**
   ```powershell
   ./delete-bin-obj.ps1
   ```

2. Then, rebuild the project:
   ```bash
   dotnet clean
   dotnet restore
   dotnet build
   ```

This issue typically occurs when there are stale build artifacts that conflict with PostSharp's targeting pack resolution.
