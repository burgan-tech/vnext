# Async Transition Execution Modes

## Purpose

An async transition (`sync=false`) is accepted, enqueued as a scheduler job, and executed
in the background. When that transition's pipeline finishes, it may ask for another one —
an automatic transition it satisfied, an error-boundary rule's replacement transition, or
an `updateData` handoff. This page documents how that **chained continuation** is realized,
which is the single knob async execution still has.

`sync=true` bypasses jobs entirely: the pipeline runs in-process and the response carries
the full instance. Nothing here applies to synchronous calls — they have always chained
in-process.

## Configuration

```jsonc
"WorkflowExecution": {
  "TransitionJobTimeoutSeconds": 300,
  "AutoTransitionMode": "Inline",   // Inline (default) | Scheduled
  "FailurePolicy": { "MaxRetries": 5, "IntervalSeconds": 30 }
}
```

Source of record:
`src/BBT.Workflow.Application/BackgroundJobs/Options/WorkflowExecutionOptions.cs`.

| Mode | Behavior |
| --- | --- |
| `Inline` (default) | The next transition runs **in-process**, inside the job already executing. The chain advances at memory speed. |
| `Scheduled` | The next transition is enqueued as **its own scheduler job** — a separate unit of work and a durable per-hop checkpoint — at the cost of one scheduler round trip per hop. |

The default is set in code, not in `appsettings.json`, so a host that never writes the key
still gets the low-latency path.

### What the setting does NOT affect

- **Authored `triggerType: 2` scheduled transitions.** Those are armed by
  `ScheduleTransitionsStep` (order 80) and are always real scheduler jobs. The name
  collision is unfortunate; the two are unrelated.
- **Sync transitions.** `SyncTransitionStrategy` does not read the setting.
- **The initial accept.** An async transition is always accepted by enqueuing one job. Only
  its *continuations* are governed here.

### Scope: every chained continuation, not only automatic ones

The setting is read once, in `TransitionJobHandler`, and projected onto
`WorkflowExecutionContext.EnqueueContinuations`. That flag drives
`ContinuationDispatcher`, which every `Directives.NextTransition` goes through — an
automatic transition from `RunAutomaticTransitionsStep`, an error-boundary rule's
replacement transition, and an `updateData` handoff alike. There is deliberately ONE
decision point rather than a second branch to keep in sync.

## Why Inline is the default

In `Scheduled` mode a three-hop auto-chain costs three scheduler round trips before the
instance reaches a state the client can act on. A UI client long-polling the state function
observes that as screen latency, and it accumulates with chain length. `Inline` removes it:
the chain runs to its resting state inside the job that is already executing, and the first
`200` from the state function carries the settled instance.

## Trade-offs of Inline

`Scheduled` remains available because `Inline` gives up real properties:

| Property | `Inline` | `Scheduled` |
| --- | --- | --- |
| Latency per chained hop | in-process | one scheduler round trip |
| Durable per-hop checkpoint | no — the accept's single `InstanceJob` row covers the whole chain | yes — one row per hop |
| Crash mid-chain | the instance stays Busy under the accept's row; recovery faults it, and the chain restarts from the accept rather than resuming at the last committed hop | resumes at the next un-run hop |
| Execution budget | `TransitionJobTimeoutSeconds` covers the WHOLE chain | covers one hop |
| Transaction granularity | one UoW per post-commit stage, so a chain crossing no post-commit barrier commits as one transaction | one UoW per hop |

Two consequences worth planning for:

- **Long chains and the timeout.** A chain whose total work approaches
  `TransitionJobTimeoutSeconds` (default 300s) will exhaust the budget and be routed to
  recovery. Size the budget against the chain, not against a single transition.
- **The `updateData` Busy probe cannot see an inline chain.**
  `TransitionPipeline.HasLiveTransitionOwnerAsync` distinguishes "Busy with a live owner"
  from "Busy parked at an auto-gated rest state" by looking for an active `InstanceJob` row.
  An inline chain leaves no per-hop row, so it is invisible to that probe and an
  `updateData` arriving mid-chain is more likely to take over than to drop. That is
  behaviorally correct — the takeover is an idempotent flip under the same short status
  lock — but it is a real difference from `Scheduled`.

## Delivery: the scheduler, and only the scheduler

When a continuation *is* enqueued (`Scheduled` mode) or an async transition is accepted,
delivery goes through `ITransitionEnqueueGateway` straight to the scheduler. There is no
second path.

The transactional-outbox alternative — publish a continuation event, let the Outbox worker
publish it, the Inbox relay forward it, and Orchestration finally enqueue the job — has
been removed. It bought durability with three extra hops of latency on a path whose whole
purpose is to be fast, and it was only ever reached as a fallback, which made it a
rarely-exercised second code path for the same outcome.

### Failure contract

Because nothing backstops a failed schedule any more, failures are reported rather than
deferred:

1. The gateway retries the scheduler briefly — 3 attempts, 50ms then 100ms backoff. A
   sidecar restart or a reset connection is normal and self-clearing; this absorbs it. The
   budget is intentionally not configurable, because the accept path calls the gateway
   while holding the instance status lock.
2. On exhaustion the gateway returns a failed `Result`, and both callers honour it:
   - `EnqueueContinuationStrategy` propagates it, so the pipeline faults the instance —
     visible and retryable — instead of committing a durable intent nothing will ever arm
     and leaving the instance parked in Busy with no owner.
   - `AsyncTransitionStrategy` fails the accept and leaves its unit of work uncommitted, so
     the caller learns the transition was not accepted instead of getting a `202` for work
     that may never be delivered. Leaving the intent behind would be worse than losing it:
     the duplicate-job guard would block every later retry of a transition that never ran.

## Tracing

Both modes produce the **same trace shape**, and that is deliberate — switching modes must
not move dashboards.

In `Scheduled` mode each hop is a job and gets a `TransitionJob.Execute/...` span from
`BackgroundJobActivityHelper.StartFlatLaneActivity`. In `Inline` mode each continuation gets
a `Transition.Hop/...` span from `TransitionHopActivity`. Both go through `FlatLaneActivity`,
the single home of the lane parenting policy, so both are:

- **parented to the lane anchor**, with the predecessor hop attached as an `ActivityLink`.
  Hops are SIBLINGS; parenting hop N+1 under hop N would make trace depth equal chain
  depth, which is exactly what the lane model exists to prevent.
- **ordered by `LaneSeq`**, advanced per hop. `ChainDepth` resets to 0 at subflow-resume,
  long-poll, timeout and retry boundaries, so it cannot order a lane on its own.
- **`ActivityKind.Consumer`**, so apm-server keeps classifying a chained transition as a
  transaction. An inline hop is not really a message consumer; the parity is worth more than
  the purity here, because `Internal` would silently drop every chained transition out of
  transaction counts and alerts built while those hops were jobs.

Two deliberate differences from the job path:

- `messaging.*` tags and the job name are absent — there is no broker and no job behind an
  inline hop.
- The span's prefix is `Transition.Hop`, **not** `TransitionJob.Execute`, so "how many
  transition jobs ran" stays answerable from traces.

### Span names carry domain/flow/transition

Both spans are named `{prefix}/{domain}/{flow}/{transition}` — e.g.
`TransitionJob.Execute/banking/loan-application/approve` — following the convention the
`SubFlow.*` spans already use. Without it a five-hop chain is five identically-named spans,
readable only by opening each one and reading its tags.

All three segments are **definition-level** identifiers, so the name stays low-cardinality
and safe as an APM transaction name. Nothing per-instance is ever appended (instance id,
correlation id, job name): apm-server groups transactions by name, and an unbounded name
turns one transaction into millions. Those stay in tags, where they already are.

> **Dashboards — two changes, both breaking for exact-name filters:**
>
> 1. A filter written as `name == "TransitionJob.Execute"` now matches **nothing**. Switch to
>    a prefix match (`name : "TransitionJob.Execute/*"`), or group by the prefix.
> 2. Add the `Transition.Hop` prefix alongside it to keep counting chained hops once a domain
>    runs in `Inline` mode.
>
> `TransitionSpanName` is the single place both names are built, so a query written against
> its two prefix constants stays correct.

Hop 0 never gets a span of its own — the caller's span (`TransitionJob.Execute/...` on the async
path) already represents it. Sync chains get no hop spans at all: they have always chained
in-process without them, and adding them would invent transactions that never existed.

## Choosing a mode

Stay on `Inline` unless per-hop durability is worth a scheduler round trip per hop. Reach
for `Scheduled` when:

- a chain's hops each do expensive, non-idempotent work you do not want to redo after a
  crash, or
- total chain work approaches `TransitionJobTimeoutSeconds` and splitting the budget per
  hop is easier than raising it, or
- you need per-hop `InstanceJob` rows for operational visibility into where a chain is.

The setting is per host, applied at the next restart. It changes no schema and no stored
data, so switching back and forth is safe: in-flight work finishes under the mode it
started in.

## Related

- [Workflow Execution Pipeline](workflow-execution-pipeline.md) — the step order a single hop runs
- `.claude/rules/vnext-workflow-developer.md` — locking model and pipeline quick reference
