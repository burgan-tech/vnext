# Strategy Pattern for Workflow Execution

## Overview

The execution layer uses the Strategy pattern to handle synchronous and asynchronous transition execution. Strategies are resolved at runtime based on `ExecMode` and invoked by `WorkflowExecutionService` (through `TransitionRunner` for chaining).

```
ITransitionStrategy
├── SyncTransitionStrategy  (pipeline execution)
└── AsyncTransitionStrategy (background job enqueue)
```

## Architecture Flow

```
WorkflowExecutionService.ExecuteTransitionCoreAsync
    ↓
IExecutionStrategyFactory.Get(ExecMode)
    ↓
ITransitionStrategy.ExecuteAsync
    ├── SyncTransitionStrategy → TransitionPipeline.RunAsync
    └── AsyncTransitionStrategy → IBackgroundJobService.EnqueueAsync
```

## Core Types

### 1. Strategy Contracts (Domain)

```csharp
public interface ITransitionStrategy
{
    ExecMode Mode { get; }
    Task<Result<TransitionExecutionContext>> ExecuteAsync(
        WorkflowExecutionContext context,
        CancellationToken cancellationToken);
}
```

```csharp
public interface IExecutionStrategyFactory
{
    Result<ITransitionStrategy> Get(ExecMode mode);
}
```

### 2. Strategy Resolution (Application)

`ExecutionStrategyFactory` resolves strategies by `ExecMode`. If not found, it falls back to the `Sync` strategy and returns `Result.Fail` only when no default exists.

### 3. Strategy Implementations

#### SyncTransitionStrategy

- Resolves handler via `ITransitionHandlerFactory`.
- Runs `TransitionPipeline` which owns context creation, locking, and sync dispatch chain.
- Enriches telemetry and returns `Result<TransitionExecutionContext>`.

#### AsyncTransitionStrategy

- Builds `TransitionExecutionContext` via `ITransitionContextFactory`.
- Enqueues a background job with `IBackgroundJobService` and persists job record.
- Returns `TransitionExecutionContext` immediately on success.

## Integration Points

### WorkflowExecutionService

`ExecuteTransitionCoreAsync` selects the strategy and then maps output to `TransitionCoreOutput`.

```csharp
return GetExecutionStrategy(context.Mode)
    .BindAsync(strategy => strategy.ExecuteAsync(context, cancellationToken))
    .BindAsync(execCtx => BuildCoreOutputAsync(context, execCtx, cancellationToken));
```

### TransitionRunner

`TransitionRunner` manages UoW boundaries and inline auto chaining, and invokes `IWorkflowExecutionService.ExecuteTransitionCoreAsync` for each hop.

## Updated DI Registration

Registered through `AddPipelineServices()`:

```csharp
services.AddScoped<IExecutionStrategyFactory, ExecutionStrategyFactory>();
services.AddScoped<ITransitionStrategy, SyncTransitionStrategy>();
services.AddScoped<ITransitionStrategy, AsyncTransitionStrategy>();
```

## Error Handling

- Strategies return `Result<T>` and rely on Result pattern for error propagation.
- Sync strategy errors come from pipeline step failures.
- Async strategy errors come from background job enqueueing failures.

## Notes

- `TransitionPipeline` manages locking and the sync dispatch chain.
- Async execution is scheduled immediately with a Dapr job (`TransitionJobHandler`).
```

## Benefits

### 1. **Separation of Concerns**
- Sync logic separated from async logic
- Background job scheduling isolated to async strategies
- Validation and preparation centralized in the service

### 2. **Code Reusability**
- Shared orchestration in `WorkflowExecutionService`
- Single strategy interface for new execution modes

### 3. **Testability**
- Each strategy can be tested independently
- Clear boundaries between components
- Mockable dependencies

### 4. **Maintainability**
- Strategy implementations remain focused and isolated
- Pipeline owns lock + sync dispatch chain in one place
- Factory provides centralized strategy resolution

### 5. **Extensibility**
- New execution strategies can be added without modifying existing code
- Factory pattern makes strategy selection configurable

## Usage Example

```csharp
var executionContext = new WorkflowExecutionContext
{
    Domain = "example",
    InstanceId = instanceId,
    WorkflowKey = "approval-workflow",
    TransitionKey = "approve",
    TriggerType = TriggerType.Manual,
    Mode = ExecMode.Sync,
    Actor = ExecutionActor.User,
    Data = jsonData,
    Headers = requestHeaders
};

var result = await workflowExecutionService.ExecuteTransitionAsync(
    executionContext,
    cancellationToken);
```

## Implementation Details

### SyncTransitionStrategy

- Resolves the trigger handler and delegates to `TransitionPipeline`.
- Pipeline owns context creation, locking, and sync dispatch chain.
- Telemetry is enriched via Activity tags.

### AsyncTransitionStrategy

- Builds `TransitionExecutionContext` first.
- Enqueues a Dapr job (`TransitionJobHandler`) and persists `InstanceJob`.
- Returns context immediately.

### Error Handling

- Result pattern is used end-to-end.
- Sync strategy returns pipeline step errors.
- Async strategy wraps Dapr enqueue errors with dependency errors.

## Testing Strategy

- Unit tests for each strategy independently
- Integration tests for the complete workflow
- Mock-based testing for external dependencies
- Behavior verification for state changes

## DI Registration

Strategies are registered through `AddPipelineServices()`:

```csharp
services.AddScoped<IExecutionStrategyFactory, ExecutionStrategyFactory>();
services.AddScoped<ITransitionStrategy, SyncTransitionStrategy>();
services.AddScoped<ITransitionStrategy, AsyncTransitionStrategy>();
```

## Integration with Other Patterns

### Result Pattern
All strategies and pipeline steps use the Result pattern for exception-free error handling:
```csharp
Task<Result<TransitionExecutionContext>> ExecuteAsync(...)
Task<Result<StepOutcome>> ExecuteAsync(...)
```

### Handler Pattern
Trigger-specific processing via `ITransitionHandler`:
- `ManualTransitionHandler`
- `AutomaticTransitionHandler`
- `ScheduledTransitionHandler`
- `EventTransitionHandler`

### Pipeline Pattern
See `docs/architecture/transition-pipeline.md` for pipeline details.

## Related Patterns

- **Strategy Pattern**: Core pattern for execution mode selection
- **Factory Pattern**: Used in `ExecutionStrategyFactory` and `TransitionHandlerFactory`
- **Pipeline Pattern**: Used in `TransitionPipeline` for ordered step execution
- **Result Pattern**: Used throughout for exception-free error handling
- **Dependency Injection**: Used throughout for loose coupling and testability
- **Chain of Responsibility**: Used in handler selection and pipeline execution

## Implementation References

- `src/BBT.Workflow.Domain/Execution/Transitions/Strategy/ITransitionStrategy.cs`
- `src/BBT.Workflow.Domain/Execution/Transitions/Factory/IExecutionStrategyFactory.cs`
- `src/BBT.Workflow.Application/Execution/Transitions/Strategy/SyncTransitionStrategy.cs`
- `src/BBT.Workflow.Application/Execution/Transitions/Strategy/AsyncTransitionStrategy.cs`
- `src/BBT.Workflow.Application/Execution/Transitions/Factory/ExecutionStrategyFactory.cs`
- `src/BBT.Workflow.Application/Execution/Services/WorkflowExecutionService.cs`
