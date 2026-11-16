# Status Locking — One Lock, at the Status Change

The runtime serializes transitions through the instance's Busy flag, not through a long-held
distributed lock. This page records the locking contract: which hop takes the single status lock and
on which key, the order of checks around it, what runs while it is held, which admission kinds flip
Busy at the accept, and why `updateData` takes no lock at all.

## Rules

- **The Busy flag is the mutex.** A distributed lock is taken *only* for the status check-and-set,
  for the milliseconds it takes. The pipeline body and its auto-chain run with no lease held.
- **Exactly one lock per request-handling hop, on `ctx.LockKey`** (`vnext:{domain}:{flow}:{id}`):
  - sync → `TransitionPipeline` admission (`ReserveAsync` / `TakeOverAsync`);
  - async accept → `ITransitionAdmissionService.AcceptAsync`, which acquires the lock once,
    performs the kind's flip and runs the duplicate-job guard + durable enqueue under it.
- Ordering on both paths: **fast-fail Busy check → validation → lock → flip → work → release.**
  Never hold a lock across context creation or schema/policy validation.
- The accept's `{LockKey}:enqueue` lock and `IReservedTransitionResolver`'s per-kind lock keys are
  **gone**. They came from the old whole-chain lock model; with a millisecond-scale status lock a
  reserved transition no longer needs its own key to get past a Busy main flow.
- **`AcceptAsync`'s callback runs with the lock HELD** — never call `ReserveAsync`/`TakeOverAsync`/
  `Release*` from inside it. `InstanceStatusLock` is a single-attempt,
  non-reentrant `TryAcquire`; the nested call would simply fail to acquire.
- The duplicate-active-job guard shares that critical section because its check-then-insert has
  **no DB constraint** behind it. A partial unique index cannot replace it: a `$self` auto loop and
  a re-armed scheduled transition both legitimately hold two active rows for the same
  `(InstanceId, JobType, SourceState, TransitionKey)` tuple.
- **cancel/exit/timeout flip Busy at the accept**, not in the pipeline. They stay exempt from the
  Busy 409, but they do change the status, so they take the same lock as everything else. The job
  re-enters `IsPreReserved` and `TransitionPipeline` skips the second `TakeOverAsync`.
- **updateData (`Unconditional`) takes NO lock and NO duplicate-job guard — on either path.** It is
  status-neutral (flip = None, nothing to serialize) and must accept parallel requests: N
  simultaneous updateData accepts share the same logical job identity yet are all legitimate, each
  carrying its own payload, so the guard's dedupe would *lose data* for this kind. Job id/name are
  unique per enqueue, so lock-free insert cannot collide; instance-data writes are serialized
  downstream by the per-instance write funnel. Before this exemption, N parallel notifiers
  (subprocess → parent `document-ready-update`) fought over the parent's status lock and every
  loser burned an error-boundary retry backoff for a lock that protected nothing.
- **A parked Busy continuation is claimed once per state.** Because updateData takes no lock, N
  parallel updateData requests that find an interaction wait parked Busy (no live owner) would all
  pass `HasLiveTransitionOwnerAsync` and all `TakeOverAsync` the same continuation — the automatic
  chain ran N times, notifying the parent and finalizing more than once, and a late copy wrote a stale
  `Status=B` over the settled state (measured on the contract domain: 14 of 20 contracts at
  concurrency 10). Before the takeover, `TransitionPipeline` now runs one set-based UPDATE on
  `Instances.ContinuationClaimedAt` (`Status='B' AND CurrentState=@state AND` no claim younger than
  `InstanceContinuationClaim.DefaultTtl`, 60 s). The winner runs the chain and clears the claim in a
  `finally` when the chain ends; a loser drops its continuation and logs `10182`. `ChangeStateStep`
  persists the new state at once, so a continuation that arrives after the chain moved on fails the
  `CurrentState` check instead of waiting out the TTL. A crashed owner's claim simply expires. A reserve
  from Active needs no claim (`ReserveAsync` already serializes it), and the claim deliberately does not
  stamp `ModifiedAt` — it is bookkeeping, the chain's own writes stamp it.
- There is **no duplicate transition-record guard** downstream. Duplicate *requests* are stopped
  only by the accept-time active-job guard; duplicate *hops* by the per-hop policy checks.

A forwardable transition on a parent with an active `S` SubFlow never reaches this lock on the parent:
the [transition proxy](../architecture/subflow-transition-proxy.md) forwards it and the **leaf** takes
the one status lock. (Async still pre-stamps the parent's `EffectiveStatus` outside the lock, in its own
UoW, and no longer reserves the chain.)

## Client disconnect (vnext#1258)

A client that disconnects (closed tab, client-side timeout) cancels the HTTP request token. Once a
request has committed its Busy reservation, that must not cancel the pipeline, the inline auto-chain or
the post-commit work, or the instance is left Busy with no owner.

- **Detach at the HTTP boundary only.** `InstanceController.RunDetachedFromClientAsync` wraps every
  action that owns Busy or runs a pipeline: Start, StartSub, SubflowForward, PATCH Transition,
  ScheduleStart, `instances/events` (event start / transition), Retry, long-poll ack, `child-cancel`, and
  the SubFlow relay callbacks `complete`, `sub/state`, `sub/fault` and `sub/cancel` (each runs the
  parent's resume pipeline and inline chain). It runs the entry on a *run token*
  (`IRunCancellation.Begin`) instead of the request token. Work done before the run — such as reading
  the event body — still uses the request token: nothing is committed yet. `InstanceCommandAppService`
  uses the token it is handed, so in-process callers (`LocalInstanceCommandGateway`, `EventAppService`,
  `InstanceScheduleAppService`, a background `TransitionJob`) keep their own token and budget.
- **What cancels the run token:** host shutdown (`ApplicationStopping`) and the
  `WorkflowExecution:TransitionJobTimeoutSeconds` budget (default 300 s). A client abort does not.
- **Nested entries would share the token.** Today only the controller calls `Begin`, once per request.
  As a defensive guarantee, a `Begin` inside an already-open lease reuses the outer run token and budget
  instead of starting a fresh one, so a future in-process caller cannot extend the budget by nesting.
- **Visibility.** On an abort the run continues and writes WorkflowLogs `10265`
  `TransitionContinuedAfterClientDisconnect` / `10266` `InstanceStartContinuedAfterClientDisconnect` /
  `10267` `RunContinuedAfterClientDisconnect` (event, retry, ack, child cancel, relay callbacks;
  Information) and tags the span `vnext.client.aborted=true`.
- **A retry while the run is live gets 409; a retry after it committed runs again.** When a caller
  that retries on failure (Dapr pub/sub redelivery, a gateway or client retrying a timed-out
  `sync=true` call, a parent relay retrying StartSub / SubflowForward / a relay callback) retries
  while the detached run still holds Busy, it gets `409 Busy` (`Instance:100031`; non-2xx, so the
  broker retries later). Once the run has committed and released Busy, nothing in admission
  recognises the retry as a duplicate, and it is executed again. What still protects it:
  - a start that carries an instance key returns the existing active instance (client calls) or
    `409` (`StrictIdempotency`: StartSub, SubFlow / SubProcess starts); a start **without a key**, or
    one whose first instance already completed, creates a second instance;
  - a retried transition or event transition is rejected if the transition is no longer valid from
    the resulting state; a self-loop or re-entrant transition runs twice.
  Before vnext#1258 the same retry found a stranded Busy and was refused forever. The exposure
  is tracked as known issue `detached-run-retry-can-duplicate`; a request idempotency key checked at
  admission is the planned fix.
- **Compensation has its own budget.** Releasing a reservation or chain (`ReleaseReservationAsync`,
  `ReleaseSubflowChainAsync`, the accept-time `CompensateUnderLockAsync`), `SubflowProxyService.RevertStampAsync`,
  the transition runner's failed-coordination fault + chain release and `MarkInstanceFaultedAsync`
  run on `CompensationCancellation` — a 15 s token independent of the caller's, so a cancelled request
  cannot skip the cleanup.
- **Child runs on its own budget.** StartSub / SubflowForward are called by a parent runtime over HTTP;
  an aborted parent call no longer cancels the child, which runs on a fresh budget. The
  budget ⊂ chain-lock-lease relation (`WorkflowExecutionOptionsValidator`) does not extend across that
  process hop.
- **Remaining gap (not fixed).** If the run token itself is cancelled (host shutdown or the 300 s
  budget) while the run owns Busy, the instance can still be stranded; recovery is only the reserved
  `cancel` / `exit`. Tracked as known issue `run-cancelled-by-shutdown-can-strand-busy`.

## Related

- [Workflow Execution Pipeline](../architecture/workflow-execution-pipeline.md) — admission and the Busy ownership marker.
- [SubFlow Transition Proxy](../architecture/subflow-transition-proxy.md) — a parent with an active SubFlow takes no lock and flips no Busy for a forwardable transition; only the leaf admits it. The old chain reserve is on the [LEGACY page](../architecture/subflow-chain-reserve.md).
- [Async Transition Execution Modes](../architecture/async-transition-execution-modes.md)
