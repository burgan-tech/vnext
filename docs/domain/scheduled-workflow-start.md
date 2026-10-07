# Scheduled Workflow Start

## Purpose

Start a new workflow instance when a schedule fires — "every 15 minutes", "every weekday at 11:35".

The runtime exposes one endpoint that a scheduler POSTs to. It owns no cron expressions, no
timezones, and no registry of schedules. The **domain owns the schedule**, as one Dapr component YAML
per schedule — the same boundary [event-driven workflows](event-driven-workflows.md) draw for topics.

```
POST /api/v1/{domain}/workflows/{workflow}/instances/schedule
```

## Why this is not the event endpoint

`instances/events?action=start` also creates instances, and a cron binding can technically be pointed
at it. It is the wrong tool here for one practical reason: `action=start` requires the workflow to
declare an `event.mapping` script, whose job is to read a correlation key out of the message. A tick
has no message. Every schedulable workflow would have to ship a script purely to invent a key.

This endpoint needs no mapping, no `event` block, and no script. A workflow is schedulable as it is.

| | `instances/events?action=start` | `instances/schedule` |
| --- | --- | --- |
| Trigger | a message somebody published | a time arriving |
| Requires `event.mapping` on the workflow | yes | no |
| Where the body comes from | the message | the query string (a tick has no body) |
| Instance key | computed by the mapping script | derived from the tick |

## The component

```yaml
apiVersion: dapr.io/v1alpha1
kind: Component
metadata:
  name: rezervation-daily-1135
spec:
  type: bindings.cron
  version: v1
  metadata:
    - name: schedule
      value: "CRON_TZ=Europe/Istanbul 0 35 11 * * *"
    - name: direction
      value: "input"
    - name: route
      value: "/api/v1/morph-touch/workflows/rezervation/instances/schedule?scheduleId=daily-1135"
scopes:
  - vnext-app
```

Shipping a new schedule is one more YAML. No runtime change, no redeploy of vNext.

### Why the route carries everything

A cron component's own metadata **never reaches the application**. The binding builds its own metadata
map and sends exactly two keys (`readTimeUTC`, `timeZone`); `schedule`, `direction` and any custom key
you add are consumed by Dapr and dropped. A `workflow: rezervation` entry would be silently ignored.

So the domain, the workflow and the schedule identity all travel in `route`, which Dapr does honour —
including a query string, exactly as a pub/sub `Subscription` route does.

`direction: input` is a Dapr-defined key that marks the binding as a trigger. It is not a free slot.

## Seed data

The tick has an empty body (`content-length: 0`), so initial instance data travels as query
parameters. Every parameter except the reserved ones becomes an instance attribute:

```yaml
    - name: route
      value: "/api/v1/morph-touch/workflows/get-on-leave-report/instances/schedule?scheduleId=nightly&branchCode=99999&reportType=daily"
```

starts the instance with `{ "branchCode": "99999", "reportType": "daily" }`.

| Reserved parameter | Meaning |
| --- | --- |
| `scheduleId` | Identity of this schedule (see below). Never becomes instance data. |
| `version` | Pin the workflow version to start. Omit for the latest published version. |
| `sync` | Block until a rest point. Default `false`; a scheduler wants a fast acknowledgement. |

**Values are always strings.** A query string carries no types, so a workflow whose start schema
requires a number, a boolean or a nested object cannot be seeded this way — give it a string-typed
start schema, or no start schema at all, and convert inside the flow.

With no query parameters the instance is started with **no body**, not an empty object, so a workflow
with no start schema works unchanged.

## Running more than one replica

Dapr's cron binding has **no leader election**. Every replica that loads the component runs its own
ticker, so a three-pod deployment calls this endpoint three times for every tick.

Two mechanisms make that produce exactly one instance.

**1. A key derived from the tick.** All replicas compute the same value:

```
{scheduleId}-{yyyyMMddTHHmmssZ}      e.g.  daily-1135-20261005T083500Z
```

It is truncated to the second on purpose. Replicas stamp `readTimeUTC` from their own clocks and
disagree in the sub-second digits; truncating is what makes them agree. A schedule firing more than
once per second cannot be deduplicated this way.

**2. Two guards around the create**, because there are two different races.

*Simultaneous replicas* are serialised by a short lock on

```
vnext:schedule:{domain}:{workflow}:{instanceKey}
```

The key alone is not enough: the start path's idempotency is a check-then-insert over a non-unique
index, so concurrent callers all pass the probe before any commits. **Measured: ten parallel calls
carrying one tick produced nine instances.** One replica takes the lock and starts the instance; the
others answer `SUCCESS` without starting anything, because the tick *is* being handled. They log
`ScheduledStartTickAlreadyInFlight` (20479, Debug).

*A straggler that arrives after the winner released* is rejected by a status-agnostic probe for an
instance already carrying the tick's key. Serialising cannot stop this one: the start path treats a
**completed** instance as a free key (`if (existingInstance.IsCompleted) return null;`), so a replica
whose ticker drifted past a short flow's completion would start a second instance. **Measured: two.**
The probe logs `ScheduledStartTickAlreadyHandled` (20480, Debug).

The lock is **released** when the start returns. That matters beyond tidiness: a tick key never
recurs, and `sys_queues.DistributedLocks` is only ever pruned by a release, so holding the lock would
leak one dead row per tick forever. Correctness does not depend on the lease either — if it lapses
mid-start, the probe still rejects the duplicate, because the instance is committed well before the
pipeline finishes.

Re-measured after both guards: ten and twenty parallel calls each produce exactly one instance, and
the straggler-after-completion case produces one.

**One consequence worth knowing:** a replica that loses the lock acknowledges the tick before the
winner has committed anything. If the winner then fails, the occurrence is lost — having several
replicas does not cover a failing winner.

### Key length

`Instances.Key` is capped at 100 characters. A workflow key may itself be that long, so the instant
can overflow the column before anyone supplies a long `scheduleId` — and an overflow would fail the
insert on **every** tick, silently ending the schedule.

An over-long scope is therefore shortened and given an 8-character SHA-256 digest of the original
value, so two long scopes sharing a prefix still produce different keys. SHA-256 rather than
`string.GetHashCode()` because the value has to be identical in every replica, and .NET randomises
string hash codes per process.

## Choosing a layout

The rule is **one component per _schedule_, not per _flow_**. How that lands depends on whether
several flows share a schedule.

### Layout A — one component per flow (the default)

Four flows on the same schedule, four components:

```yaml
# a-hourly.yaml, b-hourly.yaml, c-hourly.yaml, d-hourly.yaml — same shape, different flow + scheduleId
metadata:
  - { name: schedule,  value: "@every 1h" }
  - { name: direction, value: "input" }
  - { name: route,     value: "/api/v1/morph-touch/workflows/A/instances/schedule?scheduleId=a-hourly" }
```

Nothing to author in the domain package. Each schedule is one reviewable file, disabled by deleting
it. **Start here.**

One property to be aware of: these do **not** fire at the same instant. Each replica's sidecar runs
its own ticker and they drift independently, so "@every 1h" on four components means four nearby
times, not one.

### Layout B — one component, one batch flow

When the four belong together, point the schedule at a **fifth workflow** whose job is to start the
others. The endpoint still starts exactly one flow; the fan-out happens inside it.

```
cron tick (1 component)
   └── POST …/workflows/morning-batch/instances/schedule
          └── instance of morning-batch          ← the endpoint starts THIS, and only this
                 ├── StartTrigger → instance of A
                 ├── StartTrigger → instance of B
                 ├── StartTrigger → instance of C
                 └── StartTrigger → instance of D
```

```yaml
# morning-batch.yaml
metadata:
  - { name: schedule,  value: "CRON_TZ=Europe/Istanbul 0 0 8 * * MON-FRI" }
  - { name: direction, value: "input" }
  - { name: route,     value: "/api/v1/morph-touch/workflows/morning-batch/instances/schedule?scheduleId=morning-batch" }
```

`morning-batch` is an ordinary workflow whose initial state runs four `StartTask`s (type `11`) as
`onEntries`:

```jsonc
{
  "key": "start-a",
  "attributes": { "type": 11, "triggerDomain": "morph-touch", "triggerFlow": "A", "triggerSync": false }
}
```

**Count the files honestly.** Layout B is not "one file" — it is one component, one batch workflow,
and one task component per target: **six files for four flows**, versus four for Layout A. The
configuration is not reduced; it moves out of infra YAML into versioned, reviewable, testable domain
code. Choose it for what that buys, not to save files.

### Which to choose

Take Layout B only when you need something Layout A genuinely cannot do:

| Need | Layout A | Layout B |
| --- | --- | --- |
| Ordering — D must wait for A | no | yes (`order` on the entries) |
| Conditionality — skip C on holidays | no | yes (a condition task or rule) |
| One instance that says "the 08:00 batch ran, C failed" | no | yes |
| A failed start becomes an incident with a retry path | no — the tick is lost silently | yes |
| Seed data computed once and shared | no — repeated in each URL | yes |
| All four start at the same instant | no — tickers drift | yes |

If none of those apply, Layout A is the better answer and a batch flow is overengineering.

### Starting the same flow many times: `FanOut`

`FanOut` (task type `21`) resolves a collection at runtime and runs an inner task once per item, in
parallel, joining the outcomes into one result. Wrapping a `StartTrigger` in it starts **N instances
of one flow** — one per branch, per tenant, per file — from a single task.

It does **not** help with Layout B's "four different flows": a `StartTask`'s target is the authored
`triggerFlow`, and a mapping shapes the task's body, not its destination. Four different flows means
four task components either way.

## What a schedule does not give you

A cron tick is **fire-and-forget**. Dapr's cron binding discards the application's response — it does
not inspect the status code, there is no redelivery and no dead-letter path. If the runtime is down,
or the start fails validation, **that occurrence is lost** until the next one.

This matters when a missed run has consequences. A nightly report that silently skips a night because
a deploy was in progress is a real incident, and neither Dapr nor the runtime will tell you. If the
schedule must not be missed, pair it with a check that the expected instance exists.

Failures are logged (`ScheduledStartFailed`, EventId 20478) precisely because nothing else will
surface them.

### Not supported: one-off and client-supplied schedules

This is a **declarative, recurring** mechanism. Components are static YAML, read when the sidecar
starts. There is no API for "start this flow once, 30 minutes from now", and no way for a client to
supply a schedule at runtime. Both would be a different feature.

### What happens when something fails

Every outcome below was exercised against a live runtime.

| Situation | Answer | Instance | Logged |
| --- | --- | --- | --- |
| Normal tick | `200 SUCCESS` | created | 20475 Information |
| Another replica won the tick | `200 SUCCESS` | not created by this caller | 20479 Debug |
| Tick repeated for an instance that still exists | `200 SUCCESS` | the existing one | 20475 |
| Workflow does not exist | `200 DROP` + reason | none | 20478 Error |
| Component points at another domain | `200 DROP` + reason | none | 20477 Error |
| Start schema rejects the seed data | `200 DROP` + reason | none | 20478 Error |
| No `readtimeutc` header | `200 SUCCESS` | created, key from the local clock | 20476 Warning |
| Transient infrastructure failure | non-2xx | none | 20478 Error |

`DROP` is deliberate for anything a later tick could not fix. Nothing retries a cron tick, so the
distinction does not change *this* occurrence — it changes the log level and therefore what an
operator sees. A component repeating a validation failure every 15 minutes should read as a
configuration defect, not an outage.

**A dropped tick is a lost occurrence.** There is no redelivery and no dead letter. If a missed run
has consequences, assert that the expected instance exists rather than trusting the schedule.

## Response

The endpoint answers with the Dapr-shaped body the event endpoint uses, so it is safe for any Dapr
caller even though cron ignores it:

```json
{ "status": "SUCCESS" }
```

`DROP` for a tick that can never be processed (unknown workflow, failed start validation, wrong
domain) — logged with the reason. A non-2xx means a transient failure.

## Observability

| EventId | Level | When |
| --- | --- | --- |
| 20475 | Information | A tick arrived. One line per replica per tick — the repetition is normal and is the signal that collapsing is working. |
| 20476 | Warning | No usable tick header; the key fell back to the local clock, weakening the cross-replica guarantee to "same second". |
| 20477 | Error | The component targets a runtime serving another domain. Permanent until the YAML is fixed. |
| 20478 | Error | The start was refused. The occurrence is lost — nothing retries it. |
| 20479 | Debug | Another replica already holds this tick's lock. Expected on every multi-replica deployment; one caller wins and the rest log this. |
| 20480 | Debug | An instance for this tick already exists, so a drifting replica was rejected — including the case where the first instance has already completed. |

Each tick starts a `Schedule.Start` span tagged with the domain and flow.

## See also

- [Event-Driven Workflows](event-driven-workflows.md) — the message-driven sibling of this endpoint.
- [Dapr cron binding](https://docs.dapr.io/reference/components-reference/supported-bindings/cron/) —
  schedule syntax (`@every 15m`, `@daily`, `CRON_TZ=…`, six-field expressions).
