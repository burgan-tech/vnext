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
`O(subflow nesting)`, independent of chain length. Inside each lane item the structure is:
`transition/{key}` → (`OnExit.{state}` / `OnEntry.{state}` lifecycle groups) → `Task.Execute.{key}`
→ (`Task.PrepareInput` / `Task.Invoke` / `Task.ProcessOutput` phases) → HTTP/Dapr client span →
`Execution.Invoke.{type}/{key}` (server) → `Task.Invoke.{type}/{key}` (invoker). The arming of
scheduled/auto/timeout continuations is visible as events on the arming transition's span
(`transition.scheduled`, `transition.auto.selected`, `flow.timeout.scheduled`) — the continuation
itself stays where this lane model puts it (sibling hop, or a linked new trace for deferred jobs).
See `docs/monitoring/correlation-and-tracing.md` § "Span inventory on the task path".

## Moving parts

| Piece | Location |
|---|---|
| Ambient anchor (`Current`, `ParentLane`, `Seq`) | `BBT.Workflow.Domain/Logging/WorkflowTraceLane.cs` |
| The whole parenting policy | `BBT.Workflow.Application/Telemetry/FlatLaneActivity.cs` |
| Job span | `BackgroundJobActivityHelper.StartFlatLaneActivity` |
| Lane-carrying events | `BBT.Workflow.Events.Contracts/Events/ILaneAwareDistributedEvent.cs` |

### Scope helpers — which one to call

- **`Use(anchor, parentAnchor, seq)`** — preserve-on-null. For code already running inside a live
  lane. A legacy payload with no anchor keeps the enclosing lane instead of starting a nested one.
- **`Reset(anchor, parentAnchor, seq)`** — set exactly, **clear** on null. The entry policy for job
  handlers and internal relay endpoints. Required there because a Dapr scheduler callback is itself
  an HTTP request, so the request middleware has already anchored the lane on the *callback* span —
  transport, not the originating business request. Inheriting it would make every legacy-payload hop
  look like a cross-trace anchor mismatch.
- **`EnterChildLane()`** — subflow handoff. `Activity.Current` (the `PostCommit.*` span) becomes the
  child instance's anchor; the lane being left is remembered as `ParentLane`.

### Seeding points

| Where | What it does |
|---|---|
| `ParentInstanceIdEnrichmentMiddleware` | anchors on the ASP.NET server span (runs while it is `Activity.Current`) |
| `TransitionJobHandler` | `Reset` from `payload.TraceRoot` / `ParentTraceRoot` |
| `ForwardToSubflowJobHandler`, `StartSubflowJobHandler` | `EnterChildLane()` |
| `EventTraceScope` (Inbox) | `Reset` from a lane-aware event, else the handler span |
| `internal/subflow-forward`, `/complete`, `/sub/fault` | `Reset` from the request body |

## Safety rules

- **The lane never travels in a request header.** Cross-domain handoff rides in internal-only bodies
  (`SubflowForwardInput`, `FlowCompletedInput`, `SubFlowFaultedInput`) alongside `CorrelationId`. A
  public endpoint accepting a lane would let any caller graft spans onto an unrelated trace.
- **An anchor from another trace is linked, never parented** (`vnext.trace.lane.mismatch`), so a stale
  `AsyncLocal` or a relayed payload from an unrelated request cannot teleport a span. The comparison
  is against the *predecessor* only — a foreign **ambient** span is normal on the job path (the Dapr
  callback is its own trace) and is demoted to a link with `vnext.dapr.callback`.
- **No anchor ⇒ exactly the pre-lane behaviour**, plus `vnext.trace.lane=false`. Both payloads and
  events degrade in either direction, so deploy order is unconstrained. No DB migration: job payloads
  live in the Dapr scheduler store and outbox events in a serialized blob.
- **Deferred jobs never carry an anchor** (timer, timeout, long-poll ack). `ITraceableJobPayload`
  exposes `TraceRoot` as a default interface member returning null for exactly this reason —
  resurrecting an hours-old anchor would produce an hours-long trace.

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

## Known cosmetic effect

On the **sync** path `PostCommit.*` is now a sibling of the still-open `transition/{key}` span, so it
renders as overlapping it. Valid OpenTelemetry, and the price of having post-commit work at lane level.

## Related

- `docs/runtime/state-function-cache-and-etag.md`
- `.claude/rules/vnext-workflow-developer.md` — pipeline step order, subflow lifecycle
