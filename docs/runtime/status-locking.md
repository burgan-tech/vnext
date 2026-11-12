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

## Related

- [Workflow Execution Pipeline](../architecture/workflow-execution-pipeline.md) — admission and the Busy ownership marker.
- [SubFlow Transition Proxy](../architecture/subflow-transition-proxy.md) — a parent with an active SubFlow takes no lock and flips no Busy for a forwardable transition; only the leaf admits it. The old chain reserve is on the [LEGACY page](../architecture/subflow-chain-reserve.md).
- [Async Transition Execution Modes](../architecture/async-transition-execution-modes.md)
