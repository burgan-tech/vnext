# Embedded Scripts and Dapr Integration

## Overview

Infrastructure provides two runtime integrations:

- **Embedded scripts** for default task mappings (notably notifications).
- **Dapr metadata and notification bindings** for runtime binding discovery.

Both are registered by `AddInfrastructureModule()`.

## Embedded Scripts

### Providers

- `EmbeddedScriptProvider` loads embedded script resources lazily and caches them in memory.
- `NotificationScriptProvider` is a thin adapter that exposes the default notification script.

### Configuration

Embedded scripts are configured via `EmbeddedScriptOptions` and registered as singletons:

```csharp
services.AddEmbeddedScriptServices();
services.ConfigureEmbeddedScripts(opt =>
{
    opt.Add(
        "notification.default",
        "BBT.Workflow.Tasks.Scripting.NotificationMapping.csx",
        typeof(EmbeddedScriptEntry).Assembly);
});
```

### Usage

`NotificationTaskExecutor` calls `INotificationScriptProvider` to fetch the default mapping script,
then uses `IScriptEngine` to compile and run `InputHandler` / `OutputHandler`.

## Dapr Notification Integration

### Metadata

`IDaprMetadataProvider` pulls component metadata from the Dapr sidecar and caches it:

- `DaprMetadataProvider` uses `DaprClient.GetMetadataAsync`.
- Results are cached for subsequent calls.

### Notification Binding Resolution

`NotificationBindingResolver` resolves a single binding based on configuration:

- Uses `DaprNotificationOptions.ComponentName`.
- Classifies binding type into `NotificationBindingKind`.
- Uses `AsyncLazy` to resolve once, thread-safe.

### Warmup

`DaprMetadataWarmupHostedService` preloads metadata and resolves the binding on startup.
It retries with backoff to tolerate sidecar readiness.

### Registration

```csharp
services.AddDaprClient();
services.AddDaprNotification(configuration);
```

## Configuration

`DaprNotificationOptions` (`Dapr:Notification` section):

- `ComponentName` (default: `vnext-notification-binding`)
- `DefaultOperation` (default: `create`)
- `TimeoutSeconds` (default: 30)

## Implementation References

- `src/BBT.Workflow.Infrastructure/Scripting/EmbeddedScriptProvider.cs`
- `src/BBT.Workflow.Infrastructure/Scripting/NotificationScriptProvider.cs`
- `src/BBT.Workflow.Infrastructure/Microsoft/Extensions/DependencyInjection/EmbeddedScriptServiceCollectionExtensions.cs`
- `src/BBT.Workflow.Domain/Scripting/Contracts/EmbeddedScriptOptions.cs`
- `src/BBT.Workflow.Domain/Scripting/Contracts/INotificationScriptProvider.cs`
- `src/BBT.Workflow.Application/Tasks/Executors/Notification/NotificationTaskExecutor.cs`
- `src/BBT.Workflow.Infrastructure/Dapr/DaprNotificationOptions.cs`
- `src/BBT.Workflow.Infrastructure/Dapr/DaprNotificationServiceCollectionExtensions.cs`
- `src/BBT.Workflow.Infrastructure/Dapr/Metadata/IDaprMetadataProvider.cs`
- `src/BBT.Workflow.Infrastructure/Dapr/Metadata/DaprMetadataProvider.cs`
- `src/BBT.Workflow.Infrastructure/Dapr/Metadata/DaprMetadataWarmupHostedService.cs`
- `src/BBT.Workflow.Infrastructure/Dapr/Notification/NotificationBindingResolver.cs`
- `src/BBT.Workflow.Infrastructure/Dapr/Metadata/DaprComponentInfo.cs`
