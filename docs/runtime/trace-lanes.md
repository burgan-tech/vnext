# Trace Lanes — flat span topology for transition chains and subflows

## Why this exists

A vNext business request used to produce a deeply **nested** trace. Each auto-chained transition hop
enqueued the next one while stamping `Activity.Current` into the job payload as `TraceParent`, and the
job handler then used that value as the span's **real parent**. Nesting depth therefore equalled chain
depth. Add a subflow and the tree became unreadable — which made finding the failing hop expensive.

The fix separates two things that were conflated in one field:

| | Field | Role |
|---|---|---|
| **Anchor** | `TraceRoot` | the span the hop's own span is **parented** to |
| **Predecessor** | `TraceParent` | the previous hop, attached as an `ActivityLink` |

Causality is preserved (link + `vnext.hop.predecessor` tag) while the spans render as siblings.

## The model: one lane per instance

```
PATCH .../instances/{id}/transitions/{key}      ← APM transaction, anchors the root lane
├── TransitionJob.Execute        (hop 1)
├── TransitionJob.Execute        (hop 2)        ← sibling of hop 1, not its child
├── PostCommit.ForwardToSubflowJob              ← anchors the SUBFLOW's lane
│   ├── TransitionJob.Execute    (subflow hop 1)
│   └── TransitionJob.Execute    (subflow hop 2)
├── SubFlow.Resume/{domain}/{flow}              ← back in the parent's lane
└── TransitionJob.Execute        (parent resume)
```

A new lane opens **only** at a subflow handoff, never at a service boundary. So depth is
`O(subflow nesting)`, independent of chain length. Inside each lane item the structure is
`TransitionJob.Execute/{key}` → `Step.*` → `Task.Execute.{key}` → HTTP/Dapr client span. The lane
span itself is named after the transition it runs, and there is no separate `transition/{key}`
node any more — see [Trace Span Tree](trace-span-tree.md).

## Moving parts

| Piece | Location |
|---|---|
| Ambient anchor (`Current`, `ParentLane`, `Seq`) | `BBT.Workflow.Domain/Logging/WorkflowTraceLane.cs` |
| The whole parenting policy | `BBT.Workflow.Application/Telemetry/FlatLaneActivity.cs` |
| Job span | `BackgroundJobActivityHelper.StartFlatLaneActivity` |
| Lane-carrying events | `BBT.Workflow.Events.Contracts/Events/ILaneAwareDistributedEvent.cs` |
| Activation episode record (`StartedAt`, `Trigger`, `TransitionKey`, `Partial`) | `BBT.Workflow.Domain/Logging/ActivationEpisode.cs` |
| The synthetic `Instance.Activation/{key}` span | `BBT.Workflow.Application/Telemetry/ActivationActivity.cs` |
| Rebuilding the episode from a carrier's four fields | `BBT.Workflow.Application/Telemetry/ActivationEpisodeCarrierExtensions.cs` |
| The settlement verdict that closes an episode | `Execution/Transitions/Pipeline/TransitionSettlement.cs` → `ActivationVerdict` on `PipelineDirectives.Activation` |

### Scope helpers — which one to call

- **`Use(anchor, parentAnchor, seq, episode)`** — preserve-on-null. For code already running inside
  a live lane. A legacy payload with no anchor keeps the enclosing lane instead of starting a nested
  one. The `episode` argument follows the same rule: null keeps the enclosing episode.
- **`Reset(anchor, parentAnchor, seq, episode)`** — set exactly, **clear** on null. The entry policy
  for job handlers and internal relay endpoints. Required there because a Dapr scheduler callback is
  itself an HTTP request, so the request middleware has already anchored the lane on the *callback*
  span — transport, not the originating business request. Inheriting it would make every
  legacy-payload hop look like a cross-trace anchor mismatch. A null `episode` clears too, so a
  payload from a build that predates episodes does not inherit the callback request's.
- **`UseCurrentActivity(trigger = http)`** — anchors on `Activity.Current` **and** opens an
  activation episode whose start is that span's `StartTimeUtc`. The request middleware's call: the
  episode begins the instant the request arrived, before any endpoint code ran.
- **`UseEpisode(trigger, transitionKey)`** — classify-once. Keeps anchor, parent and seq and does
  **not** move the start; replaces the trigger only while it is still `http` (an event delivery that
  classified itself `event` is not overwritten with `manual` when it re-enters the generic
  transition entry point); refreshes the key whenever one is supplied; seeds a fresh episode
  starting now when none is ambient.
- **`EnterChildLane(restartTrigger = null)`** — subflow handoff. `Activity.Current` (the
  `PostCommit.*` span) becomes the child instance's anchor; the lane being left is remembered as
  `ParentLane`. The episode is **inherited** when `restartTrigger` is null and **restarted** at the
  handing-off span when one is given — see [Activation episode](#activation-episode) below.

### Seeding points

| Where | Anchor | Episode |
|---|---|---|
| `ParentInstanceIdEnrichmentMiddleware` | anchors on the ASP.NET server span (runs while it is `Activity.Current`) | `UseCurrentActivity()` — seeds `http`, start = the server span's start |
| `InstanceCommandAppService.StartAsync` / `.TransitionAsync`, `EventAppService`, `InstanceRetryAppService`, `LongPollAckResumeService` | — (already anchored by the middleware) | `UseEpisode(start \| manual \| event \| retry \| ack, key)` — classifies the `http` episode without moving its start; `TransitionAsync`'s `manual` loses to an earlier `event` |
| `TransitionTimerJobHandler`, `FlowTimeoutJobHandler`, `LongPollAckTimeoutJobHandler` | none — deferred payloads carry no anchor (see Safety rules) | `UseEpisode(scheduled \| timeout \| ack-timeout, key)` — opens its **own** episode at the Dapr callback span; the client's question here is "fire → Active" |
| `TransitionJobHandler` | `Reset` from `payload.TraceRoot` / `ParentTraceRoot` / `LaneSeq` | `Reset` from `payload.EpisodeStartedAt` / `EpisodeTrigger` / `EpisodeTransitionKey` / `EpisodeTraceRoot` (`ToActivationEpisode()`); a payload with a null start seeds a `Partial` `job` episode at the job span |
| `ForwardToSubflowJobHandler`, `StartSubflowJobHandler` | `EnterChildLane()` | **inherited** from the parent lane |
| `TriggerTaskExecutorBase` (trigger-family tasks) | `EnterChildLane(trigger)` | **restarted** at the invocation span |
| `EventTraceScope` (Inbox) | `Reset` from a lane-aware event, else the handler span | `Reset` from the event's three episode fields (null clears) |
| `internal/subflow-forward`, `/complete`, `/sub/fault`, `/sub/cancel` | `Reset` from the request body | `Reset` from the body's three episode fields |
| `StateNotifyJobHandler` | `Reset` from `payload.TraceRoot` / `ParentTraceRoot` | none — a notification is not a rest point |

## Safety rules

- **The lane never travels in a request header.** Cross-domain handoff rides in internal-only bodies
  (`SubflowForwardInput`, `FlowCompletedInput`, `SubFlowFaultedInput`) alongside `CorrelationId`. A
  public endpoint accepting a lane would let any caller graft spans onto an unrelated trace.
- **An anchor from another trace is linked, never parented** (`vnext.trace.lane.mismatch`), so a stale
  `AsyncLocal` or a relayed payload from an unrelated request cannot teleport a span. The comparison
  is against the *predecessor* only — a foreign **ambient** span is normal on the job path (the Dapr
  callback is its own trace) and is retained as searchable
  `vnext.dapr.callback.trace_id` / `.span_id` tags with `vnext.dapr.callback=true`.
- **No anchor ⇒ exactly the pre-lane behaviour**, plus `vnext.trace.lane=false`. Both payloads and
  events degrade in either direction, so deploy order is unconstrained. No DB migration: job payloads
  live in the Dapr scheduler store and outbox events in a serialized blob.
- **Deferred jobs never carry an anchor** (timer, timeout, long-poll ack). `ITraceableJobPayload`
  exposes `TraceRoot` as a default interface member returning null for exactly this reason —
  resurrecting an hours-old anchor would produce an hours-long trace. The same holds for the
  episode fields (`EpisodeStartedAt` / `EpisodeTrigger` / `EpisodeTransitionKey` / `EpisodeTraceRoot`, also default
  null): a deferred job opens its own episode when it fires.
- **A timestamp is not an anchor.** The carried episode start cannot graft spans onto another
  trace, so it needs none of the header protection the anchor has — it still rides only the
  internal bodies, beside the anchor, for symmetry, and is never read from a request header.
- **`state.notify` is a lane item too.** `StateNotifyPayload` carries `TraceRoot` /
  `ParentTraceRoot`, filled by `StateNotificationScheduler`; `StateNotifyJobHandler` `Reset`s the
  lane and opens `StateNotify.Execute` via `StartFlatLaneActivity`, so the notify job is a sibling
  of the hop that scheduled it (that hop linked as predecessor) rather than nested under it. A
  payload without an anchor (older build) degrades to exactly the previous
  continue-the-predecessor parenting.

## Span kinds

`kind` is always the caller's choice, never forced. Job spans stay `Consumer` so Elastic APM keeps
classifying them as transactions (apm-server keys off `SpanKind`, and OTLP carries no
parent-is-remote flag — re-parenting onto a local anchor does not change the classification).
In-process lane items (`PostCommit.*`, `SubFlow.Resume`) are `Internal`, so transaction counts and
service-map edges do not move.

## Tags

`vnext.trace.lane` · `vnext.trace.lane.anchor` · `vnext.trace.lane.mismatch` ·
`vnext.hop.predecessor` (primary causality; self-joins into a chain even where a UI hides links) ·
`vnext.lane.seq` (reliable ordinal — `vnext.chain.depth` resets to 0 at every resume/timeout/retry
boundary) · `vnext.chain.depth` · `vnext.root.instance.id` (stamped unconditionally on lane spans: the
single filter that selects a whole business request).

Episode tags, on `Instance.Activation/{key}`: `vnext.activation.outcome` · `vnext.activation.trigger`
· `vnext.activation.transition.key` · `vnext.activation.hops` (the settling hop's `vnext.lane.seq`) ·
`vnext.activation.duration_ms` · `vnext.activation.partial` · `vnext.activation.clock_skew`. On
`Transition.Settle`: `vnext.settle.cas` (`flipped` | `lost` | `skipped`) · `vnext.activation.emitted`.

## Activation episode

**Definition.** An *activation episode* runs from a **trigger** to the instance's next **rest
point**. Triggers: the HTTP start/transition request, a timer or timeout firing, an event delivery,
a retry, a long-poll ack, a subflow resume, a child start. Rest points — each a status a client can
observe through the state function:

| `vnext.activation.outcome` | Rest point |
|---|---|
| `active` | the Busy→Active compare-and-set at `Transition.Settle` flipped **and committed** |
| `completed` / `canceled` | `HandleFinishStep` completed or cancelled the instance (a cancel/exit transition or a `Cancelled`-subtype target ⇒ `canceled`) |
| `faulted` | `Instance.Fault` (pipeline failure), a post-commit parent fault, or job-timeout recovery |
| `busy.parked` | the instance rests Busy at a state whose automatic transitions did not fire |
| `busy.subtype` | the instance rests in a `Busy`-subtype state, awaiting an external signal |

Every episode is one trace (the lane model already guaranteed that) **and** one
`Instance.Activation/{key}` span whose duration is trigger → rest point — `{key}` is the settling
hop's transition key, falling back to the episode's first-hop key, then `resume`. The original
first-hop key remains available as `vnext.activation.transition.key`. The span is
synthetic and backdated; why it has to be, and why its kind is `Internal`, is in
[Trace Span Tree § Activation episode](trace-span-tree.md#activation-episode-why-the-span-is-synthetic).

**Where the start lives.** On the lane: `WorkflowTraceLane.Episode` is an
`ActivationEpisode(StartedAt, Trigger, TransitionKey, Partial)` held in the same `AsyncLocal` as the
anchor, so it flows through inline auto-chain hops, the post-commit barrier and the terminal relay
on its own. Only the async boundaries that already carry the anchor need fields — **four nullable
ones, always copied together**, beside `TraceRoot` / `ParentTraceRoot`:

| Carrier | Filled from the lane by | Restored by |
|---|---|---|
| `TransitionJobPayload` (`ITraceableJobPayload` defaults null) | `AsyncTransitionStrategy.BuildDirectPayload`, `EnqueueContinuationStrategy` | `TransitionJobHandler` → `Reset(…, payload.ToActivationEpisode())` |
| `TransitionContinuationRequested` (`ILaneAwareDistributedEvent`) | the same two enqueue sites; `TraceStampingDistributedEventBus` additionally fills any lane-aware event `??=`-style, never overwriting a preset value | Inbox `EventTraceScope`; the `/enqueue` relay copies them onto the job payload |
| `InstanceSubCompletedEvent` / `InstanceSubFaultedEvent` / `InstanceSubCanceledEvent` | `TraceStampingDistributedEventBus` | `SubflowTerminalRelay` and the Inbox `InstanceSub*EventHandler`s map them onto the inputs below |
| `FlowCompletedInput` / `SubFlowFaultedInput` / `SubItemCanceledInput` | the relay / inbox mappings above | `internal/…/complete`, `/sub/fault`, `/sub/cancel` → `Reset`; the `Subflow*Service`s copy them back onto the event republished by a terminal revert |
| `SubflowForwardInput` | `RemoteInstanceCommandAppService` | `internal/subflow-forward` → `Reset` |
| Cross-domain child start body (`CreateSubInstanceDto`) | `RemoteInstanceCommandAppService.StartSubAsync` | `sub/instances/start` → `Use` the carried episode while preserving the child server-span anchor |

Cross-domain child starts carry only the four episode fields, never the lane anchor. The child
therefore remains rooted under its own `sub/instances/start` server span, while its synthetic
activation duration starts with the parent episode. A same-domain child inherits both through
`EnterChildLane()`.

**Inherit vs restart.** A subflow handoff (`StartSubflowJobHandler`, `ForwardToSubflowJobHandler`
→ `EnterChildLane()`) **inherits** the parent's episode: the client polls the parent, which reports
the leaf's status, so the child's time-to-Active is measured from the parent's request. The parent
**resume** inherits the child's terminal-event episode for the same reason. A trigger-family task
(`TriggerTaskExecutorBase` → `EnterChildLane(trigger)`) **restarts** it — nobody waiting on this
instance observes the triggered one. Deferred jobs (timer, timeout, ack-timeout) open their **own**
episode at the callback span: their trace is deliberately separate from the request that armed
them, and the client's question there is "fire → Active".

**Who emits — and who never does.**

- The verdict is decided once per hop in `TransitionSettlement.ResolveVerdict` and recorded on
  `PipelineDirectives.Activation`; the span is emitted **after the commit** — `TransitionRunner`
  right after `Uow.Commit` (adding `instance.available.committed` to the transaction on a flip),
  `PostCommitParentMutationService.MutateFreshAsync` after its own commit, `Instance.Fault` and
  `JobTimeoutRecoveryService.FaultInstanceAsync` after their fault commits. At `Transition.Settle`
  the flip is not durable yet; a client sees Active only after the commit.
- **Only status owners emit** (`OwnsStatus`). A non-owning execution beside an in-flight chain — an
  `updateData` on a Busy parent, a forwarded request — leaves the verdict to the owner.
- **A hop that enqueued a continuation never emits.** `EnqueueContinuationStrategy` marks
  `Directives.ContinuationEnqueued`; `TransitionPipeline` passes `chainSettled: !hadNextTransition`
  and the post-commit settlement `chainSettled: !continuations.ContinuationEnqueued &&
  instance.IsBusy`. The job it becomes carries the episode and settles it.
- **A parent handing off to a live SubFlow never emits.** It is still Busy, so `busy.subflow`
  would falsely mark the activation complete. The child inherits the episode and its activation
  span records the surface that actually becomes available.
- **CAS lost ⇒ no verdict** (`vnext.settle.cas=lost`): the row was no longer Busy; whoever flipped
  it emits.
- **Fresh post-commit parent not Busy ⇒ no verdict**: a synchronous child callback already settled
  the parent — and closed the episode — before the outer post-commit ran.
- **Already Active before the hop** (a status-neutral owner such as a retry landing on a resting
  instance) ⇒ no verdict: nothing became available here.
- `Instance.Fault` emits `faulted` regardless of `OwnsStatus`: whatever the execution owned, the
  instance now rests Faulted because of it.

**Degradation tags.** `vnext.activation.partial=true` — the start was not carried to the settling
hop (payload / event / body from an older producer, or an entry point that seeded none); the span
covers only that hop, and is excluded from the `workflow_activation_duration_ms` histogram.
`vnext.activation.clock_skew=true` — the carried start lay in the future of the settling replica's
clock; the span is clamped to zero length rather than reported negative, and likewise excluded.
Alert on the flags, not on the numbers.

## Known cosmetic effect

On the **sync** path `PostCommit.*` is a sibling of the still-open transaction span (the HTTP server
span, or the job span on the async path), so it renders as overlapping it. Valid OpenTelemetry, and
the price of having post-commit work at lane level. On the **async** path the hops likewise start
after the 202 transaction has ended. The backdated `Instance.Activation/{key}` span now gives such a
trace a readable total: it starts with the transaction and ends after the last hop, so the waterfall
shows one bar that is the client's wait, instead of a short transaction followed by unrelated-looking
siblings. In Elastic the axis extends to the latest-ending span (`getWaterfallDuration` =
max(offset + duration)), so the whole episode is visible.

## Related

- [Trace Span Tree](trace-span-tree.md) — every span name, source and tag, including the
  `Instance.Activation/{key}` row and the accept/start gap spans.
- [Correlation and Tracing](../monitoring/correlation-and-tracing.md) — carriers, job
  trace-continuation matrix, reserved-header rule for cross-domain calls.
- `docs/runtime/state-function-cache-and-etag.md`
- `.claude/rules/vnext-workflow-developer.md` — pipeline step order, subflow lifecycle
