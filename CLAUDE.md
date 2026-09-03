# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Rules (always apply)

These rules are authoritative for all work in this repo. Read them before writing code:

- [.NET / Aether / vNext coding standards](.claude/rules/dotnet-coding-standards.md) — style, naming, Aether SDK usage, domain-event dual processing, logging via `WorkflowLogs.cs`, Result pattern, multi-schema rules.
- [vNext workflow developer reference](.claude/rules/vnext-workflow-developer.md) — pipeline step order, profiles, subflow lifecycle, error boundary, long-polling, instance data, `vnext-meta`.

### Personal, machine-local overrides

@CLAUDE.local.md

Optional and never committed (git-ignored). Create a `CLAUDE.local.md` in the repo root for your own
environment notes and working preferences; if the file is absent this import is simply ignored.

## Project Skills

On-demand skills live under `.claude/skills/`. Invoke via the `Skill` tool when the trigger phrase matches:

- **vnext-docs-generator** — "döküman oluştur" / "create docs"
- **vnext-meta-validator** — "validate meta" / "meta kontrol" (also after any `vnext-meta/` edit)
- **vnext-meta-matrix** — "meta matrix" / "meta rapor"
- **workflow-code-review** — "code review" / "review et" / "incele"
- **create-github-issue** — "issue aç" / "open issue" / "projeyi tara"
- **create-github-pr** — "PR oluştur" / "open PR" / "pull request"
- **git-commit-message** — "commit mesajı" / "git commit"

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

### Key Infrastructure

- **Database**: PostgreSQL with multi-schema support (one schema per tenant/flow)
- **Cache**: Redis via `IDistributedCache`
- **Messaging**: Dapr pub/sub + transactional Inbox/Outbox workers
- **Scripting**: `modules/BBT.Workflow.Modules.Scripting` — Roslyn-based C# script engine
- **Observability**: OpenTelemetry via Aether → otel-collector → Elastic APM (Kibana) + OpenObserve, structured logging via `WorkflowLogs.cs`

### Multi-Schema Tenancy

Each workflow "flow" has its own PostgreSQL schema. Schema resolution uses `ICurrentSchema` populated from HTTP headers, routes, or query string. Always wrap infrastructure operations with `currentSchema.Use(flow)`.

### Domain Events (dual-processing pattern)

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

Transitions execute through a deterministic pipeline of ordered steps. Each step has a single responsibility and returns `Result<StepOutcome>`. Steps are defined in `LifecycleOrder`:

| Order | Step | Responsibility |
|-------|------|----------------|
| 5 | HandleCancelPreflightStep | Detect cancel/exit; short-circuit if instance already completed |
| 9 | HandleUpdateDataPreflightStep | Parent update-data / shared-transition preflight for subflows |
| 10 | ForwardToActiveSubflowStep | Queue post-commit forward to active subflow; skip epilogue |
| 19 | SetBusyStep | Set instance status to Busy and persist |
| 20 | CreateTransitionRecordStep | Create transition record; duplicate key guard |
| 25 | ResourceLockStep | Acquire/release/extend resource locks via script |
| 30 | RunOnExecuteTasksStep | Run transition OnExecute tasks |
| 38 | ApplyTimeoutStateStep | Apply timeout target into context before exit |
| 39 | CancelScheduledJobsStep | Cancel scheduled jobs for current state |
| 40 | RunOnExitTasksStep | Run leaving-state OnExit tasks |
| 50 | ChangeStateStep | Persist state change |
| 60 | RunOnEntryTasksStep | Run target-state OnEntry tasks |
| 70 | HandleSubFlowStep | Start subflow correlation; enqueue StartSubflowJob |
| 79 | ClearBusyOnResumeStep | Clear busy on subflow resume path |
| 80 | ScheduleTransitionsStep | Schedule future transitions |
| 90 | RunAutomaticTransitionsStep | Evaluate auto-transition conditions; set NextTransition |
| 100 | HandleFinishStep | Complete/cancel instance on finish states |
| 110 | FinalizeTransitionStep | Complete transition record; dispose script cache |
| 112 | ResolveAvailableStep | Resolve deferred Active status |

**StepOutcome**: `Continue()` (next step), `Stop()` (break loop), `SkipTo(order)` (jump + replan), `SkipToFinalize()` (shorthand), `With(Action<PipelineDirectives>)` (mutate directives).

**PipelineExecutionProfile**: Each trigger type resolves to a profile (`IPipelineProfileResolver`) that excludes irrelevant steps. Profiles: Manual (no exclusions), AutoChain (skip Preflight/CheckParentUpdateData/ForwardSubflow/SetBusy/ApplyTimeoutState — ResourceLock runs so auto-chained transitions can lock), Scheduled, Event, ErrorBoundary (`AllowAutoChain=false`, `AllowSubFlow=false`). A **self-target** variant is composed on top of any of these for **`updateData` only** (`SkipsStateLifecycle()` = target is the authored `$self` keyword AND the transition is updateData): it additionally excludes CancelScheduledJobs/OnExit/OnEntry/Schedule, because no state is left or entered. ChangeState and OnExecute deliberately still run. Every **other** `$self` transition — a `$self` shared transition above all — keeps the base profile and runs the **full** lifecycle, including the timer re-arm; `target: $self` means "do not move the instance", not "skip the state's hooks". A literal target equal to the current state does **not** count as `$self` — start and retry-after-commit both present that shape while genuinely needing the state entered. See `.claude/rules/vnext-workflow-developer.md`.

**TransitionExecutionContext**: Built by `TransitionContextFactory` (workflow from `IComponentCacheStore`, instance from `instanceRepository.GetActiveAsync`). Same context reference flows through all steps. `Cache` dict cleared at Finalize. `Directives` accumulate mutations (next transition, post-commit jobs, epilogue skip).

### Status / State / Type Semantics

**Instance Status**: `Busy (B)` pipeline executing, `Active (A)` waiting, `Passive (P)` deactivated, `Completed (C)` finished, `Faulted (F)` terminal error.

**State Types**: `Initial = 1`, `Intermediate = 2`, `Finish = 3`, `SubFlow = 4`, `Wizard = 5`.

**State Sub Types**: `None = 0`, `Success = 1`, `Error = 2`, `Terminated = 3`, `Suspended = 4`, `Busy = 5`, `Human = 6`, `Cancelled = 7`, `Timeout = 8`.

**Trigger Types**: `Manual = 0`, `Automatic = 1`, `Scheduled = 2`, `Event = 3`.

### Sync vs Async Execution

- `sync=true`: Request blocks until pipeline completes; response includes full instance data. Use for deterministic short-lived processes and backend-to-backend integration.
- `sync=false` (default): Request accepted immediately with `{ id, status }`. Client polls via State function for completion. Use for human tasks, external API calls, and mobile/web clients.

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
- Error-boundary profile disables auto-chain and subflow to prevent cascading.

### SubFlow Lifecycle

- **SubFlow (S)**: On completion → output mapping → `ResumePipelineAsync` with `ResumeFrom = ClearBusyOnResumeStep` (order 79). Parent pipeline resumes execution.
- **SubProcess (P)**: On completion → correlation complete + persist → no parent resume (fire-and-forget).
- Start uses `StrictIdempotency: true` with parent metadata in `ExtraProperties`.
- On resume failure, correlation is reverted in a new UoW for retry.
- **Completion window**: If subflow is in terminal status while parent correlation is still open, State function shows parent transitions instead of subflow terminal view.

### Instance Repository Include Strategy

- Pipeline steps do NOT call EF `Include` directly — includes are applied at load time via `WithDetailsAsync()`.
- Default load: `Include(DataList)` + `Include(ChildCorrelations.Where(!IsCompleted))` with split queries.
- History paths use `AsNoTracking` + explicit filtered includes.
- **Rule**: Do not add unnecessary includes. If `TransitionExecutionContext` already has the data, do not re-query.

---

## Context7 MCP Sources

For domain/platform knowledge beyond what's in code:
- vNext domain: `burgan-tech/vnext-runtime` (tag `vnext-runtime`)
- Aether SDK: `burgan-tech/aether` (tag `aether`)
- Examples: tag `vnext-example`

Detailed docs live in `/docs` (implementation) and `/ai-docs` (AI-generated).
