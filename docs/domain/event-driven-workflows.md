# Event-Driven Workflows

## Purpose

vNext can turn an external event (a pub/sub message, an input-binding payload, or a plain HTTP POST)
into a workflow action: **starting a new instance** or **advancing an existing one via a transition**.

The design boundary is strict and intentional:

- The **runtime** exposes one generic receiving endpoint and runs your mapping script. It subscribes
  to nothing and knows no topics.
- The **domain service** owns the entire delivery infrastructure: broker choice, topics, Dapr
  subscription components, and producers.

This page explains both sides. For the filter model used in event correlation, see
[Instance Filtering and Queries](../runtime/instance-filtering-and-queries.md).

## The two event actions

| Action | Effect | Event declared on | Delivered with |
| --- | --- | --- | --- |
| `start` | Creates a new instance | the **workflow** (`attributes.event`) | `?action=start` |
| `transition` | Advances an active instance | a **transition** (`transition.event`) | `?action=transition&transitionKey=<key>` |

Both declaration points are optional and independent — a workflow can support either, both, or
neither. A transition-level event has one extra requirement: the transition must be declared with
`"triggerType": 3` (Event). Event delivery to any other transition type is rejected with
`NotAnEventTransition`.

## Declaring events in the component JSON

The `event` field has one property: a `mapping` script (same `ScriptCode` shape as task mappings —
`location` + base64 `code`, or a reference).

Workflow-level (enables `action=start`):

```jsonc
{
  "key": "event-driven-workflow",
  "attributes": {
    "type": "F",
    "event": {
      "mapping": { "location": "./src/StartEventMapping.csx", "code": "<base64>" }
    },
    "states": [ /* ... */ ]
  }
}
```

Transition-level (enables `action=transition` for that transition):

```jsonc
{
  "key": "abort-event-driven-workflow",
  "target": "aborted",
  "triggerType": 3,
  "event": {
    "mapping": { "location": "./src/AbortEventMapping.csx", "code": "<base64>" }
  }
}
```

## The mapping contract: `IEventMapping`

The mapping script is the adapter between the producer's payload and the workflow. It implements
`IEventMapping` (`src/BBT.Workflow.Domain/Scripting/Contracts/IEventMapping.cs`) and has exactly two
responsibilities — correlation and payload shaping. It must be deterministic and side-effect free.

```csharp
public class AbortEventMapping : ScriptBase, IEventMapping
{
    public Task<EventMappingResult> Handler(ScriptContext context)
    {
        // Raw producer payload. CloudEvent envelopes are unwrapped by the runtime before this
        // runs — you see the producer's `data`, not { specversion, source, ... }.
        var payload = context.EventPayload;

        return Task.FromResult(new EventMappingResult
        {
            // Correlation: which instance is this event about?
            InstanceKey = payload?.orderId?.ToString(),

            // Payload shaping: becomes the new instance's initial attributes (start)
            // or the transition's input data (transition).
            Body = new { reason = payload?.reason?.ToString() ?? "external-abort" }
        });
    }
}
```

`EventMappingResult` fields:

| Field | Used by | Meaning |
| --- | --- | --- |
| `InstanceKey` | start + transition | Business key. Start: the new instance's key. Transition: correlates to the **active** instance with that key. |
| `Body` | start + transition | Mapped data (initial attributes / transition input). |
| `Selector` | transition only | Fallback correlation when the payload carries no key: an `InstanceQuery ... .First()/.Last()` filter; the runtime resolves the target instance by querying the instance store. Ignored when `InstanceKey` is set. |

Selector example — correlate by data instead of key:

```csharp
result.Selector = InstanceQuery.Create()
    .Where("attributes.userId", f => f.Eq(userId))
    .Where("currentState",      f => f.Eq("waiting-payment"))
    .OrderBy("createdAt")
    .Last();   // newest match
```

The selector is automatically scoped to the target workflow (`flow = <workflow>`), so it can never
match another flow's instances. Operator reference:
[Instance Filtering and Queries](../runtime/instance-filtering-and-queries.md).

## The receiving endpoint

One internal endpoint handles all event delivery (hidden from Swagger; hosted by the Orchestration
API, `InstanceController.HandleEventAsync`):

```
POST /api/v1/{domain}/workflows/{workflow}/instances/events
     ?action=start|transition
     [&transitionKey=<key>]     // required for action=transition
     [&sync=true]               // block until the pipeline completes
```

Body: the raw event payload (CloudEvent envelopes are unwrapped automatically).

### The response is a Dapr protocol body, not an instance envelope

Dapr reads the **top-level `status` field of the subscriber's JSON response** as its delivery signal
— `SUCCESS`, `RETRY` or `DROP`; any other value is an error and the message is redelivered
([Dapr pub/sub API](https://docs.dapr.io/reference/api/pubsub_api/#expected-http-response)). That
field name collides with the instance DTOs the rest of `InstanceController` returns, whose `status`
is an `InstanceStatus` code (`"A"`, `"B"`, `"C"`, `"F"`, `"P"`). So this endpoint returns its own
`EventDeliveryResponse` instead:

```jsonc
{ "status": "SUCCESS" }                       // processed, or intentionally ignored
{ "status": "DROP", "reason": "TransitionNotFound: …" }   // unprocessable, discarded
{ "status": "SUCCESS", "instance": { "id": "…", "key": "…", "status": "C" } }  // sync=true only
```

The instance snapshot is nested (`instance`) and emitted only for `sync=true` — Dapr inspects the
top level only, so an instance status code is safe there. **Never return `StartInstanceOutput` /
`TransitionOutput` from this endpoint**: a body like `{"id":"…","status":"B"}` makes Dapr treat every
delivery as failed, and it will redeliver the same message forever without ever advancing the
partition offset.

## Delivery infrastructure (owned by the domain)

The runtime never subscribes. You route broker messages to the endpoint with **declarative Dapr
Subscription components** — one YAML per topic→action pair. The reference example
(`etc/orchestration/dapr/components/`):

```yaml
# start: any message on the topic creates an instance
apiVersion: dapr.io/v1alpha1
kind: Subscription
metadata:
  name: event-driven-workflow-start-subscription
spec:
  topic: morph-touch.event-driven-workflow
  route: /api/v1/morph-touch/workflows/event-driven-workflow/instances/events?action=start
  pubsubname: vnext-pubsub
```

```yaml
# transition: any message on the topic aborts the correlated instance, synchronously
apiVersion: dapr.io/v1alpha1
kind: Subscription
metadata:
  name: event-driven-workflow-abort-subscription
spec:
  topic: morph-touch.event-driven-workflow.abort
  route: "/api/v1/morph-touch/workflows/event-driven-workflow/instances/events?action=transition&transitionKey=abort-event-driven-workflow&sync=true"
  pubsubname: vnext-pubsub
```

Rules of thumb:

- **One topic per action.** The routing decision (which action, which transition) lives in infra;
  the data decision (correlation, body) lives in the mapping script.
- Topic naming convention: `{domain}.{workflow}` for start, `{domain}.{workflow}.{purpose}` for
  transitions.
- `pubsubname` references your Dapr pub/sub component (Redis/Kafka/RabbitMQ) — also domain-owned.
- Locally the YAMLs live in the folder mounted into the orchestration sidecar; in Kubernetes they
  are `Subscription` custom resources in your namespace scoped to the orchestration app-id.
- Shipping a new event = one more YAML. No runtime change, no redeploy of vNext.
- **Add a dead-letter topic.** The runtime drops permanently-unprocessable messages itself, but a
  transient-looking failure that never clears (a mapping script throwing on every attempt) is
  retried per your resiliency policy. Without a bound, that blocks the partition:

  ```yaml
  spec:
    topic: morph-touch.event-driven-workflow
    route: /api/v1/morph-touch/workflows/event-driven-workflow/instances/events?action=start
    pubsubname: vnext-pubsub
    deadLetterTopic: morph-touch.event-driven-workflow.dlq
  ```

  Pair it with a `maxRetries` resiliency policy on the pub/sub component, and subscribe something to
  the DLQ — even just an alert.

## Producers

Anything that can publish to the topic is a valid producer — it needs zero knowledge of vNext:

- another service via the Dapr SDK: `PublishEventAsync("vnext-pubsub", "morph-touch.event-driven-workflow", payload)`
- `dapr publish` from the CLI (local testing)
- **another vNext workflow** via a `DaprPubSubTask` publishing to the topic — event-based
  workflow-to-workflow choreography without direct coupling.

## Runtime semantics

Delivery order: endpoint → resolve workflow from the component cache → pick the event definition
(workflow-level for start, transition-level for transition) → compile + run the mapping → act.

Failures are split by intent. A message that can **never** succeed no matter how often it is
redelivered (a `transitionKey` typo, a missing event definition, a body that is not JSON, a
subscription pointed at the wrong domain) is answered `200 DROP`: retrying it would block the
partition behind a message that will never be accepted. Only genuinely transient failures keep a
non-2xx response, which is already Dapr's redelivery signal.

| Situation | Response | Broker behavior |
| --- | --- | --- |
| Processed (start or transition executed) | `200 SUCCESS` | offset advances |
| No active instance matches the key/selector | **`200 SUCCESS`, logged, intentionally ignored** | no redelivery |
| `action` is neither `start` nor `transition` | `200 DROP` `InvalidEventAction` | discarded, logged |
| Body is not parseable JSON | `200 DROP` `InvalidEventPayload` | discarded, logged |
| Route domain is not this runtime's domain | `200 DROP` `EventDomainMismatch` | discarded, logged |
| `transitionKey` unknown in the workflow | `200 DROP` `TransitionNotFound` | discarded, logged |
| `transitionKey` missing for `action=transition` | `200 DROP` `EventTransitionKeyRequired` | discarded, logged |
| Transition exists but `triggerType != 3` | `200 DROP` `NotAnEventTransition` | discarded, logged |
| Workflow/transition has no `event` definition | `200 DROP` `EventDefinitionMissing` | discarded, logged |
| Mapping script throws / returns null | 500 `EventMappingFailed` / `EventMappingNullResult` | retried per resiliency policy |
| Pipeline / infrastructure failure | 500 (or 409 on concurrency conflict) | retried per resiliency policy |

Notes:

- The 200-on-no-match rule is deliberate: an abort event for an already-completed instance must
  evaporate, not redeliver forever.
- Every `DROP` is logged as a **Warning** (`EventDeliveryDropped`, EventId 40994) with the error code
  and reason, and the reason is echoed in the response body — a discarded message is still
  diagnosable. Alert on that log; a silently dropped event is worse than a noisy one.
- A mapping script that throws deterministically is in the *retry* class, so it will be redelivered
  until the resiliency policy gives up. Bound it: give the subscription a `maxRetries` policy and a
  `deadLetterTopic` so the poison message ends up somewhere inspectable instead of consuming the
  consumer indefinitely.
- `sync=true` blocks the delivery until the transition pipeline completes; without it the event is
  accepted and processed asynchronously.
- Event transitions run with the **Event pipeline profile** (skips Preflight, ForwardSubflow,
  SetBusy, ResourceLock) and `ExecutionActor.System`.
- Domain isolation is enforced before anything runs (`IRuntimeInfoProvider.Check`), and each domain
  queries its own schema — a foreign domain's event cannot touch your instances.

## Adoption checklist

1. Declare `attributes.event` on the workflow (for start) and/or `event` + `"triggerType": 3` on a
   transition (for transition).
2. Author one `IEventMapping` `.csx` per event: correlation (`InstanceKey` or `Selector`) + `Body`.
3. Add one Dapr `Subscription` YAML per topic→action pair; make sure the pub/sub component exists.
4. Point producers at the topic.
5. Test bottom-up (next section).

## Testing

Cheapest first — the receiving side is plain HTTP, so the mapping + correlation + pipeline can be
exercised without a broker:

```bash
curl -X POST "http://localhost:4201/api/v1/morph-touch/workflows/event-driven-workflow/instances/events?action=start" \
  -H "Content-Type: application/json" \
  -d '{"orderId":"ord-1","amount":250}'
```

Expect `200 {"status":"SUCCESS"}`. Add `&sync=true` to also get the `instance` snapshot back. Because
unprocessable messages answer `200 DROP` rather than 4xx, read the `status` field — not just the
status code — when testing by hand: a `DROP` means the delivery was discarded, and the `reason` says
why.

Then validate the YAML routing with the real path:

```bash
dapr publish --publish-app-id vnext-app --pubsub vnext-pubsub \
  --topic morph-touch.event-driven-workflow --data '{"orderId":"ord-1","amount":250}'
```

Only then point the actual producer at the topic. Declarative subscriptions are read at sidecar
startup — restart the Dapr sidecar after adding or changing a YAML.

## References

- Endpoint: `orchestration/.../Controllers/Instances/InstanceController.cs` (`HandleEventAsync`)
- Response contract: `src/BBT.Workflow.Application/Events/EventDeliveryResponse.cs`,
  `orchestration/.../Controllers/Instances/EventDeliveryResultMapper.cs`
- Application service: `src/BBT.Workflow.Application/Events/EventAppService.cs`
- Mapping contract: `src/BBT.Workflow.Domain/Scripting/Contracts/IEventMapping.cs`
- Event definition: `src/BBT.Workflow.Domain/Definitions/Events/Event.cs`
- Selector resolution: `src/BBT.Workflow.Application/Instances/InstanceSelectorResolver.cs`
- Example subscriptions: `etc/orchestration/dapr/components/event-definition.yaml`,
  `event-driven-workflow-abort-subscription.yaml`
