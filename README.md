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

Start the infrastructure (PostgreSQL, Redis, Dapr, observability) with Docker:

```bash
cd etc/docker
./run-docker.sh          # infrastructure only (default)
./run-docker.sh dev      # dev mode with debugger
./run-docker.sh stage    # staging mode
```

Run the apps locally against that infrastructure (each in its own terminal; the
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
- `docs/`: developer documentation; `ai-docs/`: AI-generated technical notes

## Docs

- [docs/README.md](docs/README.md) — documentation index and reading path
- [Workflow Execution Pipeline](docs/architecture/workflow-execution-pipeline.md) — ordered steps, admission, inline auto-chain and post-commit boundaries
- [Subflow Execution](docs/architecture/subflow-execution.md) — child start/forward/retry, `S`/`P` semantics and terminal resume
- [CLAUDE.md](CLAUDE.md) / [AGENTS.md](AGENTS.md) — architecture overview and domain concepts for coding agents (same content)
- [.claude/rules/](.claude/rules/) — always-on coding standards and the workflow developer reference
- [vnext-meta/README.md](vnext-meta/README.md) — the runtime metadata package

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
