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
| **Outbox + PostCommitRelay** | a `IPostCommitEventRelay<TEvent>` REGISTRATION in DI | everything above PLUS: the runner relays the event as a **command** immediately after commit via `PostCommitRelayDispatcher` → `IInstanceCommandGateway` (routed in-process or via Dapr service invocation); the Inbox handler is demoted to a durable backup, deduplicated by the receiver's own guard |

There is no third mode. Nothing publishes synchronously inline anymore — `TraceStampingDistributedEventBus`
(the renamed, shrunk `HookedDistributedEventBus`) only stamps trace context and delegates to the
outbox; it no longer knows about hooks, `EventHookMode`, or per-event dispatch.

## Event classification

| Event | Mode |
|---|---|
| `InstanceSubCompletedEvent`, `InstanceSubFaultedEvent`, `InstanceSubCanceledEvent` | Outbox + PostCommitRelay (the three share terminal-settlement semantics; each has its own relay class over a common base) |
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
  Inbox backup picks the work up shortly after. Relay calls are bounded by the gateway's existing
  invocation timeouts.
- Observability: each relay attempt opens a `PostCommit.EventRelay` activity (see
  [Observability contract](#observability-contract) below) and logs
  `PostCommitEventRelayed` (EventId 40124) on success, `PostCommitEventRelayFailed` (40125,
  exception path), `PostCommitEventRelayRejected` (40126, failed-`Result` path),
  `PostCommitEventRelayTimedOut` (40133) or `PostCommitEventRelayDepthExceeded` (40134). The three
  EventIds carried over from the terminal-only predecessor so existing dashboards keep resolving.

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
  topic (`pubsub/{env}.aether.outbox.wakeup.v1`) independently of the app-level server span, so a
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
  `PostCommitEventRelayed` (40124, Information), `PostCommitEventRelayFailed` (40125, Warning),
  `PostCommitEventRelayRejected` (40126, Warning), `SubflowTerminalRearmed` (40127, Warning),
  `SubflowTerminalRearmExhausted` (40128, Error), `SubFlowStateChangeParentNotFound` (40129, Warning),
  `SubFlowStateChangeCorrelationNotFound` (40130, Warning), `SubFlowStateChangeOutOfOrder` (40131,
  Warning), `SubFlowStateChangeLockNotAcquired` (40132, Warning), `PostCommitEventRelayTimedOut`
  (40133, Warning), `PostCommitEventRelayDepthExceeded` (40134, Warning).

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
backup delivery (see [Latency](#latency-unvalidated-design-budget---measured-in-verification)
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
[Latency](#latency-unvalidated-design-budget---measured-in-verification) for this local run; that
table's numbers remain the forward-looking **budget** for environments this run did not exercise —
production-scale broker delay, multi-replica contention, and cross-region hops.

- **Caveat**: all numbers above come from a single local run on one M-series dev machine with every
  service running locally (no container image, no broker latency, no replica contention).
  Production-grade histograms under real broker delay and replica contention remain future work.

## Related

- [End-to-End Trace/Span Tree](trace-span-tree.md) — span-name → source → tags reference,
  including `PostCommit.EventRelay`'s place in the trace.
- [Trace Lanes](trace-lanes.md) — why a relay (a synchronous command) stays in the same trace as
  its parent rather than starting a new lane.
- `.claude/rules/dotnet-coding-standards.md` § Domain Events (Dual Processing) — the authoring
  contract every distributed event must follow now that hooks no longer exist.
