# Subflow Execution

## Purpose

Subflows let one workflow instance start and coordinate another instance. The runtime supports
blocking SubFlow (`S`) and non-blocking SubProcess (`P`) relationships. Both create a distinct child
instance and an `InstanceCorrelation`; their parent-continuation behavior differs.

All runtime-generated child start, active-child forward, and child retry calls currently use
`sync=true`, regardless of the parent request mode or the authored `S`/`P` type. Here, synchronous
means the command awaits the child's current pipeline activation until it reaches a rest point; it
does not wait through future human input or external events.

## Start Flow

1. `HandleSubFlowStep` creates the correlation and queues `StartSubflowJob` as post-commit work.
2. `TransitionRunner` commits and disposes the parent stage.
3. `StartSubflowJobHandler` reloads the parent through `FindForSubflowStartAsync`, builds the mapping
   context, and enters a child trace lane.
4. `SubflowStarter` evaluates input mapping and calls `IInstanceCommandGateway.StartSubAsync` with
   `sync=true` and `StrictIdempotency=true`.
5. The routed gateway executes locally for the same domain or uses Dapr service invocation for a
   different domain.

The child receives fixed parent metadata in `ExtraProperties`: parent id/key/domain/flow/version,
parent state and transition, flow type, and root instance id. Input mapping may supply attributes,
key, tags, headers and route values; framework-owned parent/root headers replace mapping values.

## `S` and `P` Semantics

| Type | Parent behavior | Child terminal behavior |
| --- | --- | --- |
| SubFlow (`S`) | Parent remains Busy with an open correlation and hands continuation ownership to the child. | Completion applies output mapping, closes correlation, then resumes the parent from `ClearBusyOnResumeStep` (order 79). Fault/cancellation propagate through their terminal services. |
| SubProcess (`P`) | Parent may continue after the synchronous start call reaches the child's current rest point. | Correlation is completed and persisted; normal completion does not resume the parent pipeline. |

## Forwarding to an Active Child

When a parent receives a transition while it has an active SubFlow correlation,
`ForwardToActiveSubflowStep` queues `ForwardToSubflowJob`. After the parent stage commits,
`ForwardToSubflowJobHandler` creates a child `TransitionInput` with `sync=true` and invokes
`IInstanceCommandGateway.ForwardTransitionAsync`.

For cross-domain forwarding, the chain-reserve claim travels in the internal request body. It is
never accepted from a public header. Async acceptance may reserve the active chain before returning
202 so polling sees the leaf as Busy; the forwarded child call then claims that reservation.

## Completion and Parent Resume

Child terminal events use two delivery paths:

- the event is stored in the transactional outbox;
- after commit, `SubflowTerminalRelay` immediately invokes the parent command locally or through
  Dapr, while the Inbox handler remains a durable backup.

`ISubItemTerminalGuard` deduplicates the two paths. For blocking `S`, completion prepares output
mapping before the terminal lock where possible, reloads the authoritative parent/correlation under
the required scope, applies the mapping, closes the correlation, persists, and resumes the parent.
If resume fails, the correlation is reopened in a new UoW so the terminal event can be retried.

Terminal relay DTOs preserve the terminal event's `Sync` flag. Runtime-generated child starts are
synchronous, so their normal terminal chain remains synchronous; externally supplied terminal
commands still keep their explicit contract value.

## Retry

When retry descends from a faulted parent into its active child, `InstanceRetryAppService` builds
the child `RetryInstanceInput` with `Sync=true`. The same-domain/cross-domain routed retry gateway
then awaits the child's retry activation.

## Persistence and Fresh-Read Boundaries

Subflow post-commit work deliberately does not reuse the parent's pre-commit tracked aggregate.
The stage that created or forwarded the relationship has already committed and disposed its scope;
the handler reloads the narrow authoritative shape it needs.

A synchronous child can complete and resume the parent in another scope before the outer handler
returns. Where a response needs the settled parent status, the handler performs a slim no-tracking
read. Do not replace these reads with the old parent object: it may still say Busy.

## Failure and Idempotency

- Child start uses strict idempotency with a preassigned child instance id.
- Durable correlations identify parent, child, relationship type and terminal outcome.
- Terminal settlement uses a durable settled marker/guard to absorb relay/Inbox duplicates.
- Resume failure reopens the correlation and republishes/rearms terminal delivery subject to its
  retry cap.
- Chain reservation is compensated if forwarding cannot hand ownership to the child.
- Cross-domain calls must retain parent/root identity, correlation, lane and activation fields.

## Change Safety

- Keep start, forward and descended retry calls synchronous unless this runtime contract is changed
  together with polling, ownership and terminal propagation tests.
- Do not move child work back under the parent transition transaction or status lock.
- Do not carry an EF-tracked parent across the post-commit boundary.
- Keep `S` resume behavior separate from `P` fire-and-forget completion behavior.
- Preserve terminal-event dual delivery and idempotent settlement.
- Treat the subflow completion window as a normal state: child may be terminal while parent
  correlation is still open.

## References

- `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/Steps/HandleSubFlowStep.cs`
- `src/BBT.Workflow.Application/Execution/PostCommit/Handlers/StartSubflowJobHandler.cs`
- `src/BBT.Workflow.Application/SubFlow/Services/SubflowStarter.cs`
- `src/BBT.Workflow.Application/Execution/PostCommit/Handlers/ForwardToSubflowJobHandler.cs`
- `src/BBT.Workflow.Application/SubFlow/Services/SubflowCompletionService.cs`
- `src/BBT.Workflow.Application/SubFlow/Services/SubflowTerminalRelay.cs`
- `src/BBT.Workflow.Application/Instances/InstanceRetryAppService.cs`
- [Event Publish Modes](../runtime/event-publish-modes.md)
- [Inline Auto-Chain Context Reuse](inline-chain-context-reuse.md)
