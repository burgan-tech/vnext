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
| 10 | Forward to active subflow | Queue a post-commit forward to an active subflow when the transition reaches the pipeline. Normally the [transition proxy](subflow-transition-proxy.md) has already forwarded it before the pipeline; this step covers the proxy-declined race and legacy jobs. Does not forward `updateData` or a parent shared transition available in the current state. |
| 19 | Set Busy | Mark the instance Busy during transition execution. |
| 20 | Create transition | Persist the transition attempt and duplicate guard. |
| 21 | Parent update-data data-only | When a parent has an open SubFlow correlation, persist update data and skip state lifecycle/epilogue. |
| 25 | Resource lock | Acquire, release, or extend business resource locks. |
| 30 | OnExecute | Run transition tasks before leaving the state. |
| 38 | Apply timeout state | Apply timeout target into context before exit. |
| 39 | Cancel scheduled jobs | Cancel timer jobs for the leaving state. |
| 40 | OnExit | Run leaving-state tasks. |
| 50 | Change state | Persist current/effective state changes. |
| 60 | OnEntry | Run target-state tasks. |
| 70 | SubFlow | Create correlation and enqueue subflow start work. |
| 75 | Long-poll termination | Pause after state entry and arm an acknowledgment fallback job when configured. |
| 79 | Clear busy on resume | Clear parent Busy state on subflow resume path. |
| 80 | Auto | Evaluate automatic transitions and request the next transition. |
| 90 | Schedule | Enqueue scheduled transitions. Skipped when Auto already selected a next transition. |
| 100 | Finish | Complete or cancel terminal instances. |
| 110 | Finalize | Complete transition record and clear script cache. |
| 112 | Resolve available | Resolve deferred Active status. |

There is no step at order 9 (`HandleUpdateDataPreflightStep` was removed). Parent `updateData`
handling is: neither the proxy nor `ForwardToActiveSubflowStep` forwards it, and `HandleUpdateDataDataOnlyStep`
(21) skips to Finalize when the parent has an open SubFlow correlation.

`StepOutcome` controls execution:

- `Continue()` moves to the next step.
- `Stop()` exits the current pipeline run.
- `SkipTo(order)` replans from the requested order.
- `SkipToFinalize()` jumps to finalization.
- `With(Action<PipelineDirectives>)` mutates typed directives.

Profiles remove irrelevant steps:

| Profile | Trigger | Notes |
| --- | --- | --- |
| Manual | Manual | Full pipeline. |
| AutoChain | Automatic | Skips preflight, active-subflow forwarding, Busy marking and timeout application. ResourceLock still runs. |
| Scheduled | Scheduled | Skips preflight and active-subflow forwarding. |
| Event | Event | Skips preflight and active-subflow forwarding. |
| ErrorBoundary | Error boundary | Skips preflight, active-subflow forwarding and ResourceLock; `AllowAutoChain=true` (Auto is not excluded). |

### Error-boundary profile: what it does not exclude

The error-boundary profile skips Preflight, ForwardToActiveSubflow and ResourceLock. It does **not**
disable subflow handling and does not remove the Auto step: the plan is built from
`ExcludedStepOrders` alone (`TransitionExecutor.BuildExecutionPlan`), and `LifecycleOrder.SubFlow`
(70) is in no exclusion set. A profile-level "allow subflow" flag used to exist and was never read;
it was deleted rather than given teeth, because enforcing it would have been an unrequested
behaviour change.

Resolution: `IPipelineProfileResolver.Resolve(workflowContext, transitionContext)` — if
`IsErrorBoundaryTransition` → ErrorBoundary; else by the **workflow context's** `TriggerType` (not
the transition definition's — the two can disagree and the inbound trigger is authoritative).

A sixth profile is **composed on top of** the trigger's profile rather than selected instead of it.
For an `updateData` transition, `PipelineExecutionProfile.ForSelfTarget` layers the state-lifecycle
exclusions onto the base profile (`Manual+Self`, `AutoChain+Self`, …):

| Excluded for updateData | Why |
| --- | --- |
| CancelScheduledJobs (39) | The state is not left; tearing its timers down would lose them. |
| OnExit (40) | No state is left. |
| OnEntry (60) | No state is entered; the hooks already ran when the instance first arrived. |
| Schedule (90) | Re-arming the state's timers would silently restart every timeout. |

`ChangeState (50)` deliberately still runs — it is the only step that sets `context.Target`, which
`RunAutomaticTransitionsStep (80)` needs in order to evaluate the state's auto transitions against
the freshly written data. `OnExecute (30)` also still runs: that is the transition's own work, not
the state's lifecycle. `ChangeStateStep` suppresses its state-change metric, log and span event on
this path, since reporting a change from a state to itself is a false signal there.

This is what makes `updateData` behave as intended: write the data, evaluate the auto transitions,
and chain on if one is satisfied — without re-running the current state's entry hooks.

**`updateData` is the only transition that gets it.** The variant's name says "self" because
`updateData`'s target *is* `$self`, but the selection is a policy in `PipelineProfileResolver`
(`TransitionExecutionContextExtensions.SkipsStateLifecycle`), not a property of the target. Any
other transition declaring `target: $self` — a **shared transition** being the real case — keeps the
trigger's base profile and runs the state's **full** lifecycle: OnExit and OnEntry fire, and the
state's scheduled transitions are cancelled and re-armed. Declaring `$self` says "do not move the
instance"; it does not say "skip the state's hooks". Note the consequence for timers: a frequently
invoked `$self` shared transition on a state with a short timeout pushes that timeout out on every
call.

**Only the authored `$self` keyword qualifies** for the target check itself. A literal target that
happens to equal the current
state does not, because that comparison is a coincidence produced by three unrelated mechanisms and
means "no state change" in only one of them:

- **Start** — `InstanceCommandAppService` pre-positions a new instance into the initial state at
  creation, before dispatching the start transition. The state still needs entering.
- **Retry after a partial commit** — `ChangeStateStep` persists with `saveChanges`, so a transition
  faulting in OnEntry leaves the instance committed in the target state; the retry exists to redo
  exactly that step.
- **A genuine self-loop** (`from: A, target: A`) — the one case where it does mean unchanged. Authors
  wanting the no-state-change semantics use `$self`; naming a state reads as "enter that state".

### Self-target composition: invariants

- **`SkipsStateLifecycle() = IsSelfTargetTransition() && IsUpdateDataTransition()`.** Two separate
  claims, deliberately: the first is a fact about the target, the second is the policy. Only
  `updateData` skips the lifecycle.
- **The `+Self` profile name is about the target, not the policy.** Reading `Manual+Self` as "every
  `$self` transition gets this" is the wrong conclusion and has cost real work twice — the selection
  lives in `PipelineProfileResolver`. `ForSelfTarget` is the mechanism; the resolver owns who gets it.
- **Only `$self` counts — never a literal target that happens to equal the current state.** Reading
  it as self killed the initial state's OnEntry entirely and turned retry into a no-op (start
  pre-positions via `InstanceCommandAppService`, `instance.ChangeState(initialState)`). Guarding the
  incidental cases one at a time was tried and is unsound — do not reintroduce the comparison.
- `ChangeStateStep` suppresses its state-change metric/log/span event on this path **only** (it is
  scoped to `SkipsStateLifecycle`, so a `$self` shared transition — which really does re-enter the
  state — still reports its state change). `Instance.ChangeState` separately suppresses
  `sub:state-changed` whenever previous == new, keyed on the states themselves rather than on
  the transition — and it only ARMS that notification; see the coalescing rule below.
- A parent with an open SubFlow correlation short-circuits earlier, at
  `HandleUpdateDataDataOnlyStep (21)` — data only, nothing else.

### Epilogue order: Auto before Schedule

**The epilogue order is Auto → Schedule.** When Auto selected a winner (`Directives.NextTransition`
is set), `ScheduleTransitionsStep` arms no timer — the old churn of "arm, then delete with
CancelScheduledJobs on the chain's next hop" was removed on purpose. If the hop chained with the
winner faults, the timers were never armed either (they would have been useless on a faulted
instance anyway).

## Contracts

| Input | Output | Invariants |
| --- | --- | --- |
| `WorkflowExecutionContext` | `TransitionExecutionContext` | Initial/fresh entry loads workflow and active instance; an uninterrupted inline hop reuses the previous workflow/instance. |
| Ordered `ITransitionStep` list | Mutated instance and directives | Steps execute by `LifecycleOrder`. |
| `PipelineDirectives` | Post-commit jobs, deferred events, next transition | Directives are consumed explicitly to avoid repeated work. |

The distributed status lock is held only around admission's status check-and-set. The pipeline body
and automatic chain run without a distributed lock lease. The Busy status is the ownership marker;
special admission kinds define how cancel, exit, timeout, updateData and owner re-entry behave.

## Automatic Continuations and Transaction Boundaries

Automatic continuations always run inline and are awaited. `InlineContinuationStrategy` creates an
identity/execution `WorkflowExecutionContext` for the next transition, and `TransitionPipeline`
creates a fresh `TransitionExecutionContext` for that hop. No Dapr Scheduler job is created for an
automatic continuation. For an async client request, only the initial accepted transition uses a
`flow.transition` job; that job awaits the whole uninterrupted auto-chain.

Inside one uninterrupted pipeline/UoW, later hops use the previous hop's tracked `Instance` and
resolved `Workflow` through `TransitionContextFactory.CreateFromPreloaded`. State/transition
resolution, policy validation, profile resolution and step execution still happen for every hop.
Temporary context state (`Directives`, `Items`, `Cache`) is rebuilt per hop.

A post-commit job is a hard reuse boundary. The runner commits and disposes the current scope,
executes the post-commit work, and starts any parent continuation as a new stage with a fresh
authoritative instance load. Subflow start and forward therefore never receive the old tracked
parent aggregate. See [Inline Auto-Chain Context Reuse](inline-chain-context-reuse.md) and
[Subflow Execution](subflow-execution.md).

## Failure Modes

- Validation failure prevents the pipeline from starting.
- Step exceptions are converted to pipeline failures.
- Unhandled pipeline errors mark the instance Faulted and add an incident when needed.
- Post-commit failure can fault the instance if it returns a fault request.
- Chain depth is capped to prevent infinite automatic transition loops.
- A process crash during an inline auto-chain rolls back the current UoW; the initial Dapr job is
  retried for async entry. There is no per-automatic-hop Scheduler checkpoint.

## Observability

The pipeline begins a logging scope with domain, flow, flow version, instance id,
instance key, state from/to, transition key, trigger type, chain depth, and profile.
The current trace is enriched with `vnext.chain.depth`, `vnext.pipeline.profile`, and
`vnext.chain.id`.

## Change Safety

- Add new steps with explicit `LifecycleOrder` gaps when possible.
- If a step can alter control flow, use `PipelineDirectives` rather than hidden state.
- Keep profile exclusions synchronized with tests in `PipelineExecutionProfileTests`.
- Use `CreateFromPreloaded` only inside the uninterrupted pipeline/UoW; fresh stages use
  `CreateAsync` and an authoritative load.
- Do not carry an EF-tracked instance across a post-commit, retry or subflow callback boundary.

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
- [Async Transition Execution Modes](async-transition-execution-modes.md) — initial async job routing and the always-inline auto-chain.
- [Inline Auto-Chain Context Reuse](inline-chain-context-reuse.md) — carried values, isolation and reload boundaries.
- [Subflow Execution](subflow-execution.md) — synchronous child calls and post-commit ownership handoff.
