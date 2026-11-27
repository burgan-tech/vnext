# AGENTS.md

Session bootstrap for every coding agent in this repo (Codex, Cursor and Copilot read it
directly; Claude Code imports it from `CLAUDE.md`). It holds only what the code cannot tell you; the
layout of the AI guidance is at the [end](#ai-guidance-layout).

## Project rules

Authoritative for how to work here; for what the runtime does, the code wins, then the current
`/docs` page, then these files. The two code rules load automatically in Claude Code and Cursor once
code under `src/`, the hosts, `workers/`, `modules/`, `tools/` or `test/` is open (the workflow card
also for `vnext-meta/`); other agents, and any design question asked before code is open, read them
explicitly.

- [Agent onboarding](docs/agent-onboarding.md) — source-of-truth order, where-is-X, pitfalls. Code wins over every doc (`LifecycleOrder.cs`, `PipelineExecutionProfile.cs`).
- [.NET / Aether coding standards](.claude/rules/dotnet-coding-standards.md) *(code)* — Aether, Result pattern, `WorkflowLogs.cs`, event checklist, multi-schema, tests.
- [Workflow developer card](.claude/rules/vnext-workflow-developer.md) *(code)* — pipeline, profiles, locking, subflow, authorization, state function; each section links its `/docs` page.
- [Agent Council](.claude/rules/agent-council-plan-mode.md) — a non-trivial decision gets an evidence-backed council verdict before implementation.
- [Graphify first](.claude/rules/graphify-navigation.md) — with `graphify-out/graph.json`, query the graph before grepping broadly.
- [MCP verification](.claude/rules/mcp-observability-verification.md) — confirm a locally exercised change with traces, logs and rows; a green test is not evidence alone.
- **Friction** — when a documented command, flag, step or doc proves wrong, or the same environment
  failure repeats with no progress, record it with the [`papercut`](.claude/skills/papercut/SKILL.md)
  procedure (plain steps, no tool needed) and continue with the workaround.

## Build, run, test

```bash
./scripts/setup-netstandard-ref.sh   # once on macOS/Linux, before the first build (PostSharp on .NET 10)
dotnet restore && dotnet build
cd etc/docker && ./run-docker.sh --help   # infra only (default), dev, stage, up <domain> [--offset N] [--with d1,d2], plan, status, down
```

Ports: Orchestration 4201, Execution 4202, Outbox 4401, Inbox 4501 (other domains at `+offset`; what is
running: `ai-docs/local-environments/<domain>.md`). Agents bring a domain up by the
[local-environment runbook](docs/runtime/local-environment.md): pass every argument explicitly, never
`down` a stack another compose file owns, `wf domain use X` before every `wf sync`. **Who starts it:**
run `run-docker.sh up/status/down` yourself when the user asked for it or the integration-test policy
requires the run; in every other case (a council session, a hunch, a borderline test) offer and wait.

- **Unit tests are always green.** `test/` holds unit tests only; every PR leaves zero failing tests,
  including reds it did not cause — no known-red list, no `Skip` to get green (a skip needs a reason
  and an issue reference). Run projects one at a time
  (`dotnet test test/<Project> -c Release`): [unit-tests](docs/testing/unit-tests.md).
- **Integration tests** live in sibling **vnext-example** (`tests/Core.IntegrationTests`, on
  `VNext.Testing.Sdk` from **vnext-integration-test**) and run against the **locally built** runtime,
  never an image. Required for core-process changes (pipeline, transitions, subflows, locking, instance
  data, error boundary, state function) and regression risk; when unsure, propose and wait. Each
  scenario gets a README and a `TEST-SCENARIOS.md` row in the same commit:
  [integration-testing](docs/testing/integration-testing.md), skill `runtime-integration-test`.

## Architecture in one paragraph

A distributed workflow engine on .NET 10, Clean Architecture/DDD and the Aether SDK. The public
**Orchestration** host owns definitions, instances and transitions; the internal **Execution** host runs
task invokers; they talk through Dapr service invocation. PostgreSQL holds one schema per flow — always
wrap infrastructure work in `currentSchema.Use(flow)`. Redis is the distributed cache. Every distributed
event goes through the transactional outbox (EventHook is deleted; an immediate post-commit relay is
opt-in per event, [event-publish-modes](docs/runtime/event-publish-modes.md)). Layers and their allowed
references: [dependency-map](docs/architecture/dependency-map.md).

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
| Sync vs async | `sync=true` blocks to a rest point, `sync=false` (default) answers `{ id, status }` and the client polls; a flow/transition `executionType` overrides the query parameter; runtime child start and descended retry are `sync=true`, the SubFlow proxy forwards in the parent's mode | card § Sync vs Async · [execution-type](docs/runtime/execution-type.md) |
| State function / long-polling | Conditional GET with ETag, `304` drives client polling, no server-side hold; role-filtered transitions; subflow descent | card § Long-Polling · [state-function-cache-and-etag](docs/runtime/state-function-cache-and-etag.md) |
| Well-known transitions, `availableIn` | `cancel` / `updateData` / `exit` are listed by configured key with a `kind`; `availableIn` roles AND with `transition.roles` | [well-known-transitions](docs/domain/well-known-transitions.md) · [role-grant-authorization](docs/domain/role-grant-authorization.md) |
| Row security | per-flow role-keyed row rules on list, single GET and the GetInstances / GetInstance tasks | [row-security](docs/domain/row-security.md) |
| Client loop (backend-driven view) | start → poll state → view (+ data when `loadData`) → transition → poll until `Completed` | [vnext-docs](https://burgan-tech.github.io/vnext-docs/) · [view-display-modes](docs/domain/view-display-modes.md) |
| View selection | `views[]` in declaration order, first matching `IConditionMapping` rule wins; last rule-less entry is the fallback | card § View Selection |
| Instance data | Immutable SemVer versions, full-merge model, `LatestData` + `DataList`; filter with fluent `InstanceQuery` | [instance-data-merge-concept](docs/domain/instance-data-merge-concept.md) · [instance-filtering-and-queries](docs/runtime/instance-filtering-and-queries.md) |
| Error boundary | Task → State → Global (`CompiledBoundaryChain`); `BoundaryOutcomeHandler` maps the action onto the pipeline | card § Error Boundary |
| SubFlow / SubProcess | A state starts only an `S` SubFlow; a `P` SubProcess is started by `SubProcessTask`; completion window shows parent transitions | card § SubFlow Lifecycle · [subflow-execution](docs/architecture/subflow-execution.md) |
| Instance load / includes | Includes are applied at load time (`WithDetailsAsync()`), never inside a step; never carry a tracked instance across a post-commit boundary | card § Instance Repository Include Strategy · [inline-chain-context-reuse](docs/architecture/inline-chain-context-reuse.md) |
| Locking | The Busy flag is the mutex; one millisecond-scale status lock per hop | card § Locking |

### Status / State / Type Semantics

**Instance Status**: `Busy (B)` pipeline executing, `Active (A)` waiting, `Passive (P)` deactivated, `Completed (C)` finished, `Faulted (F)` terminal error.

**State Types**: `Initial = 1`, `Intermediate = 2`, `Finish = 3`, `SubFlow = 4`, `Wizard = 5`, `AiAgent = 6`.

**State Sub Types**: `None = 0`, `Success = 1`, `Error = 2`, `Terminated = 3`, `Suspended = 4`, `Busy = 5`, `Human = 6`, `Cancelled = 7`, `Timeout = 8`.

**Trigger Types**: `Manual = 0`, `Automatic = 1`, `Scheduled = 2`, `Event = 3`.

---

## Platform repositories

The platform is spread over sibling repositories under `github.com/burgan-tech`. Expect each as a
sibling checkout of this one (`../<repo>`) — the layout `nuget.config`, vnext-example's `labs/cross-domain/lab.sh` and
the [local-environment runbook](docs/runtime/local-environment.md) already assume. When one is missing, ask the user **once** (clone into `../<repo>`
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

Context7 MCP tags: `vnext-runtime`, `aether`, `vnext-example`. `/ai-docs` is git-ignored local scratch,
never a source of truth.

---

## AI guidance layout

Content lives in exactly one place; each tool has a thin entry point. Rules and skills live under
`.claude/` (Cursor reaches rules through `@` pointers in `.cursor/rules/` and loads `.claude/skills/`
directly); runtime facts go in the workflow card or a `/docs` page and are linked, never copied;
`ai-docs/` is git-ignored local scratch, and only the council log row is committed. The full layout
table and the writing rules for always-loaded files (size budget, check script):
[agent-onboarding § AI guidance layout](docs/agent-onboarding.md#ai-guidance-layout).
