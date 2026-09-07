# AGENTS.md

This is the single session-bootstrap file for every coding agent working in this repository (Codex, Cursor, Copilot, Gemini CLI read it directly; Claude Code imports it from `CLAUDE.md`). Tool-specific wiring lives in `CLAUDE.md` (Claude skills, local overrides) and in three pointer files under `.cursor/rules/` — see [AI guidance layout](#ai-guidance-layout) at the end of this file.

## Project Rules (always apply)

These rules are authoritative for all work in this repo. Read them before writing code:

- [Agent onboarding](docs/agent-onboarding.md) — source-of-truth order, where-is-X, known pitfalls. When this file disagrees with code, trust `LifecycleOrder.cs` / `PipelineExecutionProfile.cs`.
- [.NET / Aether / vNext coding standards](.claude/rules/dotnet-coding-standards.md) — style, naming, Aether SDK usage, outbox event delivery, logging via `WorkflowLogs.cs`, Result pattern, multi-schema rules.
- [vNext workflow developer reference](.claude/rules/vnext-workflow-developer.md) — pipeline step order, profiles, subflow lifecycle, error boundary, long-polling, instance data, `vnext-meta`.
- [Agent Council plan mode](.claude/rules/agent-council-plan-mode.md) — non-trivial decisions must produce an evidence-backed plan before implementation.

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
dotnet run --project monitoring/BBT.Workflow.Monitor.HttpApi.Host
```

**Ports**: Orchestration → 4201, Execution → 4202, Monitor → 4203

## Testing

```bash
dotnet test                               # Run all tests
dotnet test test/BBT.Workflow.Application.Tests   # Single project
dotnet test --filter "FullyQualifiedName~MyTest"  # Single test
```

Test projects: `Domain.Tests`, `Application.Tests`, `Infrastructure.Tests`, `TestBase` (shared utilities).

## Architecture Overview

This is a **distributed workflow orchestration engine** built on .NET 10, Clean Architecture, DDD, and the Aether SDK.

### API Hosts

| Host | Project | Purpose |
|------|---------|---------|
| Orchestration | `orchestration/BBT.Workflow.Orchestration.HttpApi.Host` | Public-facing: manages workflow definitions, instances, transitions |
| Execution | `execution/BBT.Workflow.Execution.HttpApi.Host` | Internal: executes task invokers for a specific transition |
| Monitor | `monitoring/BBT.Workflow.Monitor.HttpApi.Host` | Read-only operational queries for monitoring clients |

Orchestration and Execution communicate through **Dapr service invocation**. Monitor reads the
runtime's operational data without owning transition execution.

### Layer Responsibilities (`src/`)

| Project | Role |
|---------|------|
| `BBT.Workflow.Domain` | Aggregates, entities, domain events, value objects, business rules. No infrastructure dependencies. |
| `BBT.Workflow.Application` | Application services, DTOs, pipeline logic, use cases. Depends on Domain only. |
| `BBT.Workflow.Infrastructure` | EF Core repositories, external integrations and routed gateways. Implements Domain and Application interfaces. |
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

### Key Infrastructure

- **Database**: PostgreSQL with multi-schema support (one schema per tenant/flow)
- **Cache**: Redis via `IDistributedCache`
- **Messaging**: Dapr pub/sub + transactional Inbox/Outbox workers
- **Scripting**: `modules/BBT.Workflow.Modules.Scripting` — Roslyn-based C# script engine
- **Observability**: OpenTelemetry via Aether → otel-collector → Elastic APM (Kibana) + OpenObserve, structured logging via `WorkflowLogs.cs`

### Multi-Schema Tenancy

Each workflow "flow" has its own PostgreSQL schema. Schema resolution uses `ICurrentSchema` populated from HTTP headers, routes, or query string. Always wrap infrastructure operations with `currentSchema.Use(flow)`.

### Domain Events

The EventHook infrastructure has been deleted. Every distributed event publishes plainly through
the transactional outbox and requires:
- **Contract** in `*.Events.Contracts/*/Events/` with `[EventName]`
- **Event Handler** (`IEventHandler<T>` in `workers/BBT.Workflow.Workers.Inbox/Handlers/`) — asynchronous, distributed, fault-tolerant
- **WorkflowLogs** entries (`BBT.Workflow.Domain/Logging/WorkflowLogs.cs`)

The three subflow terminal events (`InstanceSubCompletedEvent`, `InstanceSubFaultedEvent`,
`InstanceSubCanceledEvent`) additionally implement `ISubflowTerminalEvent`: post-commit, `SubflowTerminalRelay`
relays them as an immediate command via `IInstanceCommandGateway`, and their Inbox handler is a
durable backup deduplicated by `ISubItemTerminalGuard` — the only event category with a second
delivery path by design. See `docs/runtime/event-publish-modes.md`.

---

## Domain Concepts

### Transition Pipeline

Transitions execute through a deterministic pipeline of ordered steps (`LifecycleOrder`); each step
returns `Result<StepOutcome>`. The **ordered step table, `StepOutcome` values, `PipelineExecutionProfile`
exclusions, the `updateData`-only self-target composition and `TransitionExecutionContext` reuse rules
live in one place**: [vNext workflow developer reference](.claude/rules/vnext-workflow-developer.md).
The narrative version is [Workflow Execution Pipeline](docs/architecture/workflow-execution-pipeline.md);
the code is `src/BBT.Workflow.Domain/Execution/Transitions/Pipeline/LifecycleOrder.cs` and
`PipelineExecutionProfile.cs`. When they disagree, the code wins. Do not copy the step table into
this file again — every step change then has to touch every copy.

### Status / State / Type Semantics

**Instance Status**: `Busy (B)` pipeline executing, `Active (A)` waiting, `Passive (P)` deactivated, `Completed (C)` finished, `Faulted (F)` terminal error.

**State Types**: `Initial = 1`, `Intermediate = 2`, `Finish = 3`, `SubFlow = 4`, `Wizard = 5`.

**State Sub Types**: `None = 0`, `Success = 1`, `Error = 2`, `Terminated = 3`, `Suspended = 4`, `Busy = 5`, `Human = 6`, `Cancelled = 7`, `Timeout = 8`.

**Trigger Types**: `Manual = 0`, `Automatic = 1`, `Scheduled = 2`, `Event = 3`.

### Sync vs Async Execution

- `sync=true`: Request blocks until pipeline completes; response includes full instance data. Use for deterministic short-lived processes and backend-to-backend integration.
- `sync=false` (default): Request accepted immediately with `{ id, status }`. Client polls via State function for completion. Use for human tasks, external API calls, and mobile/web clients.
- Automatic continuations never create per-hop Scheduler jobs. They run inline and are awaited by the request or by the initial async transition job.
- Runtime-generated subflow start, active-child forward and descended child retry calls always use `sync=true`, independent of the parent caller mode and `S`/`P` definition type. This awaits the child's current activation to a rest point, not its future human/event lifetime.

### Long-Polling / State Function

- Conditional GET with ETag: `GET /functions/state` → `200` (changed) | `304` (not modified → wait → retry).
- ETag sources: `LatestData?.ETag` for entity, `IRepresentationEtagService.Generate(output)` for representation.
- **Role filtering**: `ITransitionAuthorizationManager` filters available transitions per role. Supports `$InstanceStarter`, `$PreviousUser` pseudo-roles.
- **Well-known transitions**: `cancel`, `updateData` and `exit` are listed in `availableTransitions` (configured key, not the well-known alias) with `kind` = `cancel` / `updateData` / `exit`, and their `roles` are role-filtered like any other transition. Full guide: `docs/domain/well-known-transitions.md`.
- **`availableIn`**: accepts bare state keys or `{ state, roles }` objects (mixable). Per-state `roles` compose with `transition.roles` as an **AND**. State function and `authorize` enforce state+roles; the execution policy enforces state only. Use `Transition.IsAvailableInState` / `FindAvailableIn`, never the raw list.
- No server-side hold — 304 response drives client-side polling.
- Subflow completion window: while parent correlation is open, State function shows **parent** main-flow transitions instead of subflow terminal view.

### User Integration (Backend-Driven View)

Client interaction follows a deterministic loop managed by vNext Client Workflow Manager SDK:
1. Start instance → poll State function until `status = Active`
2. Fetch view definition via View function → fetch data via Data function if `loadData: true`
3. Render UI → user triggers transition → check for transition-level view (modal/popup)
4. Submit transition → re-poll State function → loop until `status = Completed`

Backend-Driven View approach: UI changes deploy via backend only, minimizing mobile/web release cycles.

### View Selection

- `views[]` array on states and transitions; evaluated in declaration order, first matching rule wins.
- Rule: inline C# script implementing `IConditionMapping` with access to `ScriptContext` (Headers, QueryParameters, Instance.Data, State, Transition).
- Last entry without a rule serves as default/fallback — always include one.
- `loadData: true` → instance data loaded alongside view response.

### Instance Data

- **Immutable, versioned** (SemVer): task results → Patch, schema additions → Minor, breaking changes → Major.
- **Full-merge model**: each version contains the complete state + delta. `LatestData` marks current version, `DataList` contains history.
- **Queryable**: filterable on instance columns (`key`, `status`, `currentState`, `createdAt`, etc.) and `attributes.*` JSON paths using GraphQL-style filter syntax.
- **Operators**: `eq`, `ne`, `gt`, `ge`, `lt`, `le`, `between`, `like`, `startswith`, `endswith`, `in`, `nin`, `isnull`.
- **Logical operators**: `and`, `or`, `not` for complex nested queries.
- **Aggregations**: `groupBy` with `count`, `sum`, `avg`, `min`, `max`.
- Master schema (`attributes.schema`) governs data structure; changes are versioned.

### Error Boundary

- **Levels**: Task → State → Global (resolved by `CompiledBoundaryChain`). Rules sorted by `EffectivePriority` ASC → specificity DESC → definition order.
- **Actions**: `Abort`, `Retry`, `Rollback`, `Ignore`, `Notify`, `Log`.
- **Pipeline mapping** (`BoundaryOutcomeHandler`): `Log`/`Ignore` → `Continue()`; transition set → `RequestNextTransition` + `SkipToFinalize()`; abort without transition → Fail → instance fault.
- Error-boundary profile disables subflow handling and skips ResourceLock; its current code does not exclude the Auto step.

### SubFlow Lifecycle

- **SubFlow (S)**: On completion → output mapping → `ResumePipelineAsync` with `ResumeFrom = ClearBusyOnResumeStep` (order 79). Parent pipeline resumes execution.
- **SubProcess (P)**: On completion → correlation complete + persist → no parent resume (fire-and-forget).
- Start uses `StrictIdempotency: true` with parent metadata in `ExtraProperties`.
- Start, active-child forward and descended retry calls use `sync=true`; `S` versus `P` controls parent continuation, not call transport mode.
- On resume failure, correlation is reverted in a new UoW for retry.
- **Completion window**: If subflow is in terminal status while parent correlation is still open, State function shows parent transitions instead of subflow terminal view.

### Instance Repository Include Strategy

- Pipeline steps do NOT call EF `Include` directly — includes are applied at load time via `WithDetailsAsync()`.
- Default load: `DataList` (or latest-only when `WorkflowExecution:LatestOnlyInstanceLoading` is on) + `Include(ChildCorrelations.Where(!IsCompleted))` with split queries.
- `GetResultAsync(includeDetails: false)` is lean (no DataList/correlations). `true` uses `WithDetailsAsync()`.
- History paths use `AsNoTracking` + explicit filtered includes.
- **Rule**: Do not add unnecessary includes. If `TransitionExecutionContext` already has the data, do not re-query.
- Inline context reuse is valid only inside the same pipeline/UoW. Never carry a tracked instance across a post-commit, retry or subflow callback boundary.

---

## Context7 MCP Sources

For domain/platform knowledge beyond what's in code:
- vNext domain: `burgan-tech/vnext-runtime` (tag `vnext-runtime`)
- Aether SDK: `burgan-tech/aether` (tag `aether`)
- Examples: tag `vnext-example`

Detailed docs live in `/docs` (implementation). `/ai-docs` is gitignored local scratch, not a source of truth.

---

## AI guidance layout

Content lives in exactly one place; each tool has a thin entry point that points at it.

| Path | Role | Edit? |
|------|------|-------|
| `AGENTS.md` | Bootstrap for every agent (this file) | yes |
| `CLAUDE.md` | Claude Code entry: imports `AGENTS.md`, lists skills, imports `CLAUDE.local.md` | yes, keep thin |
| `docs/agent-onboarding.md` | Source-of-truth order, where-is-X, known pitfalls | yes |
| `.claude/rules/*.md` | Always-on rules — **single source**. Claude Code loads them natively | yes |
| `.cursor/rules/*.mdc` | Three 8-line pointers; each `@`-includes one file from `.claude/rules/` so Cursor reads the same text | only when a rule file is added/renamed |
| `.claude/skills/*/SKILL.md` | On-demand skills — **single source**. Cursor loads `.claude/skills/` directly for compatibility; there is no `.cursor/skills/` | yes |
| `docs/` | Implementation docs, indexed from `docs/README.md` | yes |
| `ai-docs/superpowers/{specs,plans,reports}/`, `ai-docs/agent-council/sessions/` | Dated decision records — the *why*, not the current contract. Git-ignored local scratch since 2026-09-07; only the council log row in `docs/agent-council/sessions/README.md` is committed | local |
| `CLAUDE.local.md`, `ai-docs/` | Machine-local, git-ignored | personal |

Workflow for a rule or skill change: edit under `.claude/` and commit. Nothing is copied anywhere.
Adding a **new** rule file also needs a matching pointer in `.cursor/rules/` (copy an existing one and
change the `@` path); adding a skill needs nothing.
Facts that belong to the runtime (step order, profile exclusions, event delivery modes) go in
`.claude/rules/` or a `/docs` page and are **linked** from here, never duplicated.
