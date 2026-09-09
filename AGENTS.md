# AGENTS.md

This is the single session-bootstrap file for every coding agent working in this repository (Codex, Cursor, Copilot, Gemini CLI read it directly; Claude Code imports it from `CLAUDE.md`). Tool-specific wiring lives in `CLAUDE.md` (Claude skills, local overrides) and in three pointer files under `.cursor/rules/` — see [AI guidance layout](#ai-guidance-layout) at the end of this file.

## Project Rules (always apply)

These rules are authoritative for all work in this repo. Read them before writing code:

- [Agent onboarding](docs/agent-onboarding.md) — source-of-truth order, where-is-X, known pitfalls. When this file disagrees with code, trust `LifecycleOrder.cs` / `PipelineExecutionProfile.cs`.
- [.NET / Aether / vNext coding standards](.claude/rules/dotnet-coding-standards.md) — style, naming, Aether SDK usage, outbox event delivery, logging via `WorkflowLogs.cs`, Result pattern, multi-schema rules.
- [vNext workflow developer reference](.claude/rules/vnext-workflow-developer.md) — pipeline step order, profiles, subflow lifecycle, error boundary, long-polling, instance data, `vnext-meta`.
- [Agent Council plan mode](.claude/rules/agent-council-plan-mode.md) — non-trivial decisions must produce an evidence-backed plan before implementation.
- [Codebase navigation — graphify first](.claude/rules/graphify-navigation.md) — when `graphify-out/graph.json` exists, query it (`graphify path`/`explain`/`query`) before grepping or reading broadly.

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

# Single entry point: etc/docker/run-docker.sh (--help lists everything)
cd etc/docker && ./run-docker.sh          # Infrastructure only (default)
cd etc/docker && ./run-docker.sh dev      # Dev mode: apps in containers, with debugger (asks the domain)
cd etc/docker && ./run-docker.sh stage    # Staging mode (asks the domain)
cd etc/docker && ./run-docker.sh up       # Infra + sidecars + DbMigrator + all hosts as local binaries on a
                                          # domain (asks which). `up sales --offset 10` runs a second domain
                                          # beside core; `plan` shows ports without starting; records land in
                                          # ai-docs/local-environments/<domain>.md

# Or run API hosts by hand (requires infrastructure running)
dotnet run --project orchestration/BBT.Workflow.Orchestration.HttpApi.Host
dotnet run --project execution/BBT.Workflow.Execution.HttpApi.Host
dotnet run --project monitoring/BBT.Workflow.Monitor.HttpApi.Host
```

**Ports**: Orchestration → 4201, Execution → 4202, Monitor → 4203

### Runbook: "bring up domain X" (for agents)

Commands run without a terminal, so the script never prompts — pass everything explicitly.

1. `cd etc/docker && ./run-docker.sh status` and read `ai-docs/local-environments/README.md` (if present):
   is X already registered, which offset does it have, is anything else running?
2. Decide the offset: `core` → none (offset 0). Another domain → its recorded offset, else the next
   free multiple of 10 (`./run-docker.sh plan X --offset N` shows ports and app-ids; it refuses collisions).
   Tell the user the offset you picked before starting.
3. `./run-docker.sh up X --offset N` (`--monitor` only if asked; `--no-build` only if the build is
   known to be fresh). It brings up infra + sidecars, runs DbMigrator, starts the hosts and waits for
   `/health`. Expect a few minutes on a cold build.
4. If it refuses with "docker infra is running from another compose file", stop: another stack (for
   example a cross-domain lab) owns the infra. Report it and let the user decide — do not `down` it yourself.
5. Load components with the vNext CLI (`wf`, from `burgan-tech/vnext-workflow-cli`, installed
   globally) from the domain package repo — for the examples that is `../vnext-example`. `up` has
   already registered the domain in `wf` with the right API port and database; you only switch to it:
   ```bash
   cd ../vnext-example
   wf domain use X && wf domain active      # must print X — the CLI keeps ONE global active domain
   wf check && wf sync                      # sync = add missing; update = changed; reset = force
   ```
   Never run `wf sync` without the `use` step: it publishes to whatever domain was active last time.
   System flows (`@burgan-tech/vnext-core-runtime`) go through **that domain's** init container
   (`init` for core on :3005, `init-X` on :3005+offset, already aimed at X's orchestration):
   `curl -X POST localhost:<3005+offset>/api/package/runtime/publish -H 'content-type: application/json' -d '{"appDomain":"X"}'`
   — the call is **asynchronous**: it answers `{"statusUrl": "/api/package/publish/status/<id>"}`; poll
   that URL (or `docker logs init-X`) until the job says completed before running `wf sync`.
   Known quirk: `wf check` may print "API: Not accessible" while `/health` is 200 and `wf sync` works;
   trust `curl localhost:<port>/health`. Verified 2026-09-08 on core: 7 system + 23 example workflows
   loaded, smoke workflow start → transition → Completed.
   Every port above is written in `ai-docs/local-environments/X.md` — read it instead of computing.
6. Report the base URL and the record path `ai-docs/local-environments/X.md`; point integration tests
   at `VNEXT_BASE_URL=http://localhost:<4201+offset>`. Stop with `./run-docker.sh down X`.

## Testing

```bash
dotnet test                               # Run all tests
dotnet test test/BBT.Workflow.Application.Tests   # Single project
dotnet test --filter "FullyQualifiedName~MyTest"  # Single test
```

Test projects: `Domain.Tests`, `Application.Tests`, `Infrastructure.Tests`, `TestBase` (shared utilities).

**Integration tests** live in the sibling **vnext-example** repo (`tests/Core.IntegrationTests`, on the
`VNext.Testing.Sdk` from **vnext-integration-test**) and run against the **locally built** runtime — never
a container image. Required for changes to core processes (pipeline, transitions, subflows, locking,
instance data, error boundary, state function) and for regression risk; not for small isolated fixes;
when unsure, propose and wait. Every scenario gets a README and a row in vnext-example's
`TEST-SCENARIOS.md` in the same commit. Contract: [docs/testing/integration-testing.md](docs/testing/integration-testing.md);
procedure: the `runtime-integration-test` skill.

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

## Platform repositories

The platform is spread over sibling repositories under `github.com/burgan-tech`. Expect each as a
sibling checkout of this one (`../<repo>`) — the layout `nuget.config`, `labs/cross-domain/lab.sh` and
the runbook above already assume. When one is missing, ask the user **once** (clone into `../<repo>`
or use a path they name), remember the answer (Claude: auto-memory; other agents: the developer's
git-ignored `CLAUDE.local.md`), and never write an absolute path into a committed file. Use this table
for impact analysis: a runtime change names the repos it touches; in-repo dependencies come from the
knowledge graph (`code-review-graph` MCP tools, `graphify`).

| Repo | What it is | Consult when | Trust / rules |
|------|------------|--------------|---------------|
| [vnext](https://github.com/burgan-tech/vnext) | This runtime | — | Code is the source of truth |
| [vnext-example](https://github.com/burgan-tech/vnext-example) | Example flows + integration tests + Python behaviour/load tests + cross-domain lab; the platform team's behavioural checkpoint | Any integration test; `TEST-SCENARIOS.md` is the scenario index | Extend an existing scenario before adding one; README + index row per scenario |
| [vnext-integration-test](https://github.com/burgan-tech/vnext-integration-test) | `VNext.Testing.Sdk` + `VNext.Testing.Template` (Testcontainers or `VNEXT_BASE_URL` external mode); ours | SDK behaviour, assertions, fixture lifecycle | Read it, don't guess; report gaps instead of test-side workarounds. Older clones are named `vnext-integration` |
| [aether](https://github.com/burgan-tech/aether) | Framework SDK (Result, UoW, locks, cache, jobs, multi-schema, events, OTel) | Any SDK-level behaviour | **Propose, don't edit** — the user decides. Unreleased work: `build/pack-local.sh` → `../aether/.local-feed` + `AetherPackageVersion` (`docs/testing/integration-testing.md` §8); revert before PR |
| [vnext-schema](https://github.com/burgan-tech/vnext-schema) | Component schema contracts (`@burgan-tech/vnext-schema`) | New/changed component fields or task types | External release cadence; `npm run validate` may lag the runtime |
| [mocklab](https://github.com/burgan-tech/mocklab) | API mock SDK; first choice for HTTP mocking in tests | Mock seeds, templates, `_admin` API | Templates render all-or-nothing; error in `X-Mocklab-Template-Error` |
| [vnext-ai-toolkit](https://github.com/burgan-tech/vnext-ai-toolkit) | AI skills for domain flow development (`component-task`, `workflow-scaffold`, `integration-test`, ...) | Scaffolding example components | May lag a runtime change — runtime code > vnext-docs > plugin |
| [vnext-sys-flow](https://github.com/burgan-tech/vnext-sys-flow) | Default system components (`@burgan-tech/vnext-core-runtime`), mandatory on a fresh runtime | Publishing a domain for the first time | Loaded through the domain's init container |
| [vnext-forge](https://github.com/burgan-tech/vnext-forge) | Forge Studio — VS Code designer for flows and local dev | Designer-facing metadata (`vnext-meta`), consumer specs in `docs/integration/` | — |
| [vnext-docs](https://github.com/burgan-tech/vnext-docs) | Product docs portal — https://burgan-tech.github.io/vnext-docs/ (technical, business, architecture, client consumption) | "How does a client consume this?", positioning, domain meaning | **May lag development** — not the truth for current runtime behaviour |
| [vnext-helm-charts](https://github.com/burgan-tech/vnext-helm-charts) | Helm chart (`charts/vnext`) used by domain teams to deploy | Environment-only failures; a new mandatory config/env; resource sizing | Defaults are overridable per environment — give an optimum, don't hard-code; `charts/vnext/docs/RESOURCE_TUNING.md` |
| [vnext-workflow-cli](https://github.com/burgan-tech/vnext-workflow-cli) | `wf` CLI (npm global): `domain use`, `check`, `sync`, `update`, `reset`, `csx` | Publishing components locally | One global active domain — `wf domain use X` before every `sync`; read its README, the command set changes |
| [vnext-client-view-renderer](https://github.com/burgan-tech/vnext-client-view-renderer) | View SDK for clients | View contract questions | — |
| [vnext-runtime](https://github.com/burgan-tech/vnext-runtime) | Docker compose templates for a local runtime (`make dev`, `create-domain.sh`) | Cross-domain lab template; someone without this repo's `etc/docker` | Optional — `etc/docker/run-docker.sh` + vnext-example is normally enough |
| [vnext-domain-discovery](https://github.com/burgan-tech/vnext-domain-discovery) | Discovery registry runtime (`@burgan-tech/vnext-discovery-runtime`) | Cross-domain / multi-domain work | `cross-domain-lab` skill |

Context7 MCP tags for the same knowledge: `vnext-runtime`, `aether`, `vnext-example`. Detailed
implementation docs live in `/docs`; `/ai-docs` is git-ignored local scratch, not a source of truth.

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
| `docs/` | Implementation docs, indexed from `docs/README.md`; `docs/testing/` holds the integration-test contract that the `runtime-integration-test` skill executes | yes |
| `ai-docs/superpowers/{specs,plans,reports}/`, `ai-docs/agent-council/sessions/` | Dated decision records — the *why*, not the current contract. Git-ignored local scratch since 2026-09-07; only the council log row in `docs/agent-council/sessions/README.md` is committed | local |
| `CLAUDE.local.md`, `ai-docs/` | Machine-local, git-ignored. Optional per-machine notes only (repo paths, ports) — policy never lives here | personal |

Workflow for a rule or skill change: edit under `.claude/` and commit. Nothing is copied anywhere.
Adding a **new** rule file also needs a matching pointer in `.cursor/rules/` (copy an existing one and
change the `@` path); adding a skill needs nothing.
Facts that belong to the runtime (step order, profile exclusions, event delivery modes) go in
`.claude/rules/` or a `/docs` page and are **linked** from here, never duplicated.
