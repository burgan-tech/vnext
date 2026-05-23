# Inbox/Outbox Workers

## Overview

The workflow runtime uses Aether's **Transactional Outbox** and **Inbox** processors to deliver
distributed events reliably. Domain events are written to the outbox table as part of the same
database transaction, then published asynchronously to Dapr PubSub. Inbox workers pull from the
inbox table and dispatch to event handlers with schema-aware, idempotent processing.

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────┐
│                          Event Delivery Flow                              │
├─────────────────────────────────────────────────────────────────────────┤
│ Domain/API Service                                                        │
│  AddDistributedEvent() → Outbox table (same transaction)                  │
│                    │                                                      │
│                    ▼                                                      │
│            Outbox Worker (IOutboxProcessor)                               │
│                    │ Publish serialized envelope                          │
│                    ▼                                                      │
│                Dapr PubSub                                                │
│                    │                                                      │
│                    ▼                                                      │
│            Inbox Worker (Dapr EventsController)                           │
│                    │ Persist to inbox table                               │
│                    ▼                                                      │
│            Inbox Processor (IInboxProcessor)                              │
│                    │ Dispatch to IEventHandler<TEvent>                    │
└─────────────────────────────────────────────────────────────────────────┘
```

## Worker Projects

### Inbox Worker (`BBT.Workflow.Workers.Inbox`)

**Location:** `workers/BBT.Workflow.Workers.Inbox/`

This worker hosts the Dapr endpoints for subscription discovery and event ingestion, then
processes inbox entries on a polling interval.

**Program entry point**

```csharp
ThreadPoolHelper.ConfigureThreadPool();

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.SetBasePath(Directory.GetCurrentDirectory());

// Optional Dapr Secret Store
if (builder.Configuration.GetValue<bool>("Vault:Enabled", false))
{
    var daprClient = new DaprClientBuilder().Build();
    await DaprCheckForSidecarHelper.CheckAsync(daprClient);
    builder.Configuration.AddDaprSecretStore(
        builder.Configuration["DAPR_SECRET_STORE_NAME"] ?? "vnext-secret",
        daprClient);
}

builder.Services.AddWorkerInboxModule();
var host = builder.Build();
host.UseWorkerInbox();
await host.RunAsync();
```

**Service registration highlights**

```csharp
services
    .AddDomainModule()
    .AddApplicationModule()
    .AddInfrastructureModule()
    .AddAspNetCoreModules(configuration)
    .AddResultResilience(configuration)
    .AddDaprClients()
    .AddAetherEventBus(options =>
    {
        options.DefaultSource =
            $"urn:vnext:{configuration.GetValue<string?>("ApplicationName")?.ToLowerInvariant()}";
        options.PrefixEnvironmentToTopic = true;
        options.PubSubName = configuration["DAPR_PUBSUB_STORE_NAME"]!;
    })
    .AddDbContext(configuration)
    .AddAetherBackgroundJob<WorkflowDbContext>()
    .AddDaprJobScheduler()
    .AddHostedService<InboxProcessorHostedService>();
```

**Dapr endpoints**

- `DaprEventDiscoveryController` exposes `/dapr/subscribe` for subscription discovery.
- `DaprEventController` handles `POST /events/{name}/{version}` and persists to the inbox store
  via `EventsController`.

**Inbox processing**

The hosted service resolves `IInboxProcessor` on each cycle and calls `RunAsync`. The cycle
interval comes from `AetherOutboxOptions.ProcessingInterval`.

### Outbox Worker (`BBT.Workflow.Workers.Outbox`)

**Location:** `workers/BBT.Workflow.Workers.Outbox/`

This worker reads pending outbox entries and publishes serialized envelopes to Dapr PubSub.

**Outbox processor**

```csharp
await using var scope = scopeFactory.CreateAsyncScope();
var processor = scope.ServiceProvider.GetRequiredService<IOutboxProcessor>();
await processor.RunAsync(stoppingToken);
```

`IOutboxProcessor` uses the event bus `PublishEnvelopeAsync` path, which bypasses hook execution
and is intended for replaying outbox messages.

## Inbox Event Handlers

Handlers live under `workers/BBT.Workflow.Workers.Inbox/Handlers/Instances` and follow the same
pattern: domain filtering via `IRuntimeInfoProvider`, schema switching with `ICurrentSchema`, and
unit-of-work scoped execution.

Current handlers include:

- `InstanceSubCompletedEventHandler` → `ISubflowCompletionService`
- `InstanceSubStateChangedEventHandler` → `ISubflowStateService`
- `InstanceCanceledEventHandler` → `IInstanceCancellationService`
- `ChildSubflowCancelRequestedEventHandler` → `IChildSubflowCancellationService`

## Event Hooks Interaction

The event bus in HTTP/API services is decorated with EventBus Hooks. When a hook succeeds, the
event is considered handled and is **not** published to the outbox. If a hook fails (or no hook
exists), the event is published and delivered through the outbox/inbox workers.

See [EventBus Hooks](./eventbus-hooks.md) for details.

## Configuration

```json
{
  "AetherOutbox": {
    "ProcessingInterval": "00:00:05",
    "BatchSize": 100,
    "MaxRetryCount": 3,
    "RetryDelaySeconds": 30
  }
}
```

## Multi-Schema Behavior

Handlers use schema resolution before accessing storage:

```csharp
using (currentSchema.Use(eventData.Flow))
{
    await service.ProcessAsync(eventData, cancellationToken);
}
```

## Related Documentation

- [EventBus Hooks](./eventbus-hooks.md) - Pre-publish hooks and fallback publishing
- [Background Jobs](./background-jobs.md) - Dapr job scheduling integration

