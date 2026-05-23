# Task Factory and Object Pooling

## Overview

Task factories create execution-ready `WorkflowTask` instances that are isolated from cached definitions. The implementation supports optional object pooling for high-throughput scenarios while keeping Result-based error handling.

## Core Contract (Domain)

```csharp
public interface ITaskFactory
{
    Task<Result<WorkflowTask>> CreateExecutionTaskAsync(
        IReference taskReference,
        CancellationToken cancellationToken = default);

    Result<WorkflowTask> CreateFromCached(WorkflowTask cachedTask);
}
```

## Implementations (Application)

### TaskFactory

Uses `WorkflowTask.Clone()` to create isolated instances from cache:

```csharp
public sealed class TaskFactory : ITaskFactory
{
    public Task<Result<WorkflowTask>> CreateExecutionTaskAsync(
        IReference taskReference,
        CancellationToken cancellationToken = default)
        => componentCacheStore.GetTaskAsync(taskReference, cancellationToken)
            .Then(CreateFromCached);

    public Result<WorkflowTask> CreateFromCached(WorkflowTask cachedTask)
        => Result<WorkflowTask>.Ok(cachedTask.Clone());
}
```

### PooledTaskFactory

Uses object pooling when configured and falls back to cloning otherwise:

- Pool creation uses `TaskPooledObjectPolicy`.
- Pool selection is based on `TaskFactoryOptions.PooledTaskTypes`.
- Uses `PoolableTaskRegistry` for type-specific creation/copy.

## PoolableTaskRegistry

`PoolableTaskRegistry` registers poolable task types and their efficient copy methods:

```csharp
RegisterPoolableTask<DaprServiceTask>(
    DaprServiceTask.CreateEmpty,
    (source, target) => ((DaprServiceTask)target).CopyFromInternal((DaprServiceTask)source));
```

It is used by `PooledTaskFactory` for pool creation and copying.

## Configuration

`TaskFactoryOptions` control pooling:

```csharp
public sealed class TaskFactoryOptions
{
    public bool UseObjectPooling { get; set; } = false;
    public int MaxPoolSize { get; set; } = 100;
    public int InitialPoolSize { get; set; } = 10;
    public bool EnableMetrics { get; set; } = false;
    public string[] PooledTaskTypes { get; set; } = { /* defaults */ };
}
```

Example:

```json
{
  "TaskFactory": {
    "UseObjectPooling": true,
    "MaxPoolSize": 1000,
    "InitialPoolSize": 100,
    "EnableMetrics": false,
    "PooledTaskTypes": [
      "DaprServiceTask",
      "HttpTask",
      "ScriptTask",
      "ConditionTask",
      "DaprBindingTask",
      "DaprHttpEndpointTask",
      "DaprPubSubTask",
      "HumanTask"
    ]
  }
}
```

## Dependency Injection

Registration is configuration-driven and uses singletons:

```csharp
services.AddOptions<TaskFactoryOptions>()
    .BindConfiguration(TaskFactoryOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

services.AddSingleton<TaskFactory>();
services.AddSingleton<PooledTaskFactory>();
services.AddSingleton<ITaskFactory>(sp =>
{
    var options = sp.GetRequiredService<IOptions<TaskFactoryOptions>>();
    return options.Value.UseObjectPooling
        ? sp.GetRequiredService<PooledTaskFactory>()
        : sp.GetRequiredService<TaskFactory>();
});
```

## Notes

- Pooling is opt-in via configuration.
- When pooling is enabled, tasks must support `CreateEmpty()` and `CopyFromInternal(...)`.
- `PooledTaskFactory` records pool metrics when `EnableMetrics` is true.

## Implementation References

- `src/BBT.Workflow.Domain/Tasks/Factory/ITaskFactory.cs`
- `src/BBT.Workflow.Application/Tasks/Factory/TaskFactory.cs`
- `src/BBT.Workflow.Application/Tasks/Factory/PooledTaskFactory.cs`
- `src/BBT.Workflow.Application/Tasks/Factory/TaskFactoryOptions.cs`
- `src/BBT.Workflow.Application/Microsoft/Extensions/DependencyInjection/TaskServiceCollectionExtensions.cs`
