# Agent Onboarding

Short map for a new coding session in this repo. Read this, then follow the
[docs index](README.md) only for the area you are changing. Do not treat dated
plans under `docs/superpowers/` as the current contract.

## When sources disagree

Trust this order:

1. **Code** — especially `LifecycleOrder.cs`, `PipelineExecutionProfile.cs`,
   `PipelineProfileResolver.cs`, and the step classes under
   `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/Steps/`.
2. **Current `/docs` pages** linked from [README.md](README.md) (not the
   Historical records section).
3. **`AGENTS.md` / `CLAUDE.md`** and `.claude/rules/` (they must stay aligned
   with `.cursor/rules/`).
4. **Dated plans/specs** in `docs/superpowers/` — the *why* of a decision, not
   today's behavior.

`ai-docs/` is gitignored local scratch (generated dumps, vnext-docs staging).
It is empty in git and is not a source of truth.

## Start files

| File | Role |
| --- | --- |
| [AGENTS.md](../AGENTS.md) / [CLAUDE.md](../CLAUDE.md) | Session bootstrap: hosts, pipeline card, events, subflow. |
| [.claude/rules/dotnet-coding-standards.md](../.claude/rules/dotnet-coding-standards.md) | Style, Result pattern, logging, **outbox events** (EventHook is gone). |
| [.claude/rules/vnext-workflow-developer.md](../.claude/rules/vnext-workflow-developer.md) | Pipeline, profiles, locking, well-known transitions, `availableIn`. |
| [architecture/workflow-execution-pipeline.md](architecture/workflow-execution-pipeline.md) | Ordered steps, profiles, inline auto-chain, post-commit boundaries. |
| [runtime/event-publish-modes.md](runtime/event-publish-modes.md) | Outbox vs Outbox+TerminalRelay. |

Cursor always-applies `.cursor/rules/vnext.mdc` and
`.cursor/rules/vnext-workflow-developer.mdc`. Those must match the Claude rules
above; if they do not, the code wins.

## Where is X

| Need | Open first |
| --- | --- |
| Pipeline step order / skip / profile | `src/BBT.Workflow.Domain/Execution/Transitions/Pipeline/LifecycleOrder.cs`, `PipelineExecutionProfile.cs`; steps in `.../Application/.../Pipeline/Steps/` |
| Subflow start / forward / resume | [architecture/subflow-execution.md](architecture/subflow-execution.md) |
| `cancel` / `updateData` / `exit` | [domain/well-known-transitions.md](domain/well-known-transitions.md) |
| Distributed events | [runtime/event-publish-modes.md](runtime/event-publish-modes.md); contracts in `src/BBT.Workflow.Events.Contracts/`; handlers in `workers/BBT.Workflow.Workers.Inbox/Handlers/` |
| Task type numbers | `src/BBT.Workflow.Domain/Definitions/Tasks/TaskEnums.cs` (`CacheAside = 18`, `GetInstance = 19`, `FanOut = 21`, `Python = 23`) |
| Instance load / includes | `EfCoreInstanceRepository.WithDetailsAsync()` — latest-only is gated by `WorkflowExecution:LatestOnlyInstanceLoading`; `GetResultAsync(includeDetails: false)` is lean |
| Hosts / ports | Orchestration `4201`, Execution `4202`, Monitor `4203`; Inbox `4501`, Outbox `4401` |
| Layer references | [architecture/dependency-map.md](architecture/dependency-map.md) |

## Pitfalls that have already cost work

- **There is no `HandleUpdateDataPreflightStep` (order 9).** Parent `updateData`
  is not forwarded (`ForwardToActiveSubflowStep`, 10) and, with an open SubFlow
  correlation, short-circuits at `HandleUpdateDataDataOnlyStep` (21).
- **Epilogue is Auto (80) then Schedule (90).** A satisfied auto winner must not
  arm timers that the next hop would immediately cancel.
- **EventHook is deleted.** New events are `[EventName]` + Inbox `IEventHandler<T>`
  + `WorkflowLogs`. Subflow terminal events also implement `ISubflowTerminalEvent`
  (Outbox + `SubflowTerminalRelay`).
- **`$self` does not skip state lifecycle** except `updateData`
  (`SkipsStateLifecycle`). A `$self` shared transition still runs OnExit/OnEntry
  and re-arms timers.
- **Error-boundary profile** skips Preflight, ForwardToActiveSubflow, and
  ResourceLock; `AllowSubFlow = false`; `AllowAutoChain = true`; Auto is **not**
  excluded.
- **Do not invent EventHooks, order-9 preflight, or Schedule-before-Auto.**

## What this page is not

Product docs for consumer teams live in [vnext-docs](https://burgan-tech.github.io/vnext-docs/).
This repo's `/docs` is the runtime implementation set. Do not duplicate that
site here.
