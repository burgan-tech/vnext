# EventBus Hooks

## Overview

EventBus Hooks provide a pre-publish interception layer for distributed events. Hooks allow the
runtime to execute side effects (routing, synchronization, validation, enrichment) without
changing domain logic. When all hooks succeed, the event is considered handled and is **not**
published to the underlying event bus. If any hook fails, publishing proceeds as a fallback.

## Architecture

**Contracts (`BBT.Workflow.Events.Contracts`)**

- `IEventPublishHook<TEvent>` for strongly-typed hooks
- `EventHookContext` for event metadata and topic information
- `EventHookResult` for success/failure and extra metadata
- `EventHookAttribute` to opt-in event types
- `IEventHookInvoker` / `EventHookInvoker<TEvent>` for type-safe invocation without reflection

**Infrastructure (`BBT.Workflow.Infrastructure`)**

- `HookedDistributedEventBus` decorates `IDistributedEventBus`
- `EventBusHookServiceCollectionExtensions` registers the decorator
- `EventHookServiceCollectionExtensions` provides `AddEventHook` helpers
- Hook implementations:
  - `InstanceSubCompletedEventHook`
  - `InstanceSubStateChangedEventHook`
  - `InstanceCanceledEventHook`

**Integration**

HTTP/API services call `AddEventBus(configuration)` which internally uses `AddEventBusWithHooks`
to register the decorated event bus.

## How It Works

1. Event type must have `[EventHook]`.
2. Hooks are registered in DI with `AddEventHook<TEvent, THook>()`.
3. `HookedDistributedEventBus` resolves `IEventHookInvoker` for the event type.
4. Each hook is executed with `BeforePublishAsync`.
5. Metadata is merged into the event if it exposes `Extensions` or `Metadata`.
6. **All hooks succeeded** → event is handled (not published).
7. **Any hook failed** → event is published to the inner bus.

## Usage

### 1. Mark an event as hook-enabled

```csharp
using BBT.Aether.Events;
using BBT.Workflow.Events.Hooks;

namespace BBT.Workflow.Instances.Events;

[EventHook]
[EventName("instance.sub.completed")]
public class InstanceSubCompletedEvent : IDistributedEvent
{
    [EventSubject]
    public required Guid InstanceId { get; init; }
    public required Guid SubInstanceId { get; init; }
    public required string CompletedState { get; init; }
}
```

### 2. Implement a hook

```csharp
public sealed class InstanceSubStateChangedEventHook(
    ILogger<InstanceSubStateChangedEventHook> logger,
    IInstanceCommandGateway instanceCommandGateway)
    : IEventPublishHook<InstanceSubStateChangedEvent>
{
    public async Task<EventHookResult> BeforePublishAsync(
        InstanceSubStateChangedEvent eventData,
        EventHookContext context,
        CancellationToken cancellationToken = default)
    {
        var input = new SubFlowStateChangedInput
        {
            ParentInstanceId = eventData.ParentInstanceId,
            SubInstanceId = eventData.SubInstanceId,
            Domain = eventData.Domain,
            Flow = eventData.Flow,
            Version = eventData.Version,
            NewState = eventData.NewState,
            PreviousState = eventData.PreviousState,
            ChangedAt = eventData.ChangedAt
        };

        var result = await instanceCommandGateway.UpdateSubFlowStateAsync(input, cancellationToken);

        if (!result.IsSuccess)
        {
            return EventHookResult.Fail(
                new InvalidOperationException(result.Error.Message),
                new Dictionary<string, string>
                {
                    ["hook_error"] = "SubFlowStateUpdateFailed"
                });
        }

        return EventHookResult.Ok(new Dictionary<string, string>
        {
            ["hook_executed"] = "true",
            ["sub_instance_id"] = eventData.SubInstanceId.ToString()
        });
    }
}
```

### 3. Register the hook in DI

```csharp
// WorkflowInfrastructureModuleServiceCollectionExtensions.cs
services.AddEventHook<InstanceSubCompletedEvent, InstanceSubCompletedEventHook>();
services.AddEventHook<InstanceSubStateChangedEvent, InstanceSubStateChangedEventHook>();
services.AddEventHook<InstanceCanceledEvent, InstanceCanceledEventHook>();
```

Registration also creates `EventHookInvoker<TEvent>` instances used by the event bus.

### 4. Publish events normally

Domain code still uses `AddDistributedEvent()`; hook execution is transparent.

## Hook Results and Metadata

Hook results can return additional metadata. The event bus tries to merge metadata into:

- `Extensions` (CloudEvents style)
- `Metadata` (custom dictionary)

If neither property exists, metadata is only logged for diagnostics.

## Error Handling

- Hook execution is wrapped; exceptions are caught and logged.
- Errors are recorded as `hook_error_{HookName}` and `hook_error_{HookName}_message`.
- Publishing never stops due to hook failures.

## Best Practices

- Keep hooks focused on a single responsibility.
- Return `EventHookResult.Fail(ex)` instead of throwing.
- Use scoped lifetime for hooks that need request-bound services.
- Enrich metadata with stable, queryable keys.

## Related Documentation

- [Inbox/Outbox Workers](../infrastructure/inbox-outbox-workers.md) - Delivery fallback path
- [Architecture Overview](../architecture/architecture-overview.md) - Event-driven overview

