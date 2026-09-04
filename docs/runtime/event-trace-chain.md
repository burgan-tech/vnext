# The Event Chain: Publish → Hook → Outbox → Handle, as One Trace

> **Historical document.** The `EventHook.*` model described and measured here has been removed.
> [Event Publish Modes](event-publish-modes.md) is the canonical current behavior; the current
> span inventory is [Trace Span Tree](trace-span-tree.md). The evidence below is retained for
> archaeology and must not be used as an operational trace contract.

## Why this exists

A domain event's life crosses three processes and (usually) a message broker: it is published
inside a transition's unit of work, staged to the transactional outbox, drained by the Outbox
worker onto Dapr pub/sub, and consumed by the Inbox worker's event handler. Historically the
publish-side half (hook execution) was invisible inside `Uow.Commit`/`Events.PublishDeferred`, and
the outbox → pub/sub → inbox half started a **new, disconnected trace** rooted on the worker's
poll loop — so a single logical "this happened, and here's everything it triggered" story was
split across an attributed half and an orphaned half.

Two changes address this, one per repo:

- **vnext, Task 1** (this repo, commit `82a7439d`): a span per event hook
  (`EventHook.{name}`), parented to whatever is ambient when the hook runs — `Uow.Commit` for
  `DurablePostCommit` hooks, `Events.PublishDeferred` for `HandledOrFallback` hooks. Documented
  already in [Trace/Span Tree](trace-span-tree.md#target-span-tree). **Live and verified below.**
- **aether, Task 2** (`burgan-tech/aether`, commit `950931b` on branch
  `feature/outbox-trace-continuity`): the outbox row carries the drop's trace identity
  (`TraceParent`/`TraceState` in `ExtraProperties`, `outbox.message_id` tagged onto the ambient
  publish span), and `OutboxProcessor` re-parents `Outbox.Process` onto that identity instead of
  the worker loop when present. **Not observable here — see [Release Gate](#release-gate).**

This page is the reference for the full chain: which repo owns which span, what is provably true
today, and what only becomes true after vnext takes the next Aether release.

## Update (2026-08-30): command vs. fact delivery now diverge

Everything below was captured against the per-event `EventHook.{name}` model. That model has since
been deleted outright (see [Event Publish Modes](event-publish-modes.md#purpose)) — every
distributed event now rides the transactional outbox uniformly, and the Inbox side gained a second
trace mode on top of the one demonstrated here.

The live evidence in [Verified evidence](#verified-evidence-task-1-live) below — the
`InstanceSubCompleted.Handle` transaction parenting onto the *same* trace as the producing
transition's `Events.PublishDeferred` span — was true for **every** event at the time it was
captured. It is **no longer true for that specific event**. `EventTraceScope.Start` now takes an
explicit `EventTraceMode` (`workers/BBT.Workflow.Workers.Inbox/Tracing/EventTraceScope.cs`), and the
seven `Instance*` **fact** events — including `InstanceSubCompletedEvent`, the event this page's
worked example is built on — use `EventTraceMode.IsolatedDelivery`: the handler now **roots its own
delivery trace** without a cross-trace `ActivityLink`; producer and transport ids remain searchable
tags instead. Re-running the exact query in [Regression guard](#regression-guard-eventtracescope-still-works)
today would show `InstanceSubCompleted.Handle` as a **root transaction in its own trace**, correlated
by ids but neither linked to nor sharing `trace.id` with origin trace `4682ca695dac4f7021c1a1bc4419faa1`.

The **command** events this page's diagram calls `EventHook`-adjacent but never worked an example
for — `TransitionContinuationRequested`, `ChildSubflowCancelRequested`, `ChildSubflowFaultRequested`
— use `EventTraceMode.ContinueTrace`, which is the *unchanged* continuation of exactly the behavior
demonstrated below: the handler span still parents onto the event's own `TraceParent` and joins the
producing transition's trace, byte-for-byte the same result this page's live evidence shows.

The evidence below therefore keeps historical value for the removed publish-side
`EventHook.{name}` spans and for the `ContinueTrace` shape it happens to also demonstrate on the
Inbox side — it simply no
longer describes the **current** trace shape for `InstanceSubCompletedEvent` or any other fact
event. See [Event Publish Modes § Observability contract](event-publish-modes.md#observability-contract)
for the current tag/shape reference (`messaging.message.id`, `vnext.causation.id`,
`vnext.delivery.attempt`) and [Trace/Span Tree](trace-span-tree.md) for where `Outbox.Process`
itself now roots rather than rejoining the origin trace.

## Chain diagram

```
vnext (orchestration/execution host)
  TransitionJob.Execute/{key}  or  HTTP transaction (sync path)
  └─ Uow.Commit                                  transaction commit (TransitionRunner)
     └─ EventHook.{name}                         DurablePostCommit hooks, run after commit
  └─ Events.PublishDeferred                       staging deferred events onto the bus, pre-commit
     ├─ EventHook.{name}                          HandledOrFallback hooks, run at publish time
     └─ EventBus.Publish                          → aether: EfCoreOutboxStore.StoreAsync
                                                     [Task 2] tags this span outbox.message_id
                                                     [Task 2] stamps the row's TraceParent/TraceState
                                                            ┊
                                                            ┊  (row persisted; worker polls later)
                                                            ┊
aether (BBT.Workflow.Workers.Outbox process)
  Outbox.Process                                  OutboxProcessor drain loop, per row
     [pre-release]  parent = worker-loop Activity.Current (new, disconnected trace)
     [Task 2, once taken] parent = the row's stored TraceParent — REJOINS the origin trace,
                           worker loop kept only as an ActivityLink
  └─ EventBus.PublishEnvelope
     └─ EventBus.PublishToBroker
        └─ POST (Dapr sidecar HTTP call)

vnext (BBT.Workflow.Workers.Inbox process)
  {Event}.Handle                                  EventTraceScope — ALREADY re-parents onto the
                                                     event's own TraceParent field (payload-level,
                                                     set by HookedDistributedEventBus at publish
                                                     time; independent of Task 2's row-level copy)
     └─ Dapr invoke {app}                          any outbound call the handler makes
```

Two different "TraceParent" carriers are in play and it is worth being precise about which is
which:

- The **event payload's own `TraceParent` field** (`ITraceableDistributedEvent.TraceParent`,
  set by `HookedDistributedEventBus` at publish time) is vnext-owned, ships today, and is what
  `EventTraceScope` in the Inbox worker already uses to re-parent `{Event}.Handle`. This is the
  regression guard verified below — it does **not** depend on Task 2 at all.
- The **outbox row's `TraceParent`/`TraceState`** (`OutboxMessage.ExtraProperties`, Task 2) is a
  separate, aether-owned copy used only by `OutboxProcessor` to re-parent `Outbox.Process` itself.
  It is the piece gated behind the next Aether release.

## Which repo owns which span

| Span | Repo | Source | Notes |
|---|---|---|---|
| `Uow.Commit`, `Events.PublishDeferred` | vnext | `BBT.Workflow.Pipeline` | Pre-existing (trace-span-tree work). |
| `EventHook.{name}` | vnext | `BBT.Workflow.Instances.Events` | Task 1, this page's primary subject. |
| `EventBus.Publish` (ambient at outbox write) | vnext/aether boundary | — | The span Task 2 tags `outbox.message_id` onto. |
| `Outbox.Process`, `EventBus.PublishEnvelope`, `EventBus.PublishToBroker` | aether | `BBT.Aether.Infrastructure` (`InfrastructureActivitySource`) | Re-parenting logic is Task 2. |
| `{Event}.Handle` | vnext | `BBT.Workflow.Workers.Inbox` | `EventTraceScope`, pre-existing, unaffected by Task 2. |

## Environment note

vnext consumes Aether from nuget.org (`1.0.36`, no local package feed wired into this repo's
build), so Task 2 has no observable effect here until the next Aether release ships that package.
Task 1 has no such dependency — it is pure vnext code — so it is fully observable live now. Both
halves are recorded below with that honestly split: one verified, one explained via source-code
inspection and a **negative** live check that confirms the pre-release behavior is what we expect.

## Verified evidence (Task 1, live)

### Setup actually used

The brief's primary scenario is `FuturePayTests` (drives bureau/collateral subflows, whose
completion fires the `DurablePostCommit` hooks this page cares about), and that is what ran —
the `MoneyTransferTests` fallback was not needed.

Before the flow could run, the four local hosts had to be started with **`APP_DOMAIN=core`** and
`ConnectionStrings:Default` pointed at `Aether_WorkflowDb`, bypassing this repo's current
`Properties/launchSettings.json` / `appsettings.json` — a concurrent, uncommitted change in this
working tree (not part of this task) had retargeted all four hosts to `APP_DOMAIN=contract` and a
`vNext_contract` database, which made every `vnext-example` ("core" domain) call fail with
`Instance:100001 — Invalid domain`. The four binaries were launched directly (not via
`dotnet run --launch-profile http`, which would have re-applied the dirty launch profile),
reconstructing the profile's full environment from `git show HEAD:.../launchSettings.json` with
only `APP_DOMAIN` and `ConnectionStrings:Default` restored to their committed values. No tracked
file was modified.

```
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~FuturePayTests" --nologo -v q
# Passed! - Failed: 0, Passed: 6, Skipped: 0, Total: 6, Duration: 10 s
```

### Elastic queries used

Indices `.ds-traces-apm*,traces-apm*` on `http://localhost:9200`, via Python `urllib` (curl is
blocked in this environment for Elastic). `timestamp.us` is epoch **microseconds**, not an ISO
string — `@timestamp` is not the field to filter on.

Find hook spans in the test window:

```python
import urllib.request, json
def es(path, body):
    req = urllib.request.Request(
        f"http://localhost:9200{path}", data=json.dumps(body).encode(),
        headers={"Content-Type": "application/json"})
    return json.loads(urllib.request.urlopen(req, timeout=30).read())

query = {
  "size": 30,
  "query": {"bool": {"filter": [
    {"prefix": {"span.name": "EventHook."}},
    {"range": {"timestamp.us": {"gte": 1787862420000000, "lte": 1787862600000000}}}
  ]}},
  "_source": ["trace.id", "span.id", "parent.id", "span.name", "timestamp.us", "labels"],
  "sort": [{"timestamp.us": "asc"}]
}
print(es("/.ds-traces-apm*,traces-apm*/_search", query))
```

22 `EventHook.*` spans came back across 6 traces (one per FuturePayTests test method), covering
`InstanceSubStateChangedEventHook` (`HandledOrFallback`), `InstanceCompletedCleanupEventHook`
(`HandledOrFallback`), and `InstanceSubCompletedEventHook` (`DurablePostCommit`).

### Trace of record: `4682ca695dac4f7021c1a1bc4419faa1`

(`SubmittingAnApplication_RunsTheBureauSubflowAndLandsOnAssessment` — bureau subflow completion.)

Subtree around the DurablePostCommit hook (offsets from trace start, all `service.name=vnext-app`
unless noted):

```
[  802.24ms] Uow.Commit                                span=f3c40a2161ec8c03  parent=2f675103953fefca
[  805.24ms]   └─ EventHook.InstanceSubCompleted        span=bb07bb774aa6e03a  parent=f3c40a2161ec8c03
                    vnext.event.name = InstanceSubCompletedEvent
                    vnext.hook.name  = InstanceSubCompletedEventHook
                    vnext.hook.mode  = DurablePostCommit
[  805.42ms]        ├─ Cache.Get/sys-flows:core:loan-disbursement:full:1.0.1-pkg.1.0.0
[  805.94ms]        └─ SubFlow.Completion/core/loan-disbursement
```

**Ask 1 — hook span's parent IS `Uow.Commit`, proven by span id:** `EventHook.InstanceSubCompleted`
(`bb07bb774aa6e03a`) has `parent.id = f3c40a2161ec8c03`, and `f3c40a2161ec8c03` is itself the span
named `Uow.Commit`. Confirmed directly from the raw documents, not inferred from timing.

The `HandledOrFallback` half, same trace:

```
[  791.23ms] Events.PublishDeferred                     span=345d3a8a0bd42a4e  parent=2f675103953fefca
[  791.32ms]   ├─ EventHook.InstanceSubStateChanged      span=740eccac6189962f  parent=345d3a8a0bd42a4e
                    vnext.hook.mode = HandledOrFallback
[  796.06ms]   └─ EventHook.InstanceCompletedCleanup     span=38ee456e941f22e1  parent=345d3a8a0bd42a4e
                    vnext.hook.mode = HandledOrFallback
```

Both parent to `Events.PublishDeferred` (`345d3a8a0bd42a4e`) exactly as documented in
[Trace/Span Tree](trace-span-tree.md).

**Tags — confirmed present on every one of the 22 hook spans:** `vnext.event.name`,
`vnext.hook.name`, `vnext.hook.mode` (surfaced by Elastic as `vnext_event_name` / `vnext_hook_name`
/ `vnext_hook_mode` — APM flattens label dots to underscores; the OTel attribute names are the
dotted `TelemetryConstants.TagNames` values).

**Ask 2 — client span as a CHILD of the hook span: NOT DEMONSTRATED, reported honestly.** Every
child of all 22 `EventHook.*` spans in this run is a local, in-process span —
`Cache.Get`, `SubFlow.StateChange`, `SubFlow.Completion`, `Db.SELECT`/`Db.UPDATE` — never an
`HttpClient` or `Dapr invoke` span. This is explained, not just observed: `InstanceSubCompletedEventHook`
and `InstanceSubStateChangedEventHook` both delegate to `IInstanceCommandGateway`, which "routes
between local and remote execution based on target domain" — and every FuturePayTests subflow in
this run is same-domain (`vnext.domain = core` on both the parent and the subflow), so the gateway
took the **local** path. `InstanceCompletedCleanupEventHook` does not call the gateway at all; its
children are its own DB cleanup. A cross-domain subflow completion would be needed to produce a
remote client span parented under a hook span — this environment's fixtures do not have one. The
mechanism (`Activity.Current` ambient parenting, verified above) attributes such a call correctly
when it happens; this run simply never made one.

### Regression guard: `EventTraceScope` still works

> **As captured, 2026-08 (see [Update](#update-2026-08-30-command-vs-fact-delivery-now-diverge)
> above):** this section's live query and its "same trace" conclusion are exactly what ran at the
> time — nothing here is altered. `InstanceSubCompletedEvent` has since moved to
> `EventTraceMode.IsolatedDelivery`, so re-running this today would show the `.Handle` transaction
> rooting its own trace instead of parenting onto `Events.PublishDeferred`. The regression guard
> this section demonstrates — that `EventTraceScope` re-parents onto the event's own `TraceParent`
> field at all — remains the mechanism `EventTraceMode.ContinueTrace` uses unchanged today for the
> three command events; only which events get that treatment has narrowed.

Broadening the search to `{Event}.Handle` in the inbox worker (`vnext-inbox-worker`) for the same
window finds `InstanceSubCompleted.Handle` transactions, one of them **in the exact same trace**
as the subtree above:

```python
query = {"size": 30, "query": {"bool": {"filter": [
  {"bool": {"should": [
    {"wildcard": {"span.name": "*.Handle"}},
    {"wildcard": {"transaction.name": "*.Handle"}}
  ], "minimum_should_match": 1}},
  {"range": {"timestamp.us": {"gte": 1787862300000000, "lte": 1787863200000000}}}
]}}, "_source": ["trace.id", "transaction.id", "parent.id", "transaction.name", "service.name"]}
```

```
trace=4682ca695dac4f7021c1a1bc4419faa1  svc=vnext-inbox-worker
  transaction=InstanceSubCompleted.Handle  id=f52f69cda8cb22ff  parent=345d3a8a0bd42a4e
     └─ span: Dapr invoke vnext-app
```

`f52f69cda8cb22ff`'s `parent.id` is `345d3a8a0bd42a4e` — the `Events.PublishDeferred` span from the
*same* trace as the hook subtree above, in a *different process* (`vnext-inbox-worker` vs.
`vnext-app`). This is `EventTraceScope` doing exactly what it is documented to do: it reads the
event payload's own `TraceParent` field (set by `HookedDistributedEventBus` at publish time,
independent of Task 2) and re-parents the handler span onto the originating trace. The dual-processing
pattern (Event Hook local + Event Handler distributed) is intact and both halves land in one trace
tree today, for the payload-level trace carrier. Bonus: the `Handle` transaction's own child, `Dapr
invoke vnext-app`, is exactly the "remote client call attributed to the right span" shape the brief
was checking for — just one level down from where it was expected (under `.Handle`, not under the
publish-side hook).

## Release gate

What is **not** true yet, and requires the next Aether release (package version above the
currently-consumed `1.0.36`) that includes commit `950931b`
(`feature/outbox-trace-continuity`, `burgan-tech/aether`):

1. **`outbox.message_id` on the origin span.** `EfCoreOutboxStore.StoreAsync` tags the *ambient*
   span (`EventBus.Publish`, nested under `Events.PublishDeferred`) with `outbox.message_id` at
   write time. Live-checked here and confirmed absent: `Events.PublishDeferred`
   (`345d3a8a0bd42a4e`) carries only `vnext.span_category` — no `outbox.message_id` tag, in the
   currently-running (nuget `1.0.36`) build.
2. **`Outbox.Process` re-joined to the origin trace.** Queried `service.name=vnext-worker-outbox`
   for the same window and found `Outbox.Process` transactions for `event_name=instance.sub.completed`
   messages produced by this very test run (e.g. `outbox_message_id=710e2db2-9816-4236-9aa6-d80a61086b7c`,
   trace `f82d5d97e94a…`) — every one of them a **root transaction in its own, disconnected trace**
   (`parent: None`), never sharing a trace id with `4682ca695dac4f7021c1a1bc4419faa1` or any of the
   other 5 origin traces from this run. This is the worker-loop parenting the pre-release code
   path produces, confirmed live rather than assumed.

   One label is *already* present today and easy to mistake for Task 2's evidence:
   `Outbox.Process` carries an `outbox_message_id` label of its own (`710e2db2-…`). That is a
   **pre-existing, unrelated vnext/aether tag on the processor's own span** — not the origin-span
   tag Task 2 adds. Task 2's contribution is the origin span (`EventBus.Publish`) getting that same
   id, and the two traces merging into one; neither is true yet.

Once the release lands, re-running the same queries should show: `Events.PublishDeferred` (or its
`EventBus.Publish` child) carrying `outbox.message_id`, and the corresponding `Outbox.Process`
transaction sharing `trace.id` with the origin — with the pre-release worker-loop parent demoted to
an `ActivityLink` rather than dropped.

### Operator-facing consequence — sampling volume

The Task 2 reviewer flagged this and it belongs where an operator reading dashboards will look
before being surprised by it: re-parenting `Outbox.Process` onto the origin trace means it now
**adopts the origin's sampling decision**. An origin that was sampled out produces an unsampled
`Outbox.Process` too — so outbox span volume in the backend can drop after this release, for
exactly the traces that were already being sampled out upstream. This is correct semantics (an
unsampled request's downstream work should not manufacture its own sampled trace), but it changes
what "outbox throughput" dashboards built purely from span counts will show. Any alert or dashboard
keyed on `Outbox.Process` span *count* as a proxy for outbox drain rate should move to a
sampling-independent signal (e.g. the outbox table's own row counters) before this release ships.

## Pre-deploy note

Rows written to the outbox **before** the Aether upgrade deploys carry no `TraceParent` in
`ExtraProperties` — the column/property did not exist yet when they were written. `OutboxProcessor`'s
`ActivityContext.TryParse` on a missing key simply fails, and the code falls through to the
pre-existing `parentContext = loopContext` — i.e., **old rows keep worker-loop parenting after the
deploy, by design.** This is not a bug to chase: it self-resolves as the outbox drains, and no
migration or backfill is needed. New rows written after the deploy get the full chain; rows already
sitting in the table at deploy time finish their life exactly as they do today.

## Related pages

- [Trace/Span Tree](trace-span-tree.md) — the full span tree this page's `EventHook.{name}` row
  belongs to, and the `Uow.Commit`/`Events.PublishDeferred` spans this page builds on.
- [Trace Lanes](trace-lanes.md) — the anchor/predecessor parenting model `EventTraceScope` and
  `BackgroundJobActivityHelper` both use to keep chained hops in one tree.
