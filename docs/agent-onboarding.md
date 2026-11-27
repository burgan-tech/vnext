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
4. **Dated plans/specs** (local scratch, see [AI guidance layout](#ai-guidance-layout)) — the
   *why* of a decision, not today's behavior.

`ai-docs/` is gitignored local scratch (generated dumps, vnext-docs staging).
It is empty in git and is not a source of truth.

## Start files

| File | Role |
| --- | --- |
| [AGENTS.md](../AGENTS.md) | Session bootstrap for every agent: rule links, build/test policy, one-paragraph architecture, concept map, platform repos. `CLAUDE.md` imports it and adds Claude wiring. |
| [.claude/rules/dotnet-coding-standards.md](../.claude/rules/dotnet-coding-standards.md) | Aether usage, Result pattern, logging, event checklist, tests (path-scoped). |
| [.claude/rules/vnext-workflow-developer.md](../.claude/rules/vnext-workflow-developer.md) | Runtime quick-reference card; each section links its `/docs` page (path-scoped). |
| [architecture/workflow-execution-pipeline.md](architecture/workflow-execution-pipeline.md) | Ordered steps, profiles, inline auto-chain, post-commit boundaries. |
| [runtime/event-publish-modes.md](runtime/event-publish-modes.md) | Outbox vs Outbox+PostCommitRelay. |

## AI guidance layout

Content lives in exactly one place; each tool has a thin entry point that points at it.

| Path | Role | Edit? |
|------|------|-------|
| `AGENTS.md` | Bootstrap for every agent | yes |
| `CLAUDE.md` | Claude Code entry: imports `AGENTS.md`, lists skills, imports `CLAUDE.local.md` | yes, keep thin |
| `docs/agent-onboarding.md` | Source-of-truth order, layout, where-is-X, pitfalls (this page) | yes |
| `docs/ai-capabilities.md` | Catalog: every AI skill, agent, MCP server, script — when and how | yes, with every new skill/agent |
| `.claude/rules/*.md` | Rules — **single source**; always-on, or path-scoped through `paths:` (the two code rules) | yes |
| `.cursor/rules/*.mdc` | One pointer per rule; `@`-includes its `.claude/rules/` file with the same scope | only when a rule file is added/renamed |
| `.claude/skills/*/SKILL.md` | On-demand skills — **single source**; Cursor loads `.claude/skills/` directly | yes |
| `.claude/agents/*.md` | Claude Code subagents — **thin shells** over `docs/code-review/reviewers/*.md` | yes |
| `docs/agent-council/` | Council contract (`README.md`), roles, Chair, two templates, regression cases (`evals.md`), the committed decision log | yes |
| `.github/ai/*.md` + `*.json`, `scripts/ai/` | CI agent prompts, their path/gate configuration and the trusted scripts that validate and apply their answers | yes, with tests |
| `docs/` | Implementation docs, indexed from `docs/README.md` | yes |
| `ai-docs/` (`superpowers/`, `agent-council/sessions/`, `handoff/`, `papercuts/`, `local-environments/`) | Git-ignored local scratch: dated decision records, handoffs, machine records — never a source of truth | local |
| `CLAUDE.local.md` | Machine-local, git-ignored notes (repo paths, machine facts) — policy never lives here; ports of running domains are in `ai-docs/local-environments/` | personal |

The two code rules load only once code is open: before answering a design question cold, read the
workflow card first. If a rule ever disagrees with the code, the code wins.

## Editing the AI guidance

- **Rule or skill** → edit under `.claude/rules/` or `.claude/skills/` and
  commit. A **new** rule file also gets a short pointer in `.cursor/rules/`
  (copy an existing one, change the `@` path, keep `alwaysApply`/`globs` in step
  with the rule's `paths:`); a new skill needs nothing.
- **Bootstrap fact for every agent** (ports, commands, test policy, concept map) →
  `AGENTS.md`. `CLAUDE.md` only carries Claude-specific wiring.
- **Runtime fact** (step order, profile exclusions, event modes) → the owning
  `/docs` page, plus at most a one-line rule in
  `.claude/rules/vnext-workflow-developer.md` that links it; do not paste the same
  table or paragraph into a second file.
- **Decision record** (why something was done) → local scratch (`ai-docs/`, git-ignored; layout [above](#ai-guidance-layout)); a council decision additionally gets one row in `docs/agent-council/sessions/README.md`.
- **Long task about to run out of context** → write `ai-docs/handoff/<task>.md` (goal, decisions taken,
  files touched, what is verified, next step) before compaction, and resume from it; a summary loses the
  details a handoff keeps. Advice, not a gate.

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
- **Skill/agent descriptions stay ≤ 350 characters** (the script warns above it), are quoted YAML strings,
  and carry the trigger phrases — nowhere else repeats them.
- **Judgment cues, not blanket bans.** "Match the surrounding style" beats "never write comments": a
  ban that collides with a later request costs reasoning and is often followed instead of the request.
  Hard limits stay only for safety (approvals, secrets, shared systems). Two rules that can contradict
  each other are a bug — fix the pair, do not add a third.
- **A rule about a tool lives in the tool.** Script flags, enums and error messages
  (`run-docker.sh`, `check-ai-guidance.sh`, the CI JSON schemas) carry their own usage; do not restate it
  in a rule or add usage examples. Long output goes to a file, not into the context.

### Writing a skill

- `SKILL.md` body stays under ~500 lines (the script warns); details the run needs only sometimes go in
  linked sibling files read by the step that needs them.
- Numbered steps, one objective each, each with a concrete command or check and how to tell it worked.
- A skill that runs commands gets a **When it fails** table as soon as a real failure is known: known
  failures and their fixes, from real runs or papercuts — never invented, and "try again" is not a fix.
- A step that never varies is a script, not prose for the model to re-derive.
- A skill earns its place by beating no skill on real prompts — see the ablation method in
  [agent-council/evals.md](agent-council/evals.md#method).
- **Run `scripts/check-ai-guidance.sh`** after editing guidance: frontmatter
  parses, relative links resolve, every rule has a Cursor pointer, no duplicate
  headings and matching council verdicts are errors; description length, the
  always-loaded, path-scoped and council-run budgets and a long `SKILL.md` are warnings. The `AI Guidance` workflow runs
  it on every PR that touches guidance.

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
