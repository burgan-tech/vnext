# Agent Onboarding

Short map for a new coding session in this repo. Read this, then follow the
[docs index](README.md) only for the area you are changing. Do not treat dated
design plans or specs as the current contract.

## When sources disagree

Trust this order:

1. **Code** — especially `LifecycleOrder.cs`, `PipelineExecutionProfile.cs`,
   `PipelineProfileResolver.cs`, and the step classes under
   `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/Steps/`.
2. **Current `/docs` pages** linked from [README.md](README.md) (not the
   Historical records section).
3. **`AGENTS.md`** (imported by `CLAUDE.md`) and `.claude/rules/` — Cursor reads
   the same files through `@` pointers in `.cursor/rules/`.
4. **Dated plans/specs** (local scratch, see the AI guidance layout table in `AGENTS.md`) — the
   *why* of a decision, not today's behavior.

`ai-docs/` is gitignored local scratch (generated dumps, vnext-docs staging).
It is empty in git and is not a source of truth.

## Start files

| File | Role |
| --- | --- |
| [AGENTS.md](../AGENTS.md) | Session bootstrap for every agent: hosts, layers, events, concept map, AI guidance layout. `CLAUDE.md` imports it and adds Claude wiring. |
| [.claude/rules/dotnet-coding-standards.md](../.claude/rules/dotnet-coding-standards.md) | Aether usage, Result pattern, logging, event checklist, tests (path-scoped). |
| [.claude/rules/vnext-workflow-developer.md](../.claude/rules/vnext-workflow-developer.md) | Runtime quick-reference card; each section links its `/docs` page (path-scoped). |
| [architecture/workflow-execution-pipeline.md](architecture/workflow-execution-pipeline.md) | Ordered steps, profiles, inline auto-chain, post-commit boundaries. |
| [runtime/event-publish-modes.md](runtime/event-publish-modes.md) | Outbox vs Outbox+PostCommitRelay. |

Each `.cursor/rules/*.mdc` is a short pointer that `@`-includes the matching
`.claude/rules/*.md` with the same scope (always-on, or `globs` for the two
path-scoped code rules), and Cursor loads `.claude/skills/` directly — so there is
no second copy of anything to keep aligned. The two code rules load only once code
is open: before answering a design question cold, read the workflow card first. If a rule ever
disagrees with the code, the code wins.

## Editing the AI guidance

- **Rule or skill** → edit under `.claude/rules/` or `.claude/skills/` and
  commit. A **new** rule file also gets a short pointer in `.cursor/rules/`
  (copy an existing one, change the `@` path, keep `alwaysApply`/`globs` in step
  with the rule's `paths:`); a new skill needs nothing.
- **Bootstrap fact for every agent** (ports, layers, commands, concept map) →
  `AGENTS.md`. `CLAUDE.md` only carries Claude-specific wiring.
- **Runtime fact** (step order, profile exclusions, event modes) → the owning
  `/docs` page, plus at most a one-line rule in
  `.claude/rules/vnext-workflow-developer.md` that links it; do not paste the same
  table or paragraph into a second file.
- **Decision record** (why something was done) → local scratch (`ai-docs/`, git-ignored; layout in
  `AGENTS.md`); a council decision additionally gets one row in `docs/agent-council/sessions/README.md`.

### Token budget for the always-loaded files

Everything Claude Code or Cursor loads at session start is paid for in every
conversation: `AGENTS.md`, `CLAUDE.md`, `CLAUDE.local.md`, the always-on rules and
every skill/agent `description`. Keep them lean:

- **A rule bullet is ≤ 3 lines and imperative** — what to do, what never to do,
  the symbol to look at. The narrative (why, how it was measured, which incident
  produced it) goes to the `/docs` page, and the rule ends with `Full guide: …`.
- **One fact, one file.** Before adding a sentence to an always-loaded file, grep
  for it; if it exists elsewhere, link instead.
- **No fast-rotting values** in always-loaded files: counts ("23 workflows"),
  "status as of <date>", version numbers that a release changes.
- **Code-only rules are path-scoped** (`paths:` frontmatter; the Cursor pointer
  uses `globs` + `alwaysApply: false`). Only rules that apply to every kind of
  session (council, MCP verification core, graphify) stay always-on.
- **Skill/agent descriptions stay ≤ ~300 characters**, are quoted YAML strings,
  and carry the trigger phrases — nowhere else repeats them.
- **Run `scripts/check-ai-guidance.sh`** after editing guidance: frontmatter
  parses, relative links resolve, every rule has a Cursor pointer, no duplicate
  headings, and the always-loaded total stays under budget.

## Where is X

| Need | Open first |
| --- | --- |
| Which AI skill / agent / MCP server to use, and how | [ai-capabilities.md](ai-capabilities.md) |
| Pipeline step order / skip / profile | `src/BBT.Workflow.Domain/Execution/Transitions/Pipeline/LifecycleOrder.cs`, `PipelineExecutionProfile.cs`; steps in `.../Application/.../Pipeline/Steps/` |
| Subflow start / forward / resume | [architecture/subflow-execution.md](architecture/subflow-execution.md) |
| `cancel` / `updateData` / `exit` | [domain/well-known-transitions.md](domain/well-known-transitions.md) |
| Distributed events | [runtime/event-publish-modes.md](runtime/event-publish-modes.md); contracts in `src/BBT.Workflow.Events.Contracts/`; handlers in `workers/BBT.Workflow.Workers.Inbox/Handlers/` |
| Task type numbers | `src/BBT.Workflow.Domain/Definitions/Tasks/TaskEnums.cs` (`CacheAside = 18`, `GetInstance = 19`, `FanOut = 21`, `Python = 23`) |
| Instance load / includes | `EfCoreInstanceRepository.WithDetailsAsync()` — latest-only is gated by `WorkflowExecution:LatestOnlyInstanceLoading`; `GetResultAsync(includeDetails: false)` is lean |
| Hosts / ports | Orchestration `4201`, Execution `4202`; Inbox `4501`, Outbox `4401` (core). Other domains run at `base + offset`, or co-hosted on one host set (`up <primary> --with …`, [multi-domain hosting](runtime/multi-domain-hosting.md)); what is running right now, with ports, app-ids and database: `ai-docs/local-environments/<domain>.md` (git-ignored, written by `etc/docker/run-docker.sh up`) |
| Layer references | [architecture/dependency-map.md](architecture/dependency-map.md) |
| Run an integration test for a runtime change | [testing/integration-testing.md](testing/integration-testing.md); tests live in sibling `../vnext-example` (`tests/Core.IntegrationTests`, `TEST-SCENARIOS.md`); skill `runtime-integration-test` |
| Which sibling repo owns X / where to clone it | `AGENTS.md` § Platform repositories (`../<repo>` layout, ask once, never commit absolute paths) |
| Build against unreleased Aether | [testing/integration-testing.md](testing/integration-testing.md) §8 — `aether/build/pack-local.sh`, `nuget.config` (both blocks), `AetherPackageVersion`; revert before PR |
| Env-only failure / new mandatory config | [testing/integration-testing.md](testing/integration-testing.md) §9 — `vnext-helm-charts/charts/vnext/values.yaml` passthrough, `RESOURCE_TUNING.md` |

## Pitfalls that have already cost work

- **There is no `HandleUpdateDataPreflightStep` (order 9).** Parent `updateData`
  is not forwarded (`ForwardToActiveSubflowStep`, 10) and, with an open SubFlow
  correlation, short-circuits at `HandleUpdateDataDataOnlyStep` (21).
- **Epilogue is Auto (80) then Schedule (90).** A satisfied auto winner must not
  arm timers that the next hop would immediately cancel.
- **EventHook is deleted.** New events are `[EventName]` + Inbox `IEventHandler<T>`
  + `WorkflowLogs`. An event gets the immediate post-commit path by registering an
  `IPostCommitEventRelay<TEvent>`
  (Outbox + `PostCommitRelayDispatcher`).
- **`$self` does not skip state lifecycle** except `updateData`
  (`SkipsStateLifecycle`). A `$self` shared transition still runs OnExit/OnEntry
  and re-arms timers.
- **Error-boundary profile** skips Preflight, ForwardToActiveSubflow and
  ResourceLock only. It does not disable SubFlow (70) or Auto (80); there is no
  `AllowSubFlow` flag (it was deleted, never enforced), only `AllowAutoChain`.
- **Do not invent EventHooks, order-9 preflight, or Schedule-before-Auto.**

## What this page is not

Product docs for consumer teams live in [vnext-docs](https://burgan-tech.github.io/vnext-docs/).
This repo's `/docs` is the runtime implementation set. Do not duplicate that
site here.
