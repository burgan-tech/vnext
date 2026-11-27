# Event Publish Modes: Outbox-Only Events, Post-Commit Relay, Wakeup Signal

## Purpose

Every distributed event in the runtime used to be intercepted by `HookedDistributedEventBus`,
which ran a per-event hook either inline pre-commit or inside `uow.OnCompleted` (blocking the
commit), then wrote an outbox row on top for durability. The EventHook infrastructure has been
deleted outright. All distributed events now ride the transactional outbox uniformly; events that
opt in additionally get an immediate post-commit **relay** — a direct command call, not an event —
so the receiver keeps a near-zero gap on both sync and async paths. Aether gained a wakeup signal so
the outbox/inbox poll loop no longer needs to wait out its idle interval on the common path.

This page describes the resulting publish-mode taxonomy, the relay's semantics and independence
guarantees, the wakeup mechanism, and the accepted risks. It replaces the old hook model as the
canonical reference for how a distributed event gets from "committed" to "handled."

## Publish-mode taxonomy

| Mode | Declared by | Behavior |
|---|---|---|
| **Outbox** (default, ALL events) | nothing | transactional outbox row → wakeup nudge → Outbox worker publish → broker → Inbox worker (in-process nudge) → Inbox handler |
| **Outbox + PostCommitRelay** | a `IPostCommitEventRelay<TEvent>` REGISTRATION in DI | everything above PLUS: the runner relays the event as a **command** immediately after commit via `PostCommitRelayDispatcher` → `IInstanceCommandGateway` (routed in-process or via Dapr service invocation); the Inbox handler is demoted to a durable backup (within one domain; see Failure semantics), deduplicated by the receiver's own guard |

There is no third mode. Nothing publishes synchronously inline anymore — `TraceStampingDistributedEventBus`
(the renamed, shrunk `HookedDistributedEventBus`) only stamps trace context and delegates to the
outbox; it no longer knows about hooks, `EventHookMode`, or per-event dispatch.

## Event classification

| Event | Mode |
|---|---|
| `InstanceSubCompletedEvent`, `InstanceSubFaultedEvent`, `InstanceSubCanceledEvent` | Outbox + PostCommitRelay (the three share terminal-settlement semantics; each has its own relay class over a common base). The fault relay serves a parent in **another domain** only; a parent hosted here keeps the Inbox delivery — see [below](#a-fault-is-relayed-to-a-parent-in-another-domain-by-the-code-that-commits-it). The Inbox backup exists only for a parent this runtime hosts: **across domains none of the three has a durable backup** ([Failure semantics](#relay-semantics)) |
| `InstanceSubStateChangedEvent` | Outbox + PostCommitRelay (receiver guard = the per-sub-item lock plus the monotonic `SubFlowStateChangedAt` stamp in `SubflowStateService`) |
| `InstanceCanceledEvent`, `InstanceCompletedCleanupEvent`, `InstanceFaultedCleanupEvent`, `ChildSubflow*`, `TransitionContinuationRequested` | Pure Outbox |

**The DI registration is the opt-in — there is no marker interface and no central switch.** Adding
the relay mode to a new event is one relay class plus one
`AddScoped<IPostCommitEventRelay<TEvent>, …>()` line in `AddPipelineServices`; removing that line
is that event's kill switch. `ISubflowTerminalEvent` still exists, but only as the shape the three
terminal relays read their route and span tags from — implementing it opts nothing in.

Registration alone is not sufficient grounds. Per the standing Chair precedent, each relayed event
also needs a durable backup, an idempotent order-safe receiver guard, measured latency evidence and
a council row; the default for a new event stays Outbox-only.

**Open deviation from that precedent: the cross-domain leg.** A terminal event relayed to a parent in
another domain has no durable backup, because the child domain's Inbox drops an event whose parent it
does not host. For the completion relay this is the recorded gap C3 of
[Sub-Endpoint Gate § Failure modes](sub-endpoint-gate.md#failure-modes) (council condition C-5: a
durable cross-domain retry is a separate issue). The fault relay added for vnext#1256 serves *only* that
leg, so it has no backup at all; its council (`2026-11-23-sub-fault-relay-1256`) left this to the Chair
as an explicit acceptance (condition M-3) until the durable cross-domain backup exists (vnext#1357). Until that
acceptance is recorded, the fault relay does not meet the precedent — it is not a model for a new relay.

## Relay semantics

- The runner (`TransitionRunner`), after `uow.CommitAsync` succeeds, calls
  `PostCommitRelayDispatcher.RelayAsync(coreOutput.DeferredEvents, ct)`. The dispatcher resolves
  `IPostCommitEventRelay<TEvent>` for each event's RUNTIME type — the same open-generic
  `MakeGenericType` + `GetService` shape `PostCommitExecutor` uses for `IPostCommitHandler<TJob>` —
  and processes envelopes **sequentially**. An event with no registration is skipped and travels the
  outbox alone.
- Per event: the relay's `Describe` supplies route and span metadata (read BEFORE the call so the
  span is complete even when it throws), then `RelayAsync` builds the gateway input — the exact
  mapping code that used to live in the event hooks, moved verbatim — and calls `CompleteAsync` /
  `FaultAsync` / `CancelAsync` / `UpdateSubFlowStateAsync` on `IInstanceCommandGateway`, which
  routes in-process for the same domain or via Dapr service invocation cross-domain
  (`RoutedInstanceCommandGateway`).
- **The cross-domain bound belongs to the relay, not the dispatcher.** A relay may declare a
  `RemoteTimeout` on its target; the dispatcher then bounds the REMOTE leg with a linked CTS and
  reports outcome `timeout`. The three terminal relays declare none and keep their historical
  behaviour under the gateway's own timeout. `InstanceSubStateChangedRelay` declares **2 s**: that
  channel carries roughly an order of magnitude more traffic than the three terminal channels
  combined, and unlike a terminal outcome a missed state change is corrected by the next one, so a
  slow remote must release the child's hop rather than hold it.
- `CallerMode` follows the event's own `Sync` flag (`evt.Sync ? ExecMode.Sync : ExecMode.Async`) —
  identical to what the hook did.
- **Sync chain stays sync end-to-end**: the relay is awaited before the stage returns, so a
  blocked caller's response follows the fully settled chain, exactly like the old hook. **Async
  chain relays immediately**, in the same job execution, right after commit.
- **Failure semantics**: relay exceptions (and gateway calls that return a failed `Result` without
  throwing) are logged and swallowed — the child is already committed as terminal, so the response
  must not lie about that. The outbox row, written unconditionally pre-commit, guarantees the
  Inbox backup picks the work up shortly after — **within one domain**. Topics are per publishing
  domain and a child domain's Inbox drops a terminal event whose parent is hosted elsewhere (the
  handlers' domain guard), so a failed **cross-domain** relay has no durable backup: the parent stays
  parked until the completion is re-driven ([Sub-Endpoint Gate § Failure modes](sub-endpoint-gate.md#failure-modes)).
  The same holds for a fault; there the Inbox drop is logged as a warning
  (`SubFlowFaultIgnoredDomainMismatch`, 40111), and a terminal relay that failed on a remote route
  logs `PostCommitTerminalRelayLost` (40142, Error). Relay calls are bounded by the gateway's existing
  invocation timeouts.
- Observability: each relay attempt opens a `PostCommit.EventRelay` activity (see
  [Observability contract](#observability-contract) below) and logs
  `PostCommitEventRelayed` (EventId 40124) on success, `PostCommitEventRelayFailed` (40125,
  exception path), `PostCommitEventRelayRejected` (40126, failed-`Result` path),
  `PostCommitEventRelayTimedOut` (40133) or `PostCommitEventRelayDepthExceeded` (40134). The three
  EventIds carried over from the terminal-only predecessor so existing dashboards keep resolving.

### A fault is relayed to a parent in another domain by the code that commits it

The runner relays `coreOutput.DeferredEvents`, and a fault's events are never in that list. A fault is
written in a unit of work of its own, on a freshly reloaded aggregate, so `Instance.Fault` raises the
upward `InstanceSubFaultedEvent` where no runner sees it. A client-facing failure (for example
`ResourceLockConflict`) also returns from the runner before its relay line.

Until vnext#1256 that event therefore travelled the outbox alone. Within one domain the Inbox backup
delivered it after the broker round trip, and still does. Across domains nothing delivered it: the
child domain's Inbox drops a terminal event whose parent is hosted elsewhere, and the parent stayed
Busy with no incident.

**Which parents are relayed to.** `InstanceSubFaultedRelay.Describe` declines (`SkipReason`
`parent_hosted_here`, log 40138) a parent this runtime hosts (`IRuntimeInfoProvider.IsDomainMatch`), so
only a parent in **another** domain gets the immediate relay. A parent hosted here keeps master's
path exactly: Inbox → `sub/fault`, `Sync = false`, a fresh run budget. Relaying it in-process would run
the parent's whole error-boundary chain inside the child's request, on whatever is left of the
child's run budget (15 s on the compensation paths), partly on the child's still-open unit of work —
the council session `2026-11-23-sub-fault-relay-1256` rejected that for this fix. The cost is that
fault delivery depends on topology: under `APP_DOMAINS` a co-hosted parent is "hosted here" and goes
through the Inbox. Completion and cancel are not affected; their relays still serve a local parent.

Each place that commits a direct fault hands the fault's events to `IPostCommitRelayDispatcher`
itself, the same way `SubflowStateService` does for the state channel:

| Call site | When it faults the instance |
|---|---|
| `TransitionPipeline.MarkInstanceFaultedAsync` | A pipeline step failed and no error boundary rerouted it; a `history: none` stage that cannot continue or rests in a non-Finish state. |
| `PostCommitParentMutationService.FaultAsync` | Post-commit coordination failed (for example a SubFlow start the callee refused). |
| `JobTimeoutRecoveryService.FaultInstanceAsync` | A transition job timed out or was cancelled. |
| `SubflowFaultService` (no-boundary branch) | A parent faults while settling its own child's fault because no rule of its error boundary matched (Unhandled → Abort with no transition) — the default for a parent without an `errorBoundary`. Its own upward fault goes to the grandparent. |

Rules every one of them follows:

- **Snapshot before the save.** The `SaveChanges` sink drains and clears the aggregate's events, so
  the list is copied first (`instance.GetDomainEvents().ToList()`). The outbox rows stay the durable
  record; the copy only feeds the relay. The unit tests pin this with a save that drains the aggregate.
- **Relay after the commit, outside the lock.** The call runs once the fault's unit of work and the
  lock are released (for `SubflowFaultService`, also after the child's settlement marker). A fault
  that did not commit relays nothing.
- **Token.** The pipeline relays under the caller's token while it is live, and under a fresh
  `CompensationCancellation` budget (15 s) when the run faulted *because* that token was cancelled.
  `PostCommitParentMutationService.FaultAsync` relays under the token its caller passes: the run token
  for a policy fault (`TransitionRunner`), the compensation token for a start compensation, and
  `CancellationToken.None` from a failed retry restart (`InstanceRetryAppService`). The job-timeout
  recovery relays under a fresh compensation budget (its own token is `None` on purpose).
  `SubflowFaultService` relays under the token of the delivery that settled the child. Since only a
  remote parent is relayed to, the token bounds the call to the other domain; the parent settles there
  on its own run budget.
- **Only registered events move.** The whole snapshot is handed over; `InstanceFaultedCleanupEvent`
  and `ChildSubflowFaultRequestedEvent` have no relay and keep travelling the outbox alone.

The receiver is unchanged: `SubflowFaultService` takes the per-sub-item lock and
`ISubItemTerminalGuard` absorbs a second delivery.

**A child's fault wins over its parent's start compensation — once that fault's settlement has
finished.** A child that faults in its own start chain with a client-facing code relays its fault to a
parent this runtime does not host (the parent's error boundary runs) and then answers the parent's start
call with a failure. The parent's start compensation then reaches
`PostCommitParentMutationService.FaultAsync`, whose only guard used to be `IsCompleted`: a parent its
boundary had routed to a non-terminal state was faulted there with a second incident. The fault request
now names the correlation of the failed **SubFlow** start
(`PostCommitFaultRequest.StartedSubflowCorrelationId`, set by `TransitionRunner` for a single
HandoffToChild `StartSubflowJob` and by a failed retry restart). The compensation steps aside (log 10268)
only when that correlation is read by id and found **closed, of type `S`, with outcome `Faulted`, on this
parent, and marked settled (`SettledAt`)**. The marker is what proves the boundary ran:
`SubflowFaultService` commits the correlation closed *before* the parent's boundary transition runs and
writes `SettledAt` only after that transition succeeded (a failed one reverts the correlation) — the same
proof `ISubItemTerminalGuard` asks for. Every other case faults the parent exactly as before: a
correlation still open (the child never came to exist or has not reported), a missing one
(`SubFlowCorrelationNotFound`), one closed by a completion or a cancel, a SubProcess start (its inline
continuation can fail after a successful start), and one closed but not yet marked settled. That last case
is a settlement still running, or one whose best-effort marker write was lost: the compensation then
faults a parent its boundary had already routed — visible and retryable, and what master did every time.
The settlement reload stays open-correlations-only; the closed row is a separate, untracked read by id
(`IInstanceCorrelationRepository.FindByIdAsReadOnlyAsync`), so it is never attached to the tracked parent.

**When one runtime hosts both parent and child, the compensation normally wins, as on master.** The
child's fault is not relayed to a parent hosted here (the same domain, or one co-hosted through
`APP_DOMAINS`); it goes through the Inbox and usually arrives after the start compensation has already
faulted the parent, where it stops at `parent_already_terminal`. That order is a race, not a guarantee:
the compensation holds the parent's status lock and the settlement the per-sub-item lock, so an Inbox
delivery that finishes first makes the compensation step aside by the same rule. This ownership rule
therefore depends on topology and on timing: the same pair of flows can behave differently deployed apart
and co-hosted. The race exists on master; the guard narrows it.

**Failure has no backup across domains.** A cross-domain relay is attempted once. If it fails or times
out, the parent stays Busy: log 40125/40126/40133 (with the sub and parent ids and the route) and,
for a terminal event on a remote route, **`PostCommitTerminalRelayLost` (40142, Error)** — the alert
signal for a stuck cross-domain parent. The child domain's Inbox logs 40111 for every such fault,
whether or not the relay succeeded; a 40111 with no 40124 for the same sub id is a lost delivery (also
after a process death between the fault commit and the relay, which logs nothing on the relay side).
Recovery is manual: re-post the fault from the child domain's outbox row to the parent's
`POST {domain}/workflows/{flow}/instances/{parentId}/sub/fault` endpoint; the receiver is idempotent
(a second re-post is a no-op). The row is in the child domain's `sys_queues."OutboxMessages"`
(`"EventName" = 'instance.sub.faulted'`, `"EventData"` is the CloudEvent as UTF-8 bytes). Its `data`
object is the request body with two differences, the same mapping the Inbox handler applies: the
three fields `terminationOrigin`, `initiatorInstanceId` and `cascadeId` move into one
`termination: { origin, initiatorInstanceId, cascadeId }` object, and `sync` is set to `false`. A
durable cross-domain backup is a follow-up (vnext#1357). Note also that the child's own sync request waits for the
relay: a parent domain that does not answer holds it until the gateway's invocation timeout (60 s
measured in the lab).

**Not covered.** Three fault sites still publish their upward `InstanceSubFaultedEvent` to the outbox
only: `SubflowFaultService` when the parent's definition cannot be loaded, and
`SubflowCompletionService` when the parent's definition cannot be loaded or its output mapping fails.
In a chain whose upper two levels live in different domains, that grandparent is not told. They commit
and return inside the per-sub-item lock and need their own restructuring.

### `sub:state-changed` is emitted once per activation episode

`Instance.ChangeState` does not publish this event — it **arms** it. `TransitionSettlement` publishes
it at the activation episode's **rest point** (`Instance.PublishPendingSubStateChange`), inside the
pipeline's unit of work, so the event and the state it describes commit together.

An inline auto-chain crossing A→B→C→D is one episode with one observable outcome. The parent can act
on nothing in between, because the chain has not stopped. Per-hop publishing made this the runtime's
highest-volume signal — 6 facts per 908 ms chain against a ~1.3 s delivery, with 7.5 % of 42 803
deliveries writing nothing at the receiver — and it moved the parent's state-function ETag on every
hop, waking long-pollers for states they could not use (`EffectiveState` and
`LastSubFlowStateChangedAt` are both `InstanceStateFingerprint` members).

**The value is unchanged, only the count:** the old burst's last event carried the final
`CurrentState`, and so does the single coalesced event.

Rest points that publish: became Active, reached a finish state, or deliberately rests Busy — a
parked auto-gate (`BusyParked`) or a Busy-subtype state. Two exclusions: an **open SubFlow
correlation** (the parent is Busy for the child's lifetime and the state the client observes is the
child's, so the parent's own move into the SubFlow state is superseded by the child's notification
travelling up) and **Faulted** (`InstanceSubFaultedEvent` already carries the faulted state upward;
this channel reports progression, not failure).

A notification reporting the child's terminal **`Completed`** status (finish state, cancel or exit) is
still published — the outbox keeps it as a fact for `instance.sub.*` subscribers — but it is **not
applied**: the relay declines it (`skipped`, reason `terminal_superseded`) and `SubflowStateService`
drops it on either delivery path before any read or lock. The terminal event raised in the same
commit owns that transition. `HandleFinishStep` (100) defers it before `TransitionSettlement` adds
the state change, the dispatcher relays in order, and its settlement records the child's final state
on the correlation, closes it and re-derives the parent's projection — moving every parent
fingerprint member the long-poll ETag reads. So the state change only ever arrived after that, to an
already-closed correlation (`Correlation not found`, no write), or — when the terminal relay failed
or the backup won the race — projected a finished child onto a parent about to resume, and on a
nested chain carried it to the grandparent, which the terminal settlement never corrects upward.
SubProcess children are unaffected: `PublishPendingSubStateChange` never publishes for them
(`IsSubFlow`). A notification without `NewStatus` (a publisher that predates the field) keeps the
old handling.

Instance **creation** flushes explicitly: it pre-positions the instance into its initial state and
commits in a unit of work that has no settlement. That is load-bearing when a child's start
transition targets its own initial state — nothing moves afterwards, so this is the parent's only
notification for that child.

Implementation notes for the coalescing:

- **`Instance.ChangeState` does not publish. It arms.** The event is published at the episode's
  **rest point** by `Instance.PublishPendingSubStateChange()`, called from
  `TransitionSettlement.ApplyAsync` inside the pipeline's unit of work — so the event and the state
  it describes commit together.
- **The value is unchanged, only the count.** The old burst's LAST event carried the final
  `CurrentState`; the single coalesced event carries the same one. `PreviousState` is now the state
  the episode STARTED in (nothing reads it — pinned as unread by the parent-notification council).
- **Rest point ⊋ Active.** `ShouldPublishSubState` publishes on `chainSettled && OwnsStatus`, with
  two exclusions. It is deliberately broader than the activation verdict: a lost CAS or an
  already-Active owner yields no verdict, but the state that hop wrote is real and the parent must
  hear it. Parked Busy (`BusyParked`, an unmet auto-gate) and `BusySubtype` **do** publish — they are
  rest points, the instance is sitting there waiting for input.
- **Creation flushes explicitly.** `InstanceCommandAppService` pre-positions a new instance into its
  initial state and commits in a unit of work with no settlement, so it calls
  `PublishPendingSubStateChange()` itself. **Load-bearing:** when a child's start transition targets
  its own initial state (e.g. `subflow-orchestration-grandchild` → `grandchild-initial`) nothing
  moves afterwards, so the pipeline's rest point has nothing to publish and this is the parent's
  ONLY notification for that child. Removing it silently strands the parent's `effectiveState`.
- The coalescing state is two unmapped private fields on the aggregate. Their lifetime is the
  tracked instance's, which is exactly one episode: the inline auto-chain reuses it
  (`CreateFromPreloaded`), every post-commit / retry / subflow-callback boundary loads a fresh one.
- Pinned by `SubStateChangeCoalescingTests`.

### Child state → parent `EffectiveState` (`SubflowStateService`)

- **It takes the SAME per-sub-item lock the three terminal paths take**
  (`vnext:{domain}:{flow}:{parentId}:sub:{subId:N}`, bounded wait from
  `WorkflowExecutionOptions.SubItemTerminalLockRetry`). Before that it was the only parent-mutation
  path with no lock, which made its `SubFlowStateChangedAt` read-check-write a real TOCTOU: two
  deliveries could both read the same stamp, both pass the check, and the OLDER one land last.
- **Do not "fix" this with a CAS on the correlation row placed before the parent load.** Every
  terminal path mutates the correlation and the parent in ONE `SaveChanges` batch, and EF emits
  `Instances` before `InstancesCorrelations` — the order is always P → C. A correlation-first CAS
  inverts that to C → P with an aggregate load in between, and deadlocks (40P01) against the
  terminal paths on the hot path. The lock is the mechanism; the write order stays P → C.
- **Equal `ChangedAt` is ACCEPTED and re-applied**, only strictly-older is rejected. A duplicate
  delivery carries the same stamp; re-applying is idempotent, and rejecting it would close the only
  recovery path a redelivery has.
- **The Inbox BACKUP checks before it locks** (`SubFlowStateChangedInput.IsBackupDelivery`, set only
  by `InstanceSubStateChangedEventHandler`). One no-tracking `ProbeSubflowStateAsync` row: stale by
  the same predicate, or the same notification (seq, else µs stamp) whose values the parent and the
  open correlation already carry, or a child whose correlation a terminal settlement has already
  closed (`HasClosedCorrelation`, reason `correlation_closed` — the locked path would answer
  not-found and write nothing) ⇒ dropped with no lock, no transaction, no write; anything else
  (including a child with no correlation at all) takes the locked path. It is a plain READ COMMITTED select — no row lock, no write — so it does not
  reintroduce the forbidden correlation-first CAS, and recovery is intact (an uncommitted first apply
  leaves an older watermark). A backup that loses the lock re-probes before throwing. Relay
  deliveries never probe.
- **The state channel has its OWN lock backoff** (`WorkflowExecutionOptions.SubItemStateLockRetry`,
  10 × 10 ms), not the terminal paths' (`SubItemTerminalLockRetry`, 10 × 20 ms): the wait is
  `base*attempt + jitter(0..base)`, so the base decides the first retry. The terminal base used to be
  120 ms, and the first retry alone slept 120–240 ms for a lock held a few ms (measured max 241 ms on
  the state channel; 154–169 ms backup waits on digital-investment).
- **The terminal paths re-probe between lock attempts** (`LockAcquireWait.KeepWaiting`, wired by
  `SubItemTerminalGuardExtensions.WhileCorrelationOpen`): after each backoff sleep the same lock-free
  correlation probe runs, and once it answers settled the wait is abandoned
  (`vnext.lock.wait_abandoned=true`, result `correlation_already_settled_during_wait`) instead of
  acquiring the lock only to load the parent and find the correlation closed. A not-acquired answer is
  re-probed once more before it becomes a `SubflowTerminalLockNotAcquiredException`.
- The UoW is `RequiresNew, IsTransactional = true` — not for the lock, for atomicity: without a
  transaction Aether stages the outbox rows *after* `UpdateAsync(autoSave)` already committed the
  Instance row, so the upward event and the state write could diverge.
- It loads through `FindForSubflowStateChangeAsync` (tracked parent + only this child's OPEN
  correlation, **no `DataList`**), not the default detail load.
- **It is the second post-commit relay call site.** When the parent is itself a subflow the write
  raises the grandparent's `InstanceSubStateChangedEvent`; the service snapshots the aggregate's
  events before saving, commits, releases its lock, then hands them to `IPostCommitRelayDispatcher`.
  That is what makes the fast path walk the whole ancestor chain instead of stopping at depth 1 —
  the runner never sees an event raised inside this service's own UoW. Capped by
  `PostCommitRelayDispatcher.MaxRelayDepth`; past it the event still travels the outbox.

### Relay depth — the whole ancestor chain, not one level

Applying a sub-state change to a parent that is ITSELF a subflow raises the grandparent's
`InstanceSubStateChangedEvent` inside that write. That event is raised in `SubflowStateService`'s own
unit of work, which the runner never sees — so a runner-only relay would stop at depth 1 and every
level above it would fall back to broker latency.

`SubflowStateService` therefore holds the **second dispatcher call site**: it snapshots the
aggregate's events before saving (the SaveChanges sink drains them), commits, releases its lock, and
hands them to the same dispatcher. The fast path walks up the chain recursively, each level taking
and releasing only its own `:sub:{subId}` lock.

`PostCommitRelayDispatcher.MaxRelayDepth` (10) caps the nested in-process walk. Real chains are a
handful of levels and terminate at the root; the cap only stops a pathological graph from turning
one hop into an unbounded awaited walk. Past it the event still travels the outbox — only the
immediacy is lost, logged as `depth_exceeded`. Cross-domain legs start a fresh request on the far
side and therefore a fresh count, which is correct: that hop is bounded by the relay's own remote
timeout instead.

## Independence guarantees

The relay and the outbox/Inbox pipeline are deliberately independent along four axes:

1. **Path independence** — the relay never touches the outbox table, broker, or workers; a
   worker/broker outage does not affect the relay, and a relay failure does not affect the outbox
   flow (the row is already committed).
2. **Order independence** — the relay may finish before its outbox row is even published; the
   later Inbox delivery is absorbed by `ISubItemTerminalGuard` as `AlreadySettled` for the terminal
   events, and for `InstanceSubStateChangedEvent` by `SubflowStateService`'s monotonic
   `SubFlowStateChangedAt` guard, which now runs under the SAME per-sub-item lock the terminal paths
   take (`vnext:{domain}:{flow}:{parentId}:sub:{subId:N}`). That lock is what makes the guard sound:
   before it, two deliveries could both read the same stamp, both pass the check, and the older one
   land last. Equal timestamps are accepted and re-applied — a duplicate delivery carries the same
   `ChangedAt`, re-applying it is idempotent, and rejecting it would close the only recovery path a
   redelivery has. Pure-outbox events flow at their own pace.
3. **Failure independence** — no relay or outbox failure faults the child instance; each mechanism
   has its own retry path (relay → Inbox backup; outbox → processor retry; Inbox → broker
   redelivery).
4. **Resource independence** — the relay does not mark or suppress the outbox row. The event stays
   published as a fact (so future domain event-triggers on `instance.sub.*` topics keep working);
   the duplicate delivery costs one guard probe, nothing more.

## Re-arm on phase-2 resume failure

If a subflow terminal settlement is reverted after a phase-2 parent-resume failure (the
correlation reopens), the revert's unit of work republishes the terminal event as a fresh durable
delivery so the Inbox backup can settle it again — closing the window where the original delivery
had already been ACKed by the lock-free duplicate guard before the revert happened.

- Each republish carries `RearmAttempt` incremented by one (`null`/`0` on an original delivery).
- Capped at 5 attempts (`MaxRearmAttempts` in `SubflowCompletionService`, `SubflowFaultService`,
  `SubflowCancellationService`). Below the cap: `SubflowTerminalRearmed` (WorkflowLogs 40127,
  Warning). At the cap: `SubflowTerminalRearmExhausted` (WorkflowLogs 40128, Error) — the
  correlation was reverted but no fresh durable delivery was published; this state needs manual
  intervention.

## Latency (unvalidated design budget — measured in verification)

The following table is a **design budget only**. None of these numbers have been measured against
a running system; Faz C (integration + load verification) is the phase that validates or corrects
them.

| Path | Parent-resume gap |
|---|---|
| Sync (any domain) | 0 — relay awaited before response |
| Async + same domain | ≈ 0 — inline in the same job, post-commit |
| Async + cross domain | ~10–30 ms — direct service invocation, never the outbox loop |
| Crash between commit and relay (rare) | Inbox backup: ~100–300 ms; lost-nudge tail = one idle poll interval (currently 5 s idle / 10 s max in vnext config — a tunable knob, deliberately left unchanged by this work) |

## Topic naming

Every runtime topic is `vnext.{domain}.{environment}.{eventName}.v{version}` — for example
`vnext.core.production.instance.sub.completed.v1` or `vnext.core.development.aether.outbox.wakeup.v1`.

- `{domain}` is `APP_DOMAIN` (the primary domain). Co-hosted domains (`APP_DOMAINS`) run in the same
  processes and use the same broker user, so they publish and subscribe on the primary's topics.
- `{environment}` is `ASPNETCORE_ENVIRONMENT`, lower-cased; unset means `production`, the same
  default the .NET host applies.
- The prefix is built once, in `WorkflowEventBusConfiguration.Apply`, and handed to Aether as
  `AetherEventBusOptions.TopicPrefix`. Aether's `TopicNameFormatter` is the only place a topic name is
  assembled: the publisher (`DefaultTopicNameStrategy`) and the subscription list (`/dapr/subscribe`,
  built by `EventHandlerAutoDiscovery`) both go through it, so they cannot disagree.
- Why the prefix: the Kafka broker grants each domain's user `vnext.{domain}.*` on topics and consumer
  groups. A topic outside the prefix is refused, so every runtime topic must carry it. The consumer
  group is set by the Helm chart's Kafka component (`consumerID`), not by the runtime.
- The outbox stores a message's topic when it is written (`ExtraProperties["TopicName"]`). Rows written
  before a topic-name change are still sent to the old name — drain the outbox before rolling out such
  a change.
- Topics a domain authors itself (a `DaprPubSubTask`'s `topic`, a domain-owned Dapr `Subscription`) are
  used verbatim; the runtime adds no prefix to them. Under Kafka they must follow the same
  `vnext.{domain}.{environment}.*` shape, or be granted to the domain explicitly.

## Wakeup signal

Publishing an outbox row and waiting for the next poll tick used to be the only path from
"committed" to "delivered." Aether now gates that with a loss-tolerant nudge:

- `EfCoreOutboxStore.StoreAsync` registers one `uow.OnCompleted` callback per unit of work
  (deduplicated via a `ConditionalWeakTable`) — but **only when that UoW actually stored an outbox
  row**. A UoW that stored nothing never fires a nudge.
- The callback returns immediately and publishes `OutboxWakeupEvent`
  (`[EventName("aether.outbox.wakeup")]`, empty payload) as a **detached, fire-and-forget task
  bounded to 2 seconds** — failures are logged, never awaited by the caller, never rethrown into
  the commit path. If there is no ambient UoW, the coordinator sends the signal immediately on a
  best-effort basis; that branch is explicitly **excluded from the latency guarantee** above.
- The Outbox worker subscribes to the nudge via a bespoke `/dapr/subscribe` declaration (it has no
  `IEventHandler`s of its own, so registry-driven discovery would find nothing) routed to
  `POST /internal/outbox-wakeup`, which signals `IPollingWakeSignal<IOutboxProcessor>`.
  `OutboxBackgroundService` awaits that signal instead of a plain `Task.Delay`, using the poll
  interval as a timeout — so **polling remains the safety net**, including the startup offset.
- The Inbox worker needs no cross-process subscription: `EventsController.ProcessEventAsync`
  signals `IPollingWakeSignal<IInboxProcessor>` in-process, in the same request that stored the
  inbox row.

### Config: `Aether:Outbox:WakeupSignalEnabled`

- Default: `false`. Gates the **publish** side only (`OutboxWakeupCoordinator` / the notifier
  registration) — the Outbox worker's `IPollingWakeSignal<IOutboxProcessor>` is registered
  unconditionally, so the wakeup endpoint always resolves; it just never receives a real nudge
  unless a writer host has the flag enabled.
- Enabled (`true`) in the two hosts that actually write outbox rows: `workers/BBT.Workflow.Workers.Outbox`
  (`Aether:Outbox` section) and `orchestration/BBT.Workflow.Orchestration.HttpApi.Host`
  (`Aether:Outbox` section, via `AddDomainEventsInfrastructure`). The Execution host registers no
  outbox at all (no `Aether:Outbox` section) and is untouched; the Inbox worker registers only
  `AddAetherInbox` and has no outbox config either.
- **Helm reminder**: `vnext-helm-charts` values for these two hosts need the corresponding
  `Aether__Outbox__WakeupSignalEnabled` (or equivalent nested YAML) override per environment once
  this change is promoted past local. This doc does not edit that repo — flag it to whoever owns
  the Helm charts before rollout.

### Trace noise: the wakeup round-trip stays out of business traces

The wakeup nudge is infrastructure signaling, not business flow, and three independent mechanisms
keep it from polluting a transition's trace:

- **Publish side severed** — `OutboxWakeupCoordinator.NotifyFireAndForget` clears
  `Activity.Current` to `null` before publishing `OutboxWakeupEvent` inside its detached,
  fire-and-forget `Task.Run`. The committing transition's `ExecutionContext` would otherwise flow
  into that task and hand its ambient trace context to the publish call; severing it means the
  nudge's client span (and anything the sidecar does downstream) can never attach to — or carry
  the traceparent of — the business trace whose commit triggered it.
- **Worker server span excluded** — the Outbox worker's own `POST /internal/outbox-wakeup`
  endpoint (where `IPollingWakeSignal<IOutboxProcessor>` is signaled) is now listed in
  `Telemetry:Tracing:ExcludedPaths` (`^/internal/outbox-wakeup$`), alongside the existing
  `^/health$`-style entries. Without it, every nudge delivery would mint its own ASP.NET Core
  server span in the Outbox worker — a one-span trace fired on a timer, structurally identical to
  the idle-poll `Db.*` noise this same worker already suppresses (see
  [Trace/Span Tree § EF Core instrumentation](trace-span-tree.md#ef-core-instrumentation-the-worker-poll-cost-resolved)).
- **Dapr sidecar spans are a separate, unaddressed layer** — the two mechanisms above only cover
  spans vnext's own code emits. The Dapr sidecar still instruments its own hop for the wakeup
  topic (`pubsub/vnext.{domain}.{env}.aether.outbox.wakeup.v1`) independently of the app-level server span, so a
  tiny standalone trace per nudge (sidecar publish → sidecar deliver) remains visible in the
  backend even after the app-side exclusion above. If that volume bothers a dashboard, the knob is
  an OTel-collector filter dropping that span name before export — the same pattern already used
  for other Dapr-internal noise (see [Trace Lanes](trace-lanes.md)). This plan does **not** touch
  the collector config or `vnext-helm-charts`; it is flagged here for whoever owns that
  configuration to pick up if the sidecar noise becomes a real problem.

## Observability contract

- **Relay span**: `PostCommit.EventRelay`, opened via `PipelineStepActivityHelper.StartOperationActivity`
  (the same activity source used by `Events.PublishDeferred` / `Uow.Commit`, already registered on
  both hosts). Tags:
  - `vnext.event.name` — the event's CLR type name
  - parent/subflow instance id tags, from the relay's `Describe`
  - `vnext.relay.sync` — only when the event carries a `Sync` flag (the three terminal events do;
    `InstanceSubStateChangedEvent` does not, so the tag stays unwritten for that channel)
  - `vnext.relay.route` = `local` | `remote`, derived from `IRuntimeInfoProvider.IsDomainMatch` —
    the same source the gateway itself routes by, so the tag can never disagree with the actual
    route taken
  - `vnext.relay.outcome` = `relayed` | `failed` | `skipped` | `timeout` | `depth_exceeded`
  - `vnext.delivery.role = relay` — the counterpart of the Inbox handlers' `backup`
- **Inbox backup role**: the four relayed events' Inbox handlers (`InstanceSubCompletedEventHandler`,
  `InstanceSubFaultedEventHandler`, `InstanceSubCanceledEventHandler`,
  `InstanceSubStateChangedEventHandler`) tag `vnext.delivery.role = backup` on their activity right
  after `EventTraceScope.Start(...)`.
- **Inbox delivery trace shape**: every Inbox handler calls `EventTraceScope.Start(...)` with an
  explicit `EventTraceMode` — there is no default, so each call site states its classification.
  - `ContinueTrace` covers the three **command** events (`TransitionContinuationRequested`,
    `ChildSubflowCancelRequested`, `ChildSubflowFaultRequested`) and is unchanged from before this
    work: the handler span parents onto the event's own `TraceParent`, joining the producing
    transition's trace exactly as it always has.
  - `IsolatedDelivery` covers the seven **fact** events (`InstanceCanceledEvent`,
    `InstanceCompletedCleanupEvent`, `InstanceFaultedCleanupEvent`, `InstanceSubStateChangedEvent`,
    and the three sub-terminal events `InstanceSubCompletedEvent`/`InstanceSubFaultedEvent`/
    `InstanceSubCanceledEvent`). Instead of joining the producer's trace, the handler **roots a
    brand-new trace** for its span without cross-trace `ActivityLink`s. The producer and ambient
    pub/sub delivery trace/span ids are retained as indexed tags. This is a deliberate episode separation: a fact's delivery machinery (pubsub → inbox →
    Dapr invoke → settlement) no longer drags the entire business trace it is reporting on into one
    tree. Forcing the genuine root requires clearing `Activity.Current` around the `StartActivity`
    call — a default `ActivityContext` parent alone does not do it, .NET falls back to the ambient
    activity — and `EventTraceScope.Dispose` restores the ambient afterward.
  - An `IsolatedDelivery` root is stamped `messaging.message.id` and `vnext.causation.id` (both from
    the CloudEvent envelope id, since the root is now a trace entry point and must be findable by
    the message that produced it), plus `vnext.delivery.attempt` when the event carries a
    rearm/redelivery count (the three sub-terminal events' `RearmAttempt`) — omitted entirely when
    the event carries none. `ContinueTrace` stamps none of these tags, byte-for-byte parity with
    the pre-split behavior.
  - `WorkflowTraceLane.Reset(...)` side effects are **identical in both modes** — this is what
    keeps a genuine backup-settled subflow resume anchored into the parent's trace regardless of
    which mode delivered it. This is independent of the relay-specific "Inbox backup role" bullet
    above; both can apply to the same sub-terminal handler span at once.
- **Health signal**: watch backup deliveries by outcome, not just volume. A backup delivery that
  actually **settles** the parent (the relay missed it) is a real signal the relay path degraded —
  investigate it. A backup delivery that resolves as `AlreadySettled` is expected, ordinary noise
  from the dual-delivery-by-design model; it does not indicate a problem. For the state channel the
  same reading applies to `SubFlowStateChangeOutOfOrder` (40131): a backup delivery that lands
  out-of-order is the guard absorbing the duplicate, which is the expected case.
- **WorkflowLogs** (`src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs`, 40xxx range):
  `SubFlowFaultIgnoredDomainMismatch` (40111, Warning — the Inbox dropped a fault whose parent is
  hosted in another domain; it was Debug before vnext#1256),
  `PostCommitEventRelayed` (40124, Information), `PostCommitEventRelayFailed` (40125, Warning; 40125,
  40126 and 40133 carry the sub and parent ids, and say the Inbox backup delivers on a local route
  only), `PostCommitTerminalRelayLost` (40142, Error — a terminal event not delivered to a parent in
  another domain), `PostCommitStartFaultSupersededByChildSettlement` (10268, Information),
  `PostCommitEventRelayRejected` (40126, Warning), `SubflowTerminalRearmed` (40127, Warning),
  `SubflowTerminalRearmExhausted` (40128, Error), `SubFlowStateChangeParentNotFound` (40129, Warning),
  `SubFlowStateChangeCorrelationNotFound` (40130, Warning), `SubFlowStateChangeOutOfOrder` (40131,
  Warning), `SubFlowStateChangeLockNotAcquired` (40132, Warning), `PostCommitEventRelayTimedOut`
  (40133, Warning), `PostCommitEventRelayDepthExceeded` (40134, Warning),
  `SubFlowStateChangeBackupAlreadyApplied` (40137, Debug), `PostCommitEventRelaySkipped` (40138,
  Debug), `SubFlowStateChangeTerminalSuperseded` (40139, Debug),
  `SubFlowCarriedHeadersFiltered` (40140, Debug).
- **Aether's per-attempt lock log**: `DaprDistributedLockService.TryAcquireLockAsync` logs
  `Failed to acquire lock for resource …` at Warning on every unsuccessful attempt — contention, not
  failure, and the bounded retry in `TransitionLockScopeFactory` already logs each retry at Debug
  (`TransitionLockRetryScheduled`) and exhaustion once at Warning (`InstanceLockFailed`). The
  orchestration host sets that category to `Error` in `appsettings.json` (errors still surface). Only
  orchestration needs it: it is the only host that takes the sub-item lock — the Inbox worker forwards
  backup deliveries to orchestration over HTTP. A per-category environment variable leaves the entry in
  force; a chart that replaces the whole `Logging` section must carry it (follow-up for
  `vnext-helm-charts`: `docs/integration/vnext-helm-charts/aether-lock-log-level.md`). Side effect: the
  Dapr handle's `ExtendAsync is not supported` Warning, logged under the same category, is hidden too.

## Carried headers on sub-item terminal events

The three terminal events `instance.sub.completed`, `instance.sub.faulted` and `instance.sub.canceled`
carry an optional `CarriedHeaders` map: the request headers of the call that settled the child, so the
parent's output mapping and its resume can read what the child's caller sent. The field is
additive and optional; a consumer that ignores it sees the event as before.

- **Carried set.** Every request header except the deny floor and the framework exclusions. Both are
  enforced in code (`CarriedHeaderPolicy`, `src/BBT.Workflow.Application/SubFlow/Headers/`), compared
  case-insensitively, and keys are stored lower-cased.
  - Deny floor: any name containing `authorization`, `cookie`, `token`, `secret` or `session`, plus every
    name in `AdditionalDenied` (exact match). Configuration can add to the floor, never remove from it.
  - Framework exclusions (correctness, not secrecy): `x-subflow-depth`, `x-parent-instance-id`,
    `x-root-instance-id`, `x-workflow-instance-id`, `x-correlation-id`, `traceparent`, `tracestate`,
    `baggage`, `host`, `transfer-encoding`, `connection`, `keep-alive`, and the prefixes `content-`,
    `dapr-`, `grpc-`.
  - Caps: at most `MaxEntries` (64) entries and `MaxValueLength` (4096) characters per value. An
    over-long value is dropped and counted as "denied"; entries past the entry cap are dropped and counted as "overflow". Both counts appear in log 40140; header values are never logged.
- **`role` is always carried and applied** (requester amendment RA-1). There is no `Role` option.
- **Denylist, not allowlist** (RA-2). A header that is neither on the floor nor excluded travels, so a
  secret in an unusually named header (say `x-api-key`) is carried unless it is listed in
  `AdditionalDenied`. Accepted risk: the gateway or the operator owns that list. Values reach the
  outbox row, the Inbox body and the parent's output mapping.
- **Four copy sites.** The set is captured once at the producer (`HandleFinishStep`, and the request-hop
  fault paths), only for sub-item instances (a root instance has no parent to carry them to), then copied
  verbatim at the post-commit relay, the Inbox body and the re-arm rebuild. Every receiver filters it
  again before use, because the event is untrusted input.
- **Upward parent fault.** When the parent itself faults because of the child's settlement (parent
  workflow could not be loaded, output mapping failed, abort boundary), the received set is re-captured
  through the local policy before it goes to the grandparent: `Emit=false` yields `null`, and the deny
  floor and caps apply again, so a forged `/complete` body cannot smuggle `authorization` upward.
- **Switches** (section `Workflow:SubFlow:CarriedHeaders`):

  | Key | Default | Effect |
  |---|---|---|
  | `Emit` | `true` | `false`: producers write no headers on terminal events. |
  | `ExposeToOutputMapping` | `true` | The completion and fault output mapping reads them as `context.Headers`. |
  | `ApplyToResume` | `true` | Applies them to the parent's resume request for completion, fault and cancel (`false` for a domain that wants the stricter behaviour). `Actor = System` is unchanged; `ScriptResponse.Headers` gets no resume semantics. |
  | `AdditionalDenied` | `[]` | Extra names denied (exact, case-insensitive). |
  | `MaxEntries` / `MaxValueLength` | `64` / `4096` | Caps above. |

- **`null` and empty.** If the filtered set is empty the producer writes `null`; a consumer treats `null`
  and `{}` the same (empty dictionary). Timer, timeout and job-timeout-recovery settlements, and the *downward*
  cancel/fault cascades, have no caller request, so they carry `null`; an *upward* parent fault instead
  forwards the re-filtered child set (above). The internal `/complete`, `/fault`, `/cancel` request's own
  HTTP headers are never read; only the body field counts.
- **Never logged.** Header values are never logged or put on a span. The span tag
  `vnext.subflow.carried_header_count` carries the count; `SubFlowCarriedHeadersFiltered` (40140, Debug)
  reports kept / denied / overflow counts only.

How the output mapping and resume see these headers:
[Subflow Execution](../architecture/subflow-execution.md#carried-headers-from-the-child).

## Accepted risks

These were evaluated and accepted by the user during design; they are documented here, not fixed:

- **Worker criticality** — the Outbox/Inbox workers become the mandatory path for the four
  pure-outbox instance events (previously a successful hook needed no worker at all). For the
  three sub-terminal events, the relay keeps subflow progression alive even with the workers down;
  for everything else, the workers are now tier-1 critical. This has a direct Helm consequence —
  replica counts, liveness/readiness probes, and alerting for the Outbox and Inbox workers need to
  reflect that criticality in `vnext-helm-charts`. That repo is not edited by this change; raise it
  with whoever owns the Helm charts.
- **Queue-row throughput** — a hook success previously wrote zero queue rows. Now every event
  writes an outbox row, an inbox row, and crosses the broker. `InstanceSubStateChangedEvent` is the
  hottest of the pure-outbox events. Measured in the Faz C load test, not here.
- **Rolling-upgrade coexistence** — during a rolling upgrade, old nodes still run the deleted hook
  code path (if any old binaries remain in flight) while new nodes relay; both paths are idempotent
  via `ISubItemTerminalGuard`, so in-flight messages process unchanged either way. No special
  migration step is required, but do not assume the cluster is uniformly on the new path until the
  rollout completes.

## Known gap: no `directly:true`-style arming window here

Some other parts of the runtime use a `directly:true`-style arming window (fire a request, then
narrow a race window with a job) — this design has no equivalent, because there are no jobs in
this path. The crash-between-commit-and-relay window is instead covered end-to-end by the Inbox
backup delivery (see [Latency](#latency-unvalidated-design-budget--measured-in-verification)
above) — there is no separate arming mechanism to reason about.

## Verification (2026-08-30, local stack)

Verified against the local stack (all four hosts started with `--launch-profile http`, infra via
`etc/docker/run-docker.sh`) using vnext-example's `Core.IntegrationTests` and a standalone load
probe. Single run, single machine — see the caveat at the end.

- **Integration — Subflow + ChainBusy suites**: 20/20 green against the local stack; FuturePay
  added 6/6 once MockLab was up. The initial reds seen before MockLab started were an environment
  gap, not a regression.
- **Relay primary-path evidence**: 11/11 subflow terminal relays observed on Orchestration matched
  1:1 with their Inbox backup deliveries. Duplicate absorption was confirmed via the terminal-guard
  span outcome (`AlreadySettled` as an activity tag) rather than a plaintext log line — noted as an
  observability gap; a log-level signal for `AlreadySettled` would make this auditable without
  pulling spans.
- **Wakeup signal**: Outbox worker lease→publish deltas measured 2–40 ms during bursts, with no
  idle-poll wait observed between arriving work items. Previous behavior (poll-only) carried up to
  the configured 5 s idle interval per pickup.
- **Worker-kill resilience**: with the Outbox worker stopped, a subflow-completion integration test
  still passed — the relay alone carried the parent resume. After restarting the worker, the
  accumulated backlog drained fully: 476 processed / 0 pending in `sys_queues.OutboxMessages`.
- **Load probe** (`api-tests/subflow-orchestration/terminal-relay-load.py`, 30 instances,
  concurrency 6, gap measured from server-side `InstanceTransitions` timestamps): 30/30 instances
  completed, 0 stuck. Child-terminal → parent-resume gap: p50 50.6 ms, p95 64.4 ms, p99 65.9 ms,
  max 66.3 ms — all three verdicts PASS against the p99 ≤ 250 ms objective, with roughly 4×
  margin. Queue-row cost: 360 outbox rows + 360 inbox rows for 30 instances (12 + 12 rows per
  instance across all events), consistent with the queue-row-throughput risk called out in
  [Accepted risks](#accepted-risks).

These results confirm the design budget in
[Latency](#latency-unvalidated-design-budget--measured-in-verification) for this local run; that
table's numbers remain the forward-looking **budget** for environments this run did not exercise —
production-scale broker delay, multi-replica contention, and cross-region hops.

- **Caveat**: all numbers above come from a single local run on one M-series dev machine with every
  service running locally (no container image, no broker latency, no replica contention).
  Production-grade histograms under real broker delay and replica contention remain future work.

### Fault relay (vnext#1256)

**Unit level.** `TransitionPipelineTests` (region *Fault relay to the parent*),
`PostCommitParentMutationServiceTests`, `JobTimeoutRecoveryFaultRelayTests`, `SubflowFaultServiceTests`,
`PostCommitRelayDispatcherTests`, `TransitionRunnerPostCommitTests`, `InstanceSubFaultedEventHandlerTests`
and `FaultRelayRegistrationTests` pin: the hosted-parent skip; commit → lock release → relay; the token
rule; no relay without a commit; the snapshot taken before a save that drains the aggregate; the
start-compensation guard in both orders; the relay-failure logs; the DI wiring.

**Cross-domain lab, 2026-11-23 — this version** (vnext-example `labs/cross-domain`, four domains,
provider `dapr`, images built from the working tree, scenario `child-fault-xd` 1.0.1):

| Case | Result |
|---|---|
| CF-01..04: child faults on a direct partner request, on a request proxied by the parent (sync and async), in its own start chain | 4/4 green: parent Active in its boundary target, one incident, correlation closed |
| CF-06: core parent → partner middle (no error boundary) → partner leaf; the leaf faults | Green. Leaf → middle is in one domain: no relay, the partner Inbox delivered it. The middle faulted on the default path and its own upward fault was relayed to core (this is the `SubflowFaultService` call site) |
| CF-07: core paused while the partner child faults | Parent stays Busy; partner logs 40125 (`route remote`, both ids) and 40142 at Error, the Inbox 40111. The child's sync request waited 60 s. After unpausing, a re-post of the outbox row (mapping above) settled the parent; a second re-post changed nothing |
| Whole `CrossDomainLab` suite | 51 passed, 4 failed, 2 skipped. The 4 failures are `FileOffloadCrossDomainTests` (the lab's partner sidecar has no `vnext-blob-local` binding, 503 — the same on master) |

Logs of the CF-01..06 run: five `PostCommitEventRelayed` for `InstanceSubFaultedEvent`, all to a core
parent; none for the leaf → middle fault; one Inbox `SubFlowFaultReceived` for the middle.

**First version, 2026-11-20** (relay to every parent, no start-compensation guard, no
`SubflowFaultService` site): CF-01..04 4/4 green; images built from master `e4faefa8` 4/4 red (parent
Busy after 30 s, correlation open, no incident). Measured on four traces: lock release → relay
0.5–10.4 ms; relay call 39.7–178.1 ms (first call cold); parent settlement 34.8–141.4 ms. One machine,
four samples: order of magnitude only.

**Not run:** the client-facing child start failure (start-compensation guard) end to end — no lab flow
produces a `ResourceLockConflict` in a child's start chain; it is pinned at unit level only. The
same-domain smoke on **local binaries** (`SubFlowFaultChainTests`, `SubflowStartFailureLab`,
`SubflowCarriedHeadersLab`); the same-domain path was exercised only inside the lab images (CF-06,
leaf → middle). No master baseline for CF-06 and CF-07.

## Related

- [End-to-End Trace/Span Tree](trace-span-tree.md) — span-name → source → tags reference,
  including `PostCommit.EventRelay`'s place in the trace.
- [Trace Lanes](trace-lanes.md) — why a relay (a synchronous command) stays in the same trace as
  its parent rather than starting a new lane.
- `.claude/rules/dotnet-coding-standards.md` § Domain Events (Dual Processing) — the authoring
  contract every distributed event must follow now that hooks no longer exist.
