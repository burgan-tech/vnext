# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## First-Time Setup

On macOS/Linux, run the setup script before building (required for PostSharp compatibility with .NET 10):

```bash
./scripts/setup-netstandard-ref.sh
```

## Build & Run

```bash
# Restore and build entire solution
dotnet restore
dotnet build

# Run with full infrastructure (recommended for development)
cd etc/docker && ./run-docker.sh          # Infrastructure only (default)
cd etc/docker && ./run-docker.sh dev      # Dev mode with debugger
cd etc/docker && ./run-docker.sh stage    # Staging mode

# Run API hosts locally (requires infrastructure running)
dotnet run --project orchestration/BBT.Workflow.Orchestration.HttpApi.Host
dotnet run --project execution/BBT.Workflow.Execution.HttpApi.Host
```

**Ports**: Orchestration → 4201, Execution → 4202

## Testing

```bash
dotnet test                               # Run all tests
dotnet test test/BBT.Workflow.Application.Tests   # Single project
dotnet test --filter "FullyQualifiedName~MyTest"  # Single test
```

Test projects: `Domain.Tests`, `Application.Tests`, `Infrastructure.Tests`, `TestBase` (shared utilities).

## Architecture Overview

This is a **distributed workflow orchestration engine** built on .NET 10, Clean Architecture, DDD, and the Aether SDK.

### Two API Hosts (microservices boundary)

| Host | Project | Purpose |
|------|---------|---------|
| Orchestration | `orchestration/BBT.Workflow.Orchestration.HttpApi.Host` | Public-facing: manages workflow definitions, instances, transitions |
| Execution | `execution/BBT.Workflow.Execution.HttpApi.Host` | Internal: executes task invokers for a specific transition |

The two services communicate via **Dapr service invocation**. Orchestration calls Execution for task processing; Execution calls back to Orchestration to report outcomes.

### Layer Responsibilities (`src/`)

| Project | Role |
|---------|------|
| `BBT.Workflow.Domain` | Aggregates, entities, domain events, value objects, business rules. No infrastructure dependencies. |
| `BBT.Workflow.Application` | Application services, DTOs, pipeline logic, use cases. Depends on Domain only. |
| `BBT.Workflow.Infrastructure` | EF Core repositories, external integrations, event hooks. Implements Domain and Application interfaces. |
| `BBT.Workflow.Events.Contracts` | Shared distributed event definitions (CloudEvents). |
| `BBT.Workflow.Execution` / `Execution.Abstractions` | Task invoker bindings and contracts for the Execution service. |
| `BBT.Workflow.Tasks.Abstractions` | Task interface contracts used by both Orchestration and Execution. |
| `BBT.Workflow.HttpApi.Shared` | Shared middleware, telemetry enrichment, utilities for both API hosts. |

### Workers (`workers/`)

| Worker | Purpose |
|--------|---------|
| `BBT.Workflow.Workers.Inbox` | Consumes domain events from the distributed event bus (async handlers). |
| `BBT.Workflow.Workers.Outbox` | Publishes outbox events to the event bus (transactional outbox pattern). |
| `BBT.Workflow.DbMigrator` | Runs EF Core schema migrations at deploy time. |

### Transition Pipeline

Workflow execution flows through a deterministic **transition pipeline**. When a transition is triggered:
1. Orchestration validates and persists state
2. Orchestration invokes Execution via Dapr
3. Execution runs task invokers (C# scripts, HTTP calls, etc.)
4. Execution reports back; Orchestration applies the result and may auto-trigger the next transition

Synchronous mode (`sync=true`) waits for the entire pipeline to complete and returns instance data in the response.

### Key Infrastructure

- **Database**: PostgreSQL with multi-schema support (one schema per tenant/flow)
- **Cache**: Redis via `IDistributedCache`
- **Messaging**: Dapr pub/sub + transactional Inbox/Outbox workers
- **Scripting**: `modules/BBT.Workflow.Modules.Scripting` — Roslyn-based C# script engine
- **Observability**: OpenTelemetry (Jaeger in Docker), structured logging via `WorkflowLogs.cs`

### Multi-Schema Tenancy

Each workflow "flow" has its own PostgreSQL schema. Schema resolution uses `ICurrentSchema` populated from HTTP headers, routes, or query string. Always wrap infrastructure operations with `currentSchema.Use(flow)`.

### Domain Events (dual-processing pattern)

Every domain event requires **two** handlers (see `.claude/CLAUDE.md` for the full checklist):
- **Event Hook** (`IEventPublishHook<T>` in `*.Infrastructure/*/Events/`) — synchronous, local, within the same UoW
- **Event Handler** (`IEventHandler<T>` in `workers/BBT.Workflow.Workers.Inbox/Handlers/`) — asynchronous, distributed, fault-tolerant

### Context7 MCP Sources

For domain/platform knowledge beyond what's in code:
- vNext domain: `burgan-tech/vnext-runtime` (tag `vnext-runtime`)
- Aether SDK: `burgan-tech/aether` (tag `aether`)
- Examples: tag `vnext-example`

Detailed docs live in `/docs` (implementation) and `/ai-docs` (AI-generated).
