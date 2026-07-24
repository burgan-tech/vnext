# Cross-Domain Sync Subflow Post-Commit Lock Handoff Design

**Date:** 2026-07-23

**Status:** Proposed, design approved in principle

**Scope:** Synchronous SubFlow/SubProcess start and forward operations, including Completed, Faulted, and Canceled terminal propagation

## Problem

`TransitionPipeline` currently holds the parent instance transition lock while it executes
`StartSubflowJob` and `ForwardToSubflowJob`. A synchronous child can reach a terminal state before
that post-commit call returns. Its terminal event then calls the parent through
`SubflowCompletionService`, `SubflowFaultService`, or `SubflowCancellationService`.

For a same-domain call, `ChainLockRegistry` can recognize the parent lock through `AsyncLocal` and
allow a chain-reentrant acquisition. For a cross-domain call, the callback arrives through a new
HTTP/Dapr request and a new execution context. The registry cannot cross that process boundary, so
the callback tries to acquire the parent lock normally and fails because the original parent request
still owns it.

The durable outbox/inbox fallback may eventually process the terminal event after the parent lock is
released, but that changes a `sync=true` request into eventual completion. This violates the required
contract: a synchronous request must not return before the synchronous child work and the resulting
parent processing have settled.

## Constraints

- `sync=true` remains synchronous across same-domain and cross-domain calls.
- `IDistributedLockService` and `IDistributedLockHandle` remain unchanged.
- The solution must not depend on a concrete lock provider or provider-specific ownership APIs.
- Completion, fault, and cancellation must continue to serialize parent mutations through the normal
  parent instance lock.
- Existing outbox/inbox delivery remains the durable at-least-once fallback and duplicate delivery is
  handled by correlation terminal-state idempotency.
- SubFlow and SubProcess semantics remain distinct.

## Domain Semantics

### Blocking SubFlow

A SubFlow blocks its parent. When it completes or is canceled, the terminal service closes the
correlation and resumes the parent pipeline. When it faults, the terminal service closes the
correlation and executes the parent's error-boundary result, which can transition, resume, or fault
the parent.

For `sync=true`, this parent processing must finish before the child request returns to the original
parent call.

### Independent SubProcess

A SubProcess is an independent parallel flow. Starting it does not transfer parent continuation
ownership and does not block the parent pipeline.

Completed and Faulted notifications are still propagated to the parent, but they only close the
stored correlation and record its terminal outcome. They never resume the parent pipeline. A
SubProcess terminal callback therefore acquires the parent lock, commits the correlation change, and
returns.

After a synchronous SubProcess start call returns, the original parent continuation proceeds in its
own new lock/UoW execution.

## Decision

Move subflow post-commit execution out of `TransitionPipeline`'s lock scope and execute it from the
runner only after the transition UoW has committed. This is a lock handoff, not lock re-entrancy:

1. The parent transition acquires the normal instance lock.
2. Pipeline steps persist the parent state and correlation and produce an immutable continuation
   snapshot containing post-commit jobs and any next transition.
3. The pipeline returns without executing or consuming those jobs. Returning disposes the transition
   lock and ends the corresponding `ChainLockRegistry` visibility.
4. `TransitionRunner` stages deferred events and commits the parent transition UoW.
5. The runner executes post-commit jobs without the parent lock.
6. A synchronous child terminal callback acquires the parent lock normally, processes its terminal
   outcome, and releases the lock before returning.
7. The runner realizes any remaining parent continuation according to SubFlow/SubProcess semantics
   and builds the response from authoritative state.

This ordering works with every `IDistributedLockService` implementation because it never borrows or
re-enters a remotely owned lock.

## Orchestration Boundary

`TransitionPipeline` remains responsible for:

- context creation and validation;
- instance lock acquisition;
- executing transition lifecycle steps;
- collecting directives, deferred events, post-commit jobs, and next-transition intent;
- returning an execution result without performing external post-commit work.

`TransitionRunner` (or a runner-owned post-commit coordinator) becomes responsible for:

- staging deferred events;
- committing the transition UoW;
- executing post-commit jobs after commit and after lock release;
- applying the post-commit failure policy through a fresh lock and UoW;
- realizing a pending parent continuation in a new execution scope when required;
- producing the final synchronous response after post-commit work has settled.

`PostCommitExecutor` continues to dispatch handlers in an isolated DI scope and preserve existing
idempotency behavior. It must not assume that the transition lock is held.

## Control Flow

### Blocking SubFlow Start or Forward

1. The parent transition reaches a blocking SubFlow state or forwards a transition to an active
   SubFlow.
2. The pipeline persists/finalizes its local transition and returns a post-commit job with terminal
   outer-pipeline intent.
3. The pipeline return has already released the parent lock; the runner commits the parent UoW before
   invoking the child.
4. The child executes synchronously.
5. If the child completes, faults, or is canceled, its terminal callback acquires the parent lock and
   performs the corresponding parent terminal service.
6. Blocking SubFlow completion/cancellation resumes the parent; fault follows the resolved parent
   error-boundary action.
7. The child call returns only after that callback returns.
8. The original runner does not continue from its stale parent entity. It reads authoritative parent
   status and returns the settled response.

If the child remains non-terminal, the parent remains Busy with an open blocking correlation and the
synchronous request returns that authoritative waiting state only after the child start/forward call
has completed.

### Independent SubProcess Start

1. The parent transition creates and persists the SubProcess correlation.
2. The runner commits and starts the SubProcess outside the parent lock.
3. If the SubProcess reaches Completed or Faulted during the synchronous call, its callback acquires
   the parent lock and closes only the correlation. It never resumes the parent.
4. After the start call returns, any pending parent auto-chain continuation is executed through a new
   runner invocation, UoW, and parent lock.
5. Parent continuation never uses the pre-handoff tracked entity as authoritative state.

If the SubProcess remains active, the parent continues independently. Its later Completed/Faulted
event closes the correlation through the same normal-lock path.

## Fresh-State Rule

Once the parent lock has been handed off, the original `TransitionExecutionContext.Instance` is a
snapshot and must not be used for further writes.

- Blocking SubFlow paths return from the outer pipeline and obtain response status from a fresh,
  no-tracking parent read.
- SubProcess parent continuation is reconstructed from persisted data in a new scope.
- No resolved status, incident, chain ownership, or continuation mutation is applied to the stale
  entity after post-commit execution.

This rule is the primary defense against overwriting changes made by a synchronous terminal callback.

## Post-Commit Failure Handling

The existing post-commit failure path assumes the parent lock is still held and mutates the tracked
context entity. That assumption becomes invalid.

When the failure policy requests a parent fault:

1. Acquire the normal parent instance lock through `ITransitionLockScopeFactory`.
2. Start a fresh `RequiresNew` UoW.
3. Reload the authoritative parent instance.
4. Re-check terminal/current state so a successful terminal callback is not overwritten.
5. Add the incident and fault the instance only when the policy still applies.
6. Persist, commit, and release the lock.

Transient acquisition failure is returned as a lock conflict for the existing retry/recovery path;
the implementation must not write without a held lock.

## ChainLockRegistry

`ChainLockRegistry` is not used to bridge post-commit callbacks. Because post-commit execution begins
after `TransitionPipeline.RunAsync` returns, its `AsyncLocal` registration is no longer visible and
the real transition lock has already been disposed.

The registry may remain for other same-process nested lock cases. Removing it is not part of this
change. Existing re-entrant tests remain valid for those cases, while pipeline tests must no longer
expect the parent key to be held during subflow post-commit execution.

## Expected Production Changes

Primary change surface:

- `TransitionPipeline`: stop consuming/executing post-commit jobs inside the lock and expose the
  continuation barrier.
- `TransitionRunner`: commit, execute post-commit work, realize continuation, then build the final
  response.
- `WorkflowExecutionService` / `TransitionCoreOutput`: preserve the execution/continuation data
  needed by the runner until post-commit orchestration finishes.
- `PostCommitExecutor`: remove any lock-held assumption; accept a stable execution snapshot if needed.
- Post-commit fault handling: move to fresh lock/UoW/reload semantics.
- Start/forward handlers: use fresh reads for response status and avoid treating the old tracked
  parent entity as authoritative after handoff.

No behavioral rewrite is expected in:

- `SubflowCompletionService`;
- `SubflowFaultService`;
- `SubflowCancellationService`;
- terminal event hooks;
- local/remote command gateways;
- `IDistributedLockService` or its providers.

Small signature changes may still be needed to pass immutable post-commit execution data, but the
terminal services retain their current lock and idempotency responsibilities.

## Testing

### Ordering and lock lifecycle

- Parent lock is held while pipeline steps execute.
- Parent lock is released before `StartSubflowJob` and `ForwardToSubflowJob` handlers run.
- Post-commit jobs do not run before the parent UoW commit succeeds.
- A failed parent commit prevents post-commit execution.
- `ChainLockRegistry.IsHeld(parentKey)` is false during post-commit execution.

### Cross-domain synchronous terminal propagation

For each terminal outcome, use a test double that models an HTTP/process boundary and therefore does
not inherit `AsyncLocal` state:

- Completed callback acquires the parent lock and synchronously settles a blocking SubFlow parent.
- Faulted callback acquires the parent lock and synchronously executes the configured boundary result.
- Canceled callback acquires the parent lock and synchronously resumes a blocking SubFlow parent.
- The original `sync=true` response is produced only after the parent callback and resulting pipeline
  work finish.

### SubProcess

- Completed and Faulted callbacks close the correlation without invoking parent resume.
- Parent continuation executes after SubProcess start in a fresh lock/UoW.
- A synchronous terminal callback followed by parent continuation reloads authoritative state and
  preserves the terminal correlation.
- A later at-least-once duplicate is a no-op through the stored terminal outcome guard.

### Failure and regression coverage

- Post-commit failure faults the parent through fresh lock/UoW/reload.
- A concurrent successful terminal callback is not overwritten by post-commit failure recovery.
- Same-domain synchronous SubFlow behavior remains synchronous.
- Asynchronous execution and Inbox retries preserve existing eventual/idempotent behavior.
- Nested blocking SubFlows release each parent lock before invoking the next child.
- Existing auto-chain, transition-per-job, reserved transition, and chain-reaper tests remain green.

## Acceptance Criteria

- No synchronous cross-domain Completed, Faulted, or Canceled callback fails because the originating
  parent pipeline still holds the parent instance lock.
- `sync=true` does not return before synchronous child and required blocking-parent processing settle.
- SubProcess terminal events never resume the parent and only close their correlation.
- SubProcess parent continuation remains independent and runs under a fresh lock/UoW.
- No post-handoff write uses the original tracked parent entity.
- `IDistributedLockService` remains unchanged and no concrete provider dependency is introduced.
- Post-commit failure processing is serialized through the normal parent lock.

## Non-Goals

- Introducing distributed lock ownership delegation or fencing tokens.
- Changing terminal event at-least-once delivery.
- Removing `ChainLockRegistry` globally.
- Changing SubFlow/SubProcess domain behavior beyond correcting lock and post-commit ordering.
- Redesigning unrelated continuation or background-job processing.
