# SubItem Terminal Propagation Design

**Date:** 2026-07-16

**Status:** Approved for implementation planning

## Problem

SubFlow and SubProcess instances retain parent metadata and a parent-side
`InstanceCorrelation`, but their terminal behavior is asymmetric:

- completion notifies the parent for both SubFlow and SubProcess;
- fault notifies the parent only for blocking SubFlow instances;
- cancel never notifies the parent;
- a parent-originated cancel already propagates downward to every active correlation, so an
  unconditional upward cancel notification would bounce back into the terminating parent;
- lifecycle hooks currently may update the parent before the child transaction commits and may
  suppress the durable outbox message after a successful hook.

Consequently, faulted SubProcess correlations and directly canceled SubFlow/SubProcess
correlations can remain open. A blocking parent can remain Busy after its child is canceled, and
terminal propagation does not have a durable retry boundary in every execution path.

## Goals

1. Close parent correlations for Completed, Faulted, and Canceled child outcomes.
2. Preserve the current incident and error-boundary behavior for blocking SubFlow faults.
3. Treat blocking SubFlow cancel as a non-error terminal outcome: close the correlation and resume
   the parent pipeline without child output mapping or incident creation.
4. Treat SubProcess fault and cancel as observational outcomes only: close the correlation without
   changing or resuming the parent.
5. Prevent upward notification loops when fault or cancel originated from a parent cascade.
6. Persist each terminal event durably with the child state change and execute any synchronous fast
   path only after the child commit.
7. Make hook and inbox delivery idempotent under duplicate, delayed, and out-of-order messages.
8. Preserve local and cross-domain routing through `IInstanceCommandGateway` and the inbox worker.

## Non-goals

- A SubProcess fault or cancel does not fault, cancel, resume, or mutate parent instance data.
- A canceled SubFlow does not execute output mapping and does not create a parent incident.
- Parent fault continues to cascade only into blocking SubFlow children. Fire-and-forget
  SubProcess children are not newly faulted by this work.
- This design does not add distributed exactly-once delivery. It provides at-least-once delivery
  with idempotent convergence.
- Existing completion, fault error-boundary, retry, and sync caller semantics are not replaced.

## Domain terminology

### Terminal outcome

`InstanceCorrelation` gains a nullable terminal outcome:

```csharp
public enum SubItemTerminalOutcome
{
    Completed = 1,
    Faulted = 2,
    Canceled = 3
}
```

Legacy rows remain valid with `TerminalOutcome = null`. New terminal operations set both
`IsCompleted = true` and the corresponding outcome. Reverting a correlation clears
`IsCompleted`, `CompletedAt`, and `TerminalOutcome`.

The first committed terminal outcome wins. Repeating the same outcome is an idempotent success.
Receiving a different outcome after termination is a terminal conflict: it is logged and ignored.

### Termination context

Fault and cancel operations carry a common context:

```csharp
public enum TerminationOrigin
{
    Direct = 1,
    ParentCascade = 2
}

public sealed record TerminationContext(
    TerminationOrigin Origin,
    Guid InitiatorInstanceId,
    Guid CascadeId);
```

The default for API, timeout, reaper, and normal pipeline termination is `Direct`, with the current
instance as initiator and a newly generated cascade ID. Downward child propagation changes the
origin to `ParentCascade`, retains the original initiator, and retains the same cascade ID at every
depth.

`TerminationContext` is transported as typed transition/application data. Headers may mirror the
values for telemetry, but business behavior must not depend on spoofable free-form headers alone.

## Event contracts

### Fault

`InstanceSubFaultedEvent` remains backward compatible and is extended with:

- `SubItemType` (`SubFlow` or `SubProcess`);
- `TerminationOrigin`;
- `InitiatorInstanceId`;
- `CascadeId`.

Direct faults publish this event for every `IsSubItem` instance. Blocking SubFlow events retain the
current state, data, incident, trace, and sync fields. SubProcess fault events contain identity,
terminal state/time, type, and termination context; data and incident fields remain null.

An instance faulted with `Origin = ParentCascade` does not publish an upward fault event. It still
publishes its own cleanup event and propagates the same context to its active blocking SubFlow
children.

### Cancel

A new `InstanceSubCanceledEvent` is introduced. It contains:

- parent instance ID, domain, flow, and version;
- child instance ID and SubItem type;
- canceled state and canceled timestamp;
- root instance ID;
- termination origin, initiator instance ID, and cascade ID;
- sync caller flag when available.

It deliberately does not contain child data or incident information.

A directly canceled SubItem publishes `InstanceSubCanceledEvent`. A child canceled with
`Origin = ParentCascade` does not publish the upward event.

### Downward cascade events

`ChildSubflowCancelRequestedEvent` and `ChildSubflowFaultRequestedEvent` carry the termination
context. The child cancellation/fault services pass that context into the child operation. Nested
children receive the same initiator and cascade ID.

## Parent terminal processing

Parent processing uses the stored `InstanceCorrelation.SubFlowType`; it never trusts only the event
payload when deciding whether to resume the pipeline.

### Common phase

Within a transactional `RequiresNew` UOW:

1. Load the parent with the addressed correlation.
2. Return success when the parent is terminal.
3. Return success when the correlation already has the same terminal outcome.
4. Log and return success when the correlation has a different terminal outcome.
5. Update the child terminal state and timestamp where supplied.
6. Complete the correlation with the requested terminal outcome.
7. Restore the parent effective state to its own current state for blocking SubFlow correlations.
8. Persist and commit.

### SubProcess

For Completed, Faulted, or Canceled SubProcess outcomes, processing ends after the common phase.
Parent status, data, incident state, error boundaries, and pipeline execution remain unchanged.

### Blocking SubFlow completion

Existing completion behavior remains in place: apply output mapping, complete the correlation, and
resume the parent pipeline.

### Blocking SubFlow fault

Existing behavior remains in place:

- copy child incident context into a parent incident;
- resolve and execute the parent's SubFlow error boundary;
- run output mapping with incident context when currently supported;
- fault, transition, or resume the parent according to the boundary result;
- revert the correlation if post-commit transition/resume fails.

### Blocking SubFlow cancel

Cancel is not an error and is not completion output:

- do not create an incident;
- do not execute an error boundary;
- do not execute child output mapping;
- commit the correlation as `Canceled`;
- resume the parent from `LifecycleOrder.ClearBusyOnResumeStep` with
  `IsSubFlowResume = true` and the canceled child ID;
- let the parent's automatic transition evaluation choose the next path.

If the resume fails with a non-soft error, reload the parent with all correlations in a new UOW,
revert the addressed correlation, and fail processing so the durable event can be retried. Existing
soft results such as `AutoTransitionConditionNotMet` and `InstanceCompleted` retain their current
semantics.

## Parent-originated cascade behavior

When a parent instance is canceled:

1. Mark every active correlation `Canceled` before emitting child cancel requests.
2. Create one termination context whose initiator is the parent and whose cascade ID is shared by
   all children.
3. Send `ChildSubflowCancelRequestedEvent` for every active SubFlow and SubProcess correlation.
4. Each child cancels itself with `Origin = ParentCascade` and propagates the same context downward.
5. No child sends an upward canceled event for that cascade.

The completed-correlation and terminal-parent guards remain as defense in depth. A delayed event
from an older direct operation cannot resume or otherwise mutate a parent that has already
terminated its correlation during a cancel cascade.

Parent-originated blocking SubFlow fault propagation follows the same no-bounce rule. The parent
remains authoritative, and cascaded children do not report the induced fault back upward.

## Durable event and hook ordering

Terminal events use a durable post-commit hook mode:

1. The inner event bus always stages the terminal event in the current UOW's outbox.
2. A successful hook never suppresses that outbox record.
3. When an ambient UOW exists, hook execution is registered through `IUnitOfWork.OnCompleted`.
4. The hook therefore runs only after the child business transaction and outbox record commit.
5. If the hook fails, the committed outbox message remains available to the inbox worker.
6. If the hook succeeds, the inbox may later deliver the same event; parent processing treats it as
   an idempotent duplicate.
7. When no ambient UOW exists, the durable event must be persisted before executing the hook.

The existing handled-or-fallback hook behavior remains the default for unrelated events. Terminal
events opt into the durable post-commit mode explicitly so this work does not silently change every
hook in the system.

`TransitionRunner` must not swallow an error that means the terminal event could not be staged in
the outbox. A failed staging operation fails the transition commit path. Errors from the post-commit
fast hook do not roll back the already committed child state because inbox delivery is the durable
retry path.

## Idempotency and concurrency

The correlation is the authoritative idempotency record. No additional deduplication table is
required.

- Event identity is logically `(ParentInstanceId, SubInstanceId, TerminalOutcome)`.
- Same-outcome duplicates return success without invoking mapping, boundaries, or resume.
- A different outcome after termination logs a terminal conflict and preserves the first outcome.
- Parent terminal state wins over a late child notification.
- Parent cancel wins over a concurrent child fault once the parent/correlation cancel commits.
- Optimistic concurrency or a distributed instance lock protects simultaneous parent correlation
  updates. A concurrency loser retries by reloading the authoritative correlation.
- A reverted correlation can be processed again because its terminal outcome is cleared.

## Observability

Every terminal propagation log and activity includes:

- root instance ID;
- parent instance ID;
- child instance ID;
- SubItem type;
- terminal outcome;
- termination origin;
- initiator instance ID;
- cascade ID;
- domain, flow, and version.

Duplicate delivery logs at Debug. Terminal conflicts, correlation-not-found results, and failed
resume/revert operations log at Warning or Error with the same identifiers.

Persisting `TerminalOutcome` on `InstanceCorrelation` allows monitoring to distinguish successful,
faulted, and canceled child termination without copying SubProcess data or incidents.

## Database migration

Add a nullable terminal outcome column to `InstanceCorrelations`. Existing completed rows remain
null because their historical outcome cannot be inferred safely. All new completion, fault, and
cancel paths populate it.

No existing correlation rows are deleted or rewritten. Existing indexes remain valid; no new index
is required unless query profiling later demonstrates a terminal-outcome filter requirement.

## Testing strategy

### Domain tests

- direct SubFlow fault emits an upward event with incident/data;
- direct SubProcess fault emits an upward event without incident/data;
- direct SubFlow and SubProcess cancel emit an upward canceled event;
- parent-cascade fault and cancel emit no upward event;
- nested cascade preserves initiator and cascade ID;
- correlation records Completed, Faulted, and Canceled outcomes;
- same-outcome completion is idempotent and a different later outcome does not overwrite it.

### Parent service tests

- SubProcess fault/cancel closes correlation without parent status, mapping, boundary, or resume;
- blocking SubFlow cancel closes correlation and resumes the parent;
- blocking SubFlow cancel does not map data or create an incident;
- resume failure reverts the correlation in a new UOW;
- duplicate event is a successful no-op;
- terminal parent is a successful no-op;
- parent cancel racing child fault preserves the parent cancel result.

### Cascade tests

- parent cancel targets every active SubFlow and SubProcess correlation;
- parent-cascade children do not emit upward events;
- a three-level hierarchy propagates only downward with one cascade ID;
- duplicate cascade delivery does not execute a child cancel transition twice.

### Event durability tests

- child state and terminal outbox record share one transaction;
- hook does not execute before commit;
- hook success does not remove or suppress the outbox record;
- hook failure leaves inbox delivery able to update the parent;
- hook plus inbox duplicate resumes the parent at most once;
- child rollback executes no hook and produces no deliverable terminal event;
- outbox staging failure prevents a successful transition commit result.

### Regression tests

Existing tests for SubFlow completion/fault boundaries, caller-mode propagation, correlation
revert/retry, cancel preflight, child job cleanup, and local/remote gateway routing remain green.

## Rollout compatibility

- New event properties are additive.
- The new canceled event uses its own event name and handler.
- Legacy fault events without `SubItemType` are interpreted as blocking SubFlow events.
- Legacy correlations with null terminal outcome continue to rely on `IsCompleted`; they are not
  reopened automatically.
- Producers and consumers can be deployed in either order as long as consumers accept missing
  additive fields and old producers retain the existing fault/completion contracts.
