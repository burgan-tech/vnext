# Workflow Execution Pipeline

## Purpose

The transition pipeline is the deterministic state machine executor. It takes a
`WorkflowExecutionContext`, builds a `TransitionExecutionContext`, validates it, applies
an execution profile, and runs ordered steps until the transition chain is complete.

## Boundaries

The pipeline owns ordering, locking, flow control, auto-chain continuation, post-commit
jobs, and fault marking. Individual steps own one lifecycle concern. Steps should not
load unrelated state or make policy decisions that belong to profile resolution.

## Architecture Flow

| Order | Step | Responsibility |
| --- | --- | --- |
| 5 | Preflight | Cancel/exit detection and already-completed guard. |
| 9 | Parent update-data preflight | Shared transition handling for parent/subflow flows. |
| 10 | Forward to active subflow | Forward parent transitions into an active subflow when applicable. |
| 19 | Set Busy | Mark the instance Busy during transition execution. |
| 20 | Create transition | Persist the transition attempt and duplicate guard. |
| 25 | Resource lock | Acquire, release, or extend business resource locks. |
| 30 | OnExecute | Run transition tasks before leaving the state. |
| 38 | Apply timeout state | Apply timeout target into context before exit. |
| 39 | Cancel scheduled jobs | Cancel timer jobs for the leaving state. |
| 40 | OnExit | Run leaving-state tasks. |
| 50 | Change state | Persist current/effective state changes. |
| 60 | OnEntry | Run target-state tasks. |
| 70 | SubFlow | Create correlation and enqueue subflow start work. |
| 79 | Clear busy on resume | Clear parent Busy state on subflow resume path. |
| 80 | Schedule | Enqueue scheduled transitions. |
| 90 | Auto | Evaluate automatic transitions and request the next transition. |
| 100 | Finish | Complete or cancel terminal instances. |
| 110 | Finalize | Complete transition record and clear script cache. |
| 112 | Resolve available | Resolve deferred Active status. |

`StepOutcome` controls execution:

- `Continue()` moves to the next step.
- `Stop()` exits the current pipeline run.
- `SkipTo(order)` replans from the requested order.
- `SkipToFinalize()` jumps to finalization.
- `With(Action<PipelineDirectives>)` mutates typed directives.

Profiles remove irrelevant steps:

| Profile | Trigger | Notes |
| --- | --- | --- |
| Manual | Manual | Full pipeline, auto-chain and subflow allowed. |
| AutoChain | Automatic | Skips preflight, busy marking, resource lock, timeout application, and subflow forwarding. |
| Scheduled | Scheduled | Skips preflight and parent/subflow forwarding. |
| Event | Event | Skips preflight and forward-to-active-subflow. |
| ErrorBoundary | Error boundary | Minimal recovery path; lock and subflow prelude are excluded. |

## Contracts

| Input | Output | Invariants |
| --- | --- | --- |
| `WorkflowExecutionContext` | `TransitionExecutionContext` | Context factory loads workflow definition and active instance. |
| Ordered `ITransitionStep` list | Mutated instance and directives | Steps execute by `LifecycleOrder`. |
| `PipelineDirectives` | Post-commit jobs, deferred events, next transition | Directives are consumed explicitly to avoid repeated work. |

The main lock is acquired once and held across an automatic chain. Reserved transitions
can use their own lock path, for example subflow resume.

## Failure Modes

- Validation failure prevents the pipeline from starting.
- Step exceptions are converted to pipeline failures.
- Unhandled pipeline errors mark the instance Faulted and add an incident when needed.
- Post-commit failure can fault the instance if it returns a fault request.
- Chain depth is capped to prevent infinite automatic transition loops.

## Observability

The pipeline begins a logging scope with domain, flow, flow version, instance id,
instance key, state from/to, transition key, trigger type, chain depth, and profile.
The current trace is enriched with `vnext.chain.depth`, `vnext.pipeline.profile`, and
`vnext.chain.id`.

## Change Safety

- Add new steps with explicit `LifecycleOrder` gaps when possible.
- If a step can alter control flow, use `PipelineDirectives` rather than hidden state.
- Keep profile exclusions synchronized with tests in `PipelineExecutionProfileTests`.
- Do not bypass `TransitionContextFactory` when the pipeline needs workflow or instance state.

### PostgreSQL-to-Dapr Lock Cutover

Deployments upgrading from the former PostgreSQL-backed general `IDistributedLockService`
binding to the Dapr-backed binding must not use a rolling update. Old and new orchestration
replicas would coordinate through different stores, so the same logical lock could be acquired
in both. Use a quiesced/Recreate cutover, or blue-green only when the old replicas are fully
quiesced and stopped before the new replicas can execute workflow or background operations.

## References

- `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/TransitionPipeline.cs`
- `src/BBT.Workflow.Domain/Execution/Transitions/Pipeline/LifecycleOrder.cs`
- `src/BBT.Workflow.Domain/Execution/Transitions/Pipeline/StepOutcome.cs`
- `src/BBT.Workflow.Domain/Execution/Transitions/Pipeline/PipelineExecutionProfile.cs`
- `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/PipelineProfileResolver.cs`
- `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/Steps/`
- [Async Transition Execution Modes](async-transition-execution-modes.md) — how the `WorkflowExecution` flags route async continuations.
