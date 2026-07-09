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

## Producers

Anything that can publish to the topic is a valid producer — it needs zero knowledge of vNext:

- another service via the Dapr SDK: `PublishEventAsync("vnext-pubsub", "morph-touch.event-driven-workflow", payload)`
- `dapr publish` from the CLI (local testing)
- **another vNext workflow** via a `DaprPubSubTask` publishing to the topic — event-based
  workflow-to-workflow choreography without direct coupling.

## Runtime semantics

Delivery order: endpoint → resolve workflow from the component cache → pick the event definition
(workflow-level for start, transition-level for transition) → compile + run the mapping → act.

| Situation | Response | Broker behavior |
| --- | --- | --- |
| Processed (start or transition executed) | 200 | done |
| No active instance matches the key/selector | **200, logged, intentionally ignored** | no redelivery |
| Mapping script throws / returns null | 500 `EventMappingFailed` / `EventMappingNullResult` | retried per resiliency policy |
| `transitionKey` unknown in the workflow | 404 `TransitionNotFound` | retried per resiliency policy |
| Transition exists but `triggerType != 3` | 400 `NotAnEventTransition` | retried per resiliency policy |
| Workflow/transition has no `event` definition | 404 `EventDefinitionMissing` | retried per resiliency policy |

Notes:

- The 200-on-no-match rule is deliberate: an abort event for an already-completed instance must
  evaporate, not redeliver forever.
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

Then validate the YAML routing with the real path:

```bash
dapr publish --publish-app-id vnext-app --pubsub vnext-pubsub \
  --topic morph-touch.event-driven-workflow --data '{"orderId":"ord-1","amount":250}'
```

Only then point the actual producer at the topic. Declarative subscriptions are read at sidecar
startup — restart the Dapr sidecar after adding or changing a YAML.

## References

- Endpoint: `orchestration/.../Controllers/Instances/InstanceController.cs` (`HandleEventAsync`)
- Application service: `src/BBT.Workflow.Application/Events/EventAppService.cs`
- Mapping contract: `src/BBT.Workflow.Domain/Scripting/Contracts/IEventMapping.cs`
- Event definition: `src/BBT.Workflow.Domain/Definitions/Events/Event.cs`
- Selector resolution: `src/BBT.Workflow.Application/Instances/InstanceSelectorResolver.cs`
- Example subscriptions: `etc/orchestration/dapr/components/event-definition.yaml`,
  `event-driven-workflow-abort-subscription.yaml`
