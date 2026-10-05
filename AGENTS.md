# AGENTS.md

This is the single session-bootstrap file for every coding agent working in this repository (Codex, Cursor, Copilot, Gemini CLI read it directly; Claude Code imports it from `CLAUDE.md`). Tool-specific wiring lives in `CLAUDE.md` (Claude skills, local overrides) and in one pointer file per rule under `.cursor/rules/` — see [AI guidance layout](#ai-guidance-layout) at the end of this file.

## Project Rules (always apply)

These rules are authoritative for all work in this repo. The two code rules are path-scoped — they load
automatically once you read or edit code under `src/`, the hosts, `workers/`, `modules/`, `test/` or
`vnext-meta/`; for a design question asked before any code is open, read them explicitly:

- [Agent onboarding](docs/agent-onboarding.md) — source-of-truth order, where-is-X, known pitfalls. When this file disagrees with code, trust `LifecycleOrder.cs` / `PipelineExecutionProfile.cs`.
- [.NET / Aether / vNext coding standards](.claude/rules/dotnet-coding-standards.md) *(path-scoped)* — Aether SDK usage, event checklist, logging via `WorkflowLogs.cs`, Result pattern, multi-schema, tests.
- [vNext workflow developer reference](.claude/rules/vnext-workflow-developer.md) *(path-scoped)* — the runtime quick-reference card: pipeline, profiles, locking, subflow, authorization, state function; each section links its `/docs` page.
- [Agent Council plan mode](.claude/rules/agent-council-plan-mode.md) — non-trivial decisions must produce an evidence-backed plan before implementation.
- [Codebase navigation — graphify first](.claude/rules/graphify-navigation.md) — when `graphify-out/graph.json` exists, query it (`graphify path`/`explain`/`query`) before grepping or reading broadly.
- [Verifying a change through the MCP servers](.claude/rules/mcp-observability-verification.md) — after exercising a change locally, confirm it with traces, span durations, logs and persisted rows (`.mcp.json`: openobserve, postgres, redis, elasticsearch); a green test run is not evidence on its own.

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
```

**Ports**: Orchestration → 4201, Execution → 4202, Outbox → 4401, Inbox → 4501

### Runbook: "bring up domain X" (for agents)

Commands run without a terminal, so the script never prompts — pass everything explicitly.

1. `cd etc/docker && ./run-docker.sh status` and read `ai-docs/local-environments/README.md` (if present):
   is X already registered, which offset does it have, is anything else running?
2. Decide the offset: `core` → none (offset 0). Another domain → its recorded offset, else the next
   free multiple of 10 (`./run-docker.sh plan X --offset N` shows ports and app-ids; it refuses collisions).
   Tell the user the offset you picked before starting.
3. `./run-docker.sh up X --offset N` (`--no-build` only if the build is
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
   Each of `sync` / `update` / `reset` ends with one `POST definitions/publish/completed` — the
   runtime's post-deployment hook, and the **only automatic** invalidation of the discovery endpoint
   cache (which no longer has a TTL). An older `wf` still calls the removed `definitions/re-initialize`
   and swallows the 404 silently, so cross-domain endpoints stay as they were at startup. See
   [Publish-completed hook](docs/runtime/publish-completed-hook.md).
   System flows (`@burgan-tech/vnext-core-runtime`) go through **that domain's** init container
   (`init` for core on :3005, `init-X` on :3005+offset, already aimed at X's orchestration):
   `curl -X POST localhost:<3005+offset>/api/package/runtime/publish -H 'content-type: application/json' -d '{"appDomain":"X"}'`
   — the call is **asynchronous**: it answers `{"statusUrl": "/api/package/publish/status/<id>"}`; poll
   that URL (or `docker logs init-X`) until the job says completed before running `wf sync`.
   Known quirk: `wf check` may print "API: Not accessible" while `/health` is 200 and `wf sync` works;
   trust `curl localhost:<port>/health`. Verified 2026-09-08 on core: system + example workflows
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

Test projects (`test/`, unit tests only): `Domain.Tests`, `Application.Tests`, `Infrastructure.Tests`, `TestBase` and `Shared` (shared utilities), `Benchmarks`.

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

Orchestration and Execution communicate through **Dapr service invocation**.

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

An event gains a second, immediate delivery path by REGISTERING a relay —
`AddScoped<IPostCommitEventRelay<TEvent>, …>()` in `AddPipelineServices`, no marker interface, no
central switch. Post-commit, `PostCommitRelayDispatcher` relays it as a command via
`IInstanceCommandGateway`, and its Inbox handler becomes a durable backup. Four events are
registered today: the three subflow terminal events (backup deduplicated by `ISubItemTerminalGuard`)
and `InstanceSubStateChangedEvent` (guarded by the per-sub-item lock plus the monotonic
`SubFlowStateChangedAt` stamp). Registration alone is not licence: each relayed event needs a durable
backup, an idempotent order-safe receiver guard, measured latency evidence and a council row. See
`docs/runtime/event-publish-modes.md`.

---

## Domain Concepts

Runtime rules live in **one** place — the quick-reference card
[vNext workflow developer reference](.claude/rules/vnext-workflow-developer.md) (loaded automatically when
you work on code) — and each topic's narrative lives in its `/docs` page. This section is only the map;
do not copy a rule or a table back into it, every behaviour change would then have to touch every copy.
When the card, a doc page and the code disagree, the code wins (`LifecycleOrder.cs`,
`PipelineExecutionProfile.cs`, `PipelineProfileResolver.cs`).

| Concept | One-line orientation | Read |
|---|---|---|
| Transition pipeline | Ordered steps (`LifecycleOrder`), each returns `Result<StepOutcome>`; profiles exclude steps; only `updateData` skips the state lifecycle | card § Transition Pipeline Order · [workflow-execution-pipeline](docs/architecture/workflow-execution-pipeline.md) |
| Sync vs async | `sync=true` blocks to a rest point, `sync=false` (default) answers `{ id, status }` and the client polls; a flow/transition `executionType` overrides the query parameter; runtime-generated child calls are always `sync=true` | card § Sync vs Async · [execution-type](docs/runtime/execution-type.md) |
| State function / long-polling | Conditional GET with ETag, `304` drives client polling, no server-side hold; role-filtered transitions; subflow descent | card § Long-Polling · [state-function-cache-and-etag](docs/runtime/state-function-cache-and-etag.md) |
| Well-known transitions, `availableIn` | `cancel` / `updateData` / `exit` are listed by configured key with a `kind`; `availableIn` roles AND with `transition.roles` | [well-known-transitions](docs/domain/well-known-transitions.md) · [role-grant-authorization](docs/domain/role-grant-authorization.md) |
| Client loop (backend-driven view) | start → poll state → view (+ data when `loadData`) → transition → poll until `Completed` | [vnext-docs](https://burgan-tech.github.io/vnext-docs/) · [view-display-modes](docs/domain/view-display-modes.md) |
| View selection | `views[]` in declaration order, first matching `IConditionMapping` rule wins; last rule-less entry is the fallback | card § View Selection |
| Instance data | Immutable SemVer versions, full-merge model, `LatestData` + `DataList`; filter with fluent `InstanceQuery` | [instance-data-merge-concept](docs/domain/instance-data-merge-concept.md) · [instance-filtering-and-queries](docs/runtime/instance-filtering-and-queries.md) |
| Error boundary | Task → State → Global (`CompiledBoundaryChain`); `BoundaryOutcomeHandler` maps the action onto the pipeline | card § Error Boundary |
| SubFlow / SubProcess | A state starts only an `S` SubFlow; a `P` SubProcess is started by `SubProcessTask`; completion window shows parent transitions | card § SubFlow Lifecycle · [subflow-execution](docs/architecture/subflow-execution.md) |
| Instance load / includes | Includes are applied at load time (`WithDetailsAsync()`), never inside a step; never carry a tracked instance across a post-commit boundary | card § Instance Repository Include Strategy · [inline-chain-context-reuse](docs/architecture/inline-chain-context-reuse.md) |
| Locking | The Busy flag is the mutex; one millisecond-scale status lock per hop | card § Locking |

### Status / State / Type Semantics

**Instance Status**: `Busy (B)` pipeline executing, `Active (A)` waiting, `Passive (P)` deactivated, `Completed (C)` finished, `Faulted (F)` terminal error.

**State Types**: `Initial = 1`, `Intermediate = 2`, `Finish = 3`, `SubFlow = 4`, `Wizard = 5`.

**State Sub Types**: `None = 0`, `Success = 1`, `Error = 2`, `Terminated = 3`, `Suspended = 4`, `Busy = 5`, `Human = 6`, `Cancelled = 7`, `Timeout = 8`.

**Trigger Types**: `Manual = 0`, `Automatic = 1`, `Scheduled = 2`, `Event = 3`.

---

## Platform repositories

The platform is spread over sibling repositories under `github.com/burgan-tech`. Expect each as a
sibling checkout of this one (`../<repo>`) — the layout `nuget.config`, vnext-example's `labs/cross-domain/lab.sh` and
the runbook above already assume. When one is missing, ask the user **once** (clone into `../<repo>`
or use a path they name), remember the answer (Claude: auto-memory; other agents: the developer's
git-ignored `CLAUDE.local.md`), and never write an absolute path into a committed file. Use this table
for impact analysis: a runtime change names the repos it touches; in-repo dependencies come from the
knowledge graph (`graphify`, see the navigation rule).

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
| `docs/ai-capabilities.md` | Catalog: every AI skill, agent, MCP server, script — when and how | yes, with every new skill/agent |
| `.claude/rules/*.md` | Rules — **single source**. Claude Code loads them natively: always, or only for matching files when the frontmatter has `paths:` (the two code rules) | yes |
| `.cursor/rules/*.mdc` | One short pointer per rule file; each `@`-includes its `.claude/rules/` file so Cursor reads the same text, with the same always-on / path-scoped setting | only when a rule file is added/renamed |
| `.claude/skills/*/SKILL.md` | On-demand skills — **single source**. Cursor loads `.claude/skills/` directly for compatibility; there is no `.cursor/skills/` | yes |
| `.claude/agents/*.md` | Claude Code subagents — **thin shells only**. Each one points at the file that holds its content (today: `docs/code-review/reviewers/*.md`); other agents ignore the folder | yes |
| `docs/` | Implementation docs, indexed from `docs/README.md`; `docs/testing/` holds the integration-test contract that the `runtime-integration-test` skill executes, `docs/code-review/` the reviewer checklists that `pr-review` and `workflow-code-review` both run | yes |
| `ai-docs/superpowers/{specs,plans,reports}/`, `ai-docs/agent-council/sessions/` | Dated decision records — the *why*, not the current contract. Git-ignored local scratch since 2026-09-07; only the council log row in `docs/agent-council/sessions/README.md` is committed | local |
| `CLAUDE.local.md`, `ai-docs/` | Machine-local, git-ignored. Optional per-machine notes only (repo paths, ports) — policy never lives here | personal |

Workflow for a rule or skill change: edit under `.claude/` and commit. Nothing is copied anywhere.
Adding a **new** rule file also needs a matching pointer in `.cursor/rules/` (copy an existing one and
change the `@` path); adding a skill needs nothing.
Facts that belong to the runtime (step order, profile exclusions, event delivery modes) go in
`.claude/rules/` or a `/docs` page and are **linked** from here, never duplicated. Writing rules for the
always-loaded files (size budget, what belongs in a rule vs a doc page, the check script):
[agent-onboarding § Editing the AI guidance](docs/agent-onboarding.md#editing-the-ai-guidance).
