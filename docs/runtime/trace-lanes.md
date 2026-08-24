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
`O(subflow nesting)`, independent of chain length. Inside each lane item the previous structure is
unchanged: `transition/{key}` → `Task.Execute.{key}` → HTTP/Dapr client span.

## Moving parts

| Piece | Location |
|---|---|
| Ambient anchor (`Current`, `ParentLane`, `Seq`) | `BBT.Workflow.Domain/Logging/WorkflowTraceLane.cs` |
| The whole parenting policy | `BBT.Workflow.Application/Telemetry/FlatLaneActivity.cs` |
| Job span | `BackgroundJobActivityHelper.StartFlatLaneActivity` |
| Inline chain hop span | `BBT.Workflow.Application/Telemetry/TransitionHopActivity.cs` |
| Transition span names | `BBT.Workflow.Application/Telemetry/TransitionSpanName.cs` |
| Lane-carrying events | `BBT.Workflow.Events.Contracts/Events/ILaneAwareDistributedEvent.cs` |

### Scope helpers — which one to call

- **`Use(anchor, parentAnchor, seq)`** — preserve-on-null. For code already running inside a live
  lane. A legacy payload with no anchor keeps the enclosing lane instead of starting a nested one.
- **`Reset(anchor, parentAnchor, seq)`** — set exactly, **clear** on null. The entry policy for job
  handlers and internal relay endpoints. Required there because a Dapr scheduler callback is itself
  an HTTP request, so the request middleware has already anchored the lane on the *callback* span —
  transport, not the originating business request. Inheriting it would make every legacy-payload hop
  look like a cross-trace anchor mismatch.
- **`EnterChildLane()`** — a handoff to ANOTHER instance running in this process.
  `Activity.Current` (the handing-off span) becomes that instance's anchor; the lane being left is
  remembered as `ParentLane`, and the ordinal restarts at 0 because the hops belong to a different
  instance. Two callers: a subflow handoff (`PostCommit.*` span) and a same-domain trigger task
  (`Task.Execute` span) — see below.

### Seeding points

| Where | What it does |
|---|---|
| `ParentInstanceIdEnrichmentMiddleware` | anchors on the ASP.NET server span (runs while it is `Activity.Current`) |
| `TransitionJobHandler` | `Reset` from `payload.TraceRoot` / `ParentTraceRoot` |
| `ForwardToSubflowJobHandler`, `StartSubflowJobHandler` | `EnterChildLane()` |
| `TriggerTaskExecutorBase.RouteAsync` (local branch only) | `EnterChildLane()` |
| `TransitionPipeline.RunChainAsync` (inline continuations) | `Use(seq: NextSeq())` + a `Transition.Hop` span |
| `EventTraceScope` (Inbox) | `Reset` from a lane-aware event, else the handler span |
| `internal/subflow-forward`, `/complete`, `/sub/fault` | `Reset` from the request body |

## Same-domain trigger tasks

A trigger-family task (StartTrigger, DirectTrigger, GetInstance, GetInstances, GetInstanceData,
SubProcess) whose target domain is this runtime's own dispatches **in-process** instead of over Dapr.
Anything the target instance then starts reads the ambient lane — which still belongs to the
**calling** instance's request — so its transition jobs and post-commit work anchored to the caller's
lane and surfaced as siblings of the caller's own hops, with nothing tying them to the task that
triggered them.

`TriggerTaskExecutorBase.RouteAsync` therefore calls `EnterChildLane()` on the **local branch only**,
making the `Task.Execute` span the target instance's anchor. The triggered work is then flat
underneath the task, exactly as a subflow handoff behaves.

- The **remote** branch is deliberately left alone: it crosses Dapr and the invoker stamps the lane
  into the request as `TraceRoot`/`ParentTraceRoot`. Entering a child lane there would re-anchor it
  onto this process's task span.
- `IsSameDomain` is **private** to the base class, so a new executor cannot branch on the domain
  itself and skip the lane.
- For the read-only members of the family the child lane is inert today — a read starts no lane-aware
  span — but it is still entered so the policy is uniform.

## Inline chain hops

With `WorkflowExecution:AutoTransitionMode = Inline` (the default) a chained transition runs
in-process rather than as its own scheduler job, so there is no job handler to open its span.
`TransitionPipeline.RunChainAsync` opens one per continuation via `TransitionHopActivity`, which goes
through `FlatLaneActivity` like every other lane item: anchor-parented, predecessor linked, `LaneSeq`
advanced, `Consumer` kind so apm-server still counts a transaction.

- **Hop 0 gets no span of its own** — the caller's span already represents it.
- **Sync chains get none at all.** They have always chained in-process without per-hop spans; adding
  them would invent transactions that never existed.
- Full guide: `docs/architecture/async-transition-execution-modes.md`.

## Transition span names

Both transition-hop spans are named `{prefix}/{domain}/{flow}/{transition}` — built in one place,
`TransitionSpanName`, whose two prefix constants are `TransitionJob.Execute` (ran as a scheduler job)
and `Transition.Hop` (ran inline). Without the suffix a five-hop chain is five identically-named
spans, readable only by opening each one.

All three segments are **definition-level** identifiers, so the name stays low-cardinality and safe
as an APM transaction name. **Never append anything per-instance** (instance id, correlation id, job
name): apm-server groups transactions by name, and an unbounded name turns one transaction into
millions. Those belong in tags, where they already are.

> Dashboards filtering `name == "TransitionJob.Execute"` match nothing after this change — switch to
> a prefix match, and add the `Transition.Hop` prefix to keep counting chained hops.

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

- `docs/architecture/async-transition-execution-modes.md` — `AutoTransitionMode` and the trace shape both modes share
- `docs/runtime/state-function-cache-and-etag.md`
- `.claude/rules/vnext-workflow-developer.md` — pipeline step order, subflow lifecycle
