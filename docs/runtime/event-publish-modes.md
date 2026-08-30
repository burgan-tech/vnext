# Event Publish Modes: Outbox-Only Events, Subflow Terminal Relay, Wakeup Signal

## Purpose

Every distributed event in the runtime used to be intercepted by `HookedDistributedEventBus`,
which ran a per-event hook either inline pre-commit or inside `uow.OnCompleted` (blocking the
commit), then wrote an outbox row on top for durability. The EventHook infrastructure has been
deleted outright. All distributed events now ride the transactional outbox uniformly; the three
subflow-terminal events additionally get an immediate post-commit **relay** — a direct command
call, not an event — so parent resume keeps a near-zero gap on both sync and async paths. Aether
gained a wakeup signal so the outbox/inbox poll loop no longer needs to wait out its idle
interval on the common path.

This page describes the resulting publish-mode taxonomy, the relay's semantics and independence
guarantees, the wakeup mechanism, and the accepted risks. It replaces the old hook model as the
canonical reference for how a distributed event gets from "committed" to "handled."

## Publish-mode taxonomy

| Mode | Declared by | Behavior |
|---|---|---|
| **Outbox** (default, ALL events) | nothing | transactional outbox row → wakeup nudge → Outbox worker publish → broker → Inbox worker (in-process nudge) → Inbox handler |
| **Outbox + TerminalRelay** | implementing `ISubflowTerminalEvent` | everything above PLUS: the runner relays the event as a **command** immediately after commit via `SubflowTerminalRelay` → `IInstanceCommandGateway` (routed in-process or via Dapr service invocation); the Inbox handler is demoted to a durable backup, deduplicated via `ISubItemTerminalGuard` |

There is no third mode. Nothing publishes synchronously inline anymore — `TraceStampingDistributedEventBus`
(the renamed, shrunk `HookedDistributedEventBus`) only stamps trace context and delegates to the
outbox; it no longer knows about hooks, `EventHookMode`, or per-event dispatch.

## Event classification

| Event | Mode |
|---|---|
| `InstanceSubCompletedEvent`, `InstanceSubFaultedEvent`, `InstanceSubCanceledEvent` | Outbox + TerminalRelay (all three share the same terminal-settlement semantics and one relay code path) |
| `InstanceCanceledEvent`, `InstanceCompletedCleanupEvent`, `InstanceFaultedCleanupEvent`, `InstanceSubStateChangedEvent`, `ChildSubflow*`, `TransitionContinuationRequested` | Pure Outbox |

Only the three sub-terminal events implement `ISubflowTerminalEvent`. Adding the relay mode to a
new event means implementing that marker interface — nothing else opts an event into the relay
path.

## Relay semantics

- The runner (`TransitionRunner`), after `uow.CommitAsync` succeeds, calls
  `SubflowTerminalRelay.RelayAsync(coreOutput.DeferredEvents, ct)`. The relay filters the deferred
  events down to `ISubflowTerminalEvent` payloads and processes them **sequentially** — a single
  hop produces at most one terminal event by domain construction (terminal outcomes are mutually
  exclusive, pinned by `SubItemTerminalProbe.Conflict`), so the loop is defensive rather than a
  real fan-out.
- Per event: the exact mapping code that used to live in the event hooks (moved verbatim) builds
  the gateway input, then calls `CompleteAsync` / `FaultAsync` / `CancelAsync` on
  `IInstanceCommandGateway`, which routes in-process for the same domain or via Dapr service
  invocation cross-domain (`RoutedInstanceCommandGateway`).
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
- Observability: each relay attempt opens a `Subflow.TerminalRelay` activity (see
  [Observability contract](#observability-contract) below) and logs
  `SubflowTerminalRelayed` (EventId 40124) on success, `SubflowTerminalRelayFailed` (40125,
  exception path) or `SubflowTerminalRelayRejected` (40126, failed-`Result` path) otherwise.

## Independence guarantees

The relay and the outbox/Inbox pipeline are deliberately independent along four axes:

1. **Path independence** — the relay never touches the outbox table, broker, or workers; a
   worker/broker outage does not affect the relay, and a relay failure does not affect the outbox
   flow (the row is already committed).
2. **Order independence** — the relay may finish before its outbox row is even published; the
   later Inbox delivery is absorbed by `ISubItemTerminalGuard` as `AlreadySettled`. Pure-outbox
   events flow at their own pace; a stale `InstanceSubStateChangedEvent` arriving after completion
   is rejected by the existing monotonic `SubFlowStateChangedAt` guard in `SubflowStateService`.
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

## Observability contract

- **Relay span**: `Subflow.TerminalRelay`, opened via `PipelineStepActivityHelper.StartOperationActivity`
  (the same activity source used by `Events.PublishDeferred` / `Uow.Commit`, already registered on
  both hosts). Tags:
  - `vnext.event.name` — the event's CLR type name
  - parent/subflow instance id tags
  - `vnext.relay.sync` (`terminal.Sync`)
  - `vnext.relay.route` = `local` | `remote`, derived from `IRuntimeInfoProvider.IsDomainMatch` —
    the same source the gateway itself routes by, so the tag can never disagree with the actual
    route taken
  - `vnext.relay.outcome` = `relayed` | `failed` | `skipped`, set after dispatch
- **Inbox backup role**: the three sub-terminal Inbox handlers (`InstanceSubCompletedEventHandler`,
  `InstanceSubFaultedEventHandler`, `InstanceSubCanceledEventHandler`) tag
  `vnext.delivery.role = backup` on their activity right after `EventTraceScope.Start(...)`.
- **Health signal**: watch backup deliveries by outcome, not just volume. A backup delivery that
  actually **settles** the parent (the relay missed it) is a real signal the relay path degraded —
  investigate it. A backup delivery that resolves as `AlreadySettled` is expected, ordinary noise
  from the dual-delivery-by-design model; it does not indicate a problem.
- **WorkflowLogs** (`src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs`, 40xxx range):
  `SubflowTerminalRelayed` (40124, Information), `SubflowTerminalRelayFailed` (40125, Warning),
  `SubflowTerminalRelayRejected` (40126, Warning), `SubflowTerminalRearmed` (40127, Warning),
  `SubflowTerminalRearmExhausted` (40128, Error).

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
  including `Subflow.TerminalRelay`'s place in the trace.
- [Trace Lanes](trace-lanes.md) — why a relay (a synchronous command) stays in the same trace as
  its parent rather than starting a new lane.
- `.claude/rules/dotnet-coding-standards.md` § Domain Events (Dual Processing) — the authoring
  contract every distributed event must follow now that hooks no longer exist.
