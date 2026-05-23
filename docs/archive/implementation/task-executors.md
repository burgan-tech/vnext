# Task Executors

## Overview

The task execution system runs workflow tasks using dedicated executors, a shared execution engine, and a coordinator that orchestrates parallel/sequential execution. It follows the Result pattern for error propagation and uses a template method in `TaskExecutorBase<TTask>` to enforce a consistent lifecycle.

## Architecture

```
TaskCoordinator (orchestrates order/parallelism)
    ↓
TaskExecutionEngine (single-task lifecycle, error boundaries)
    ↓
TaskExecutorRegistry → ITaskExecutor
    ↓
TaskExecutorBase<TTask> (prepare → invoke → post → output)
```

Remote execution goes through `RemoteInvokerService` and the Execution Service using `TaskEnvelope` and `TaskInvocationResult`.

## Core Components

### TaskCoordinator

`TaskCoordinator` groups tasks by `Order`, executes groups sequentially, and runs same-order tasks in parallel. It delegates single-task execution to `TaskExecutionEngine` and uses evaluators for condition/timer scripts.

### TaskExecutionEngine

`TaskExecutionEngine` runs a single task and applies error boundaries:

- Builds boundary chain (Task → State → Global).
- Resolves retry policy and executes via Polly.
- Resolves fallback actions (Abort, Retry, Rollback, Notify, Log, Ignore).
- Persists `InstanceTask` via `ITaskPersistenceStrategyFactory`.

### TaskExecutorBase (Template Method)

`TaskExecutorBase<TTask>` defines the lifecycle:

1. Validate
2. PrepareInput
3. PreProcess
4. Invoke
5. PostProcess
6. ProcessOutput
7. CreateResponse

It returns `Result<StandardTaskResponse>` and does not intercept business errors from remote invocations.

### TaskExecutorContext

```csharp
public sealed record TaskExecutorContext(
    WorkflowTask Task,
    OnExecuteTask OnExecuteTask,
    ScriptContext ScriptContext,
    Guid? InstanceTransitionId,
    TaskTrigger TaskTrigger)
{
    public TaskType TaskType => Task.GetTaskType();
}
```

### StandardTaskResponse

```csharp
public sealed class StandardTaskResponse
{
    public dynamic? Data { get; set; }
    public int? StatusCode { get; set; }
    public bool IsSuccess { get; set; } = true;
    public string? ErrorMessage { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
    public long? ExecutionDurationMs { get; set; }
    public string? TaskType { get; set; }
}
```

## Built-in Executors

Executors live under `BBT.Workflow.Application/Tasks/Executors` and include:

- HTTP: `HttpTaskExecutor`
- Dapr: `DaprServiceTaskExecutor`, `DaprBindingTaskExecutor`, `DaprHttpEndpointTaskExecutor`, `DaprPubSubTaskExecutor`
- Script: `ScriptTaskExecutor`
- Notification: `NotificationTaskExecutor`
- Human: `HumanTaskExecutor`
- Trigger: `StartTriggerTaskExecutor`, `DirectTriggerTaskExecutor`, `SubProcessTaskExecutor`, `GetInstanceDataTaskExecutor`, `GetInstancesTaskExecutor`

## Remote Invocation Flow

Remote tasks are mapped to `TaskEnvelope` via `TaskBindingMapper` and executed through `RemoteInvokerService` using Dapr invocation. The execution service returns `TaskInvocationResult`:

```csharp
public sealed class TaskInvocationResult
{
    public bool IsSuccess { get; init; }
    public int? StatusCode { get; init; }
    public string? Body { get; init; }
    public object? Data { get; init; }
    public string? ErrorMessage { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
    public long ExecutionDurationMs { get; init; }
    public string? TaskType { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}
```

`TaskBindingMapper` supports remote bindings for:

- `HttpTask`
- `DaprServiceTask`
- `DaprBindingTask`
- `DaprHttpEndpointTask`
- `DaprPubSubTask`
- `NotificationTask`
- `StartTask` and `GetInstanceDataTask` (basic bindings)

`DirectTriggerTask` and `SubProcessTask` require runtime context and are handled by trigger executors.

## Dependency Injection

Task services are registered via `AddTaskHandlers()`:

```csharp
services.AddTaskHandlers();
```

This registers executors, evaluators, task coordination, persistence strategies, factories, and scripting services (see `TaskServiceCollectionExtensions`).

## Best Practices

- Prefer `TaskExecutorBase<TTask>` and override only needed lifecycle steps.
- Use Result pattern consistently; never throw for business errors.
- Use `TaskBindingMapper` for remote bindings and keep mapping logic in one place.
- Keep mapping scripts minimal and deterministic.

## Implementation References

- `src/BBT.Workflow.Application/Tasks/Coordinator/TaskCoordinator.cs`
- `src/BBT.Workflow.Application/Tasks/Coordinator/TaskExecutionEngine.cs`
- `src/BBT.Workflow.Application/Tasks/Executors/Core/TaskExecutorBase.cs`
- `src/BBT.Workflow.Domain/Tasks/Executors/Core/TaskExecutorContext.cs`
- `src/BBT.Workflow.Application/Tasks/Executors/Remote/RemoteInvokerService.cs`
- `src/BBT.Workflow.Application/Tasks/Mapping/TaskBindingMapper.cs`
# Task Executors

## Overview

The Task Execution System orchestrates and executes different types of tasks within workflow transitions. It implements the **Template Method Pattern** for consistent task lifecycle management and supports both local and remote task execution through a clean separation of concerns.

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────┐
│                        TaskCoordinator                                   │
│  ┌────────────────────────────────────────────────────────────────────┐ │
│  │  Parallel/Sequential Execution Strategy                           │ │
│  │  Condition & Timer Evaluation                                      │ │
│  └────────────────────────────────────────────────────────────────────┘ │
│                                    │                                     │
│                                    ▼                                     │
│  ┌────────────────────────────────────────────────────────────────────┐ │
│  │  TaskExecutorRegistry                                              │ │
│  │  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐  ┌───────────┐ │ │
│  │  │ DaprService │  │    Http     │  │   Script    │  │  Trigger  │ │ │
│  │  │  Executor   │  │  Executor   │  │  Executor   │  │ Executors │ │ │
│  │  └─────────────┘  └─────────────┘  └─────────────┘  └───────────┘ │ │
│  └────────────────────────────────────────────────────────────────────┘ │
│                                    │                                     │
│         ┌──────────────────────────┴──────────────────────┐             │
│         │                                                  │             │
│         ▼                                                  ▼             │
│  ┌─────────────────┐                          ┌─────────────────────┐   │
│  │  Local Execution│                          │  Remote Execution   │   │
│  │  (Script, etc.) │                          │  (RemoteInvokerSvc) │   │
│  └─────────────────┘                          └─────────────────────┘   │
│                                                           │              │
└───────────────────────────────────────────────────────────│──────────────┘
                                                            │
                                                            ▼
                                               ┌─────────────────────┐
                                               │  Execution Service  │
                                               │  TaskInvokerRegistry│
                                               └─────────────────────┘
```

## Core Components

### 1. TaskCoordinator

Orchestrates task execution with support for parallel and sequential execution strategies:

```csharp
public sealed class TaskCoordinator : ITaskCoordinator
{
    private readonly ITaskExecutorRegistry _executorRegistry;
    private readonly IConditionEvaluator _conditionEvaluator;
    private readonly ITimerEvaluator _timerEvaluator;
    private readonly ITaskFactory _taskFactory;
    private readonly ITaskPersistenceStrategyFactory _persistenceStrategyFactory;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IWorkflowMetrics _workflowMetrics;
    private readonly ILogger<TaskCoordinator> _logger;

    /// <inheritdoc />
    public async Task<Result> ExecuteAsync(
        IEnumerable<OnExecuteTask> onExecuteTasks,
        Guid? instanceTransitionId,
        TaskTrigger taskTrigger,
        ScriptContext context,
        CancellationToken cancellationToken = default)
    {
        var tasks = onExecuteTasks.ToList();
        if (!tasks.Any()) return Result.Ok();

        // Determine execution strategy
        var canExecuteInParallel = CanExecuteInParallel(tasks);

        if (canExecuteInParallel)
        {
            return await ExecuteTasksInParallelAsync(tasks, instanceTransitionId, 
                taskTrigger, context, cancellationToken);
        }

        return await ExecuteTasksSequentiallyAsync(tasks, instanceTransitionId, 
            taskTrigger, context, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<bool>> ExecuteConditionAsync(
        ScriptCode script,
        ScriptContext context,
        CancellationToken cancellationToken = default)
    {
        return _conditionEvaluator.EvaluateAsync(script, context, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<TimerSchedule>> ExecuteTimerAsync(
        ScriptCode script,
        ScriptContext context,
        CancellationToken cancellationToken = default)
    {
        return _timerEvaluator.EvaluateAsync(script, context, cancellationToken);
    }

    private static bool CanExecuteInParallel(IList<OnExecuteTask> tasks)
    {
        // Tasks with same order can execute in parallel
        var orders = tasks.Select(t => t.Order).Distinct().ToList();
        return orders.Count == 1 || tasks.Count == 1;
    }
}
```

### 2. TaskExecutorBase (Template Method Pattern)

Abstract base class providing consistent task lifecycle:

```csharp
public abstract class TaskExecutorBase<TTask> : ITaskExecutor
    where TTask : WorkflowTask
{
    protected readonly ILogger Logger;

    protected TaskExecutorBase(ILogger logger)
    {
        Logger = logger;
    }

    public abstract TaskType TaskType { get; }

    /// <summary>
    /// Template Method: Orchestrates the complete task execution lifecycle.
    /// </summary>
    public async Task<Result<StandardTaskResponse>> ExecuteAsync(
        TaskExecutorContext context,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var taskKey = context.Task.Key;

        Logger.LogDebug("Executing task {TaskKey} with executor {Executor}",
            taskKey, GetType().Name);

        // 1. Validate & Cast
        var validationResult = ValidateContext(context);
        if (!validationResult.IsSuccess)
        {
            return Result<StandardTaskResponse>.Fail(validationResult.Error);
        }

        var task = (TTask)context.Task;

        // 2. PrepareInput (virtual - custom per executor)
        var inputResult = await PrepareInputAsync(task, context, cancellationToken);
        if (!inputResult.IsSuccess)
        {
            return CreateErrorResponse(inputResult.Error, stopwatch.ElapsedMilliseconds);
        }

        // 3. PreProcess (virtual - optional)
        var preProcessResult = await PreProcessAsync(task, context, cancellationToken);
        if (!preProcessResult.IsSuccess)
        {
            return CreateErrorResponse(preProcessResult.Error, stopwatch.ElapsedMilliseconds);
        }

        // 4. Invoke (abstract - must be implemented)
        var invokeResult = await InvokeAsync(task, context, cancellationToken);
        if (!invokeResult.IsSuccess)
        {
            return CreateErrorResponse(invokeResult.Error, stopwatch.ElapsedMilliseconds);
        }

        // 5. PostProcess (virtual - optional, e.g., correlation)
        var postProcessResult = await PostProcessAsync(task, invokeResult.Value!, 
            context, cancellationToken);
        if (!postProcessResult.IsSuccess)
        {
            return CreateErrorResponse(postProcessResult.Error, stopwatch.ElapsedMilliseconds);
        }

        // 6. ProcessOutput (virtual - custom per executor)
        var outputResult = await ProcessOutputAsync(task, invokeResult.Value!, 
            context, cancellationToken);
        if (!outputResult.IsSuccess)
        {
            return CreateErrorResponse(outputResult.Error, stopwatch.ElapsedMilliseconds);
        }

        stopwatch.Stop();

        // 7. CreateResponse
        return CreateSuccessResponse(invokeResult.Value!, outputResult.Value, 
            stopwatch.ElapsedMilliseconds);
    }

    // Template method hooks (virtual - can be overridden)
    protected virtual Task<Result> PrepareInputAsync(TTask task, TaskExecutorContext context, 
        CancellationToken cancellationToken) => Task.FromResult(Result.Ok());

    protected virtual Task<Result> PreProcessAsync(TTask task, TaskExecutorContext context, 
        CancellationToken cancellationToken) => Task.FromResult(Result.Ok());

    // Abstract method - must be implemented by concrete executors
    protected abstract Task<Result<TaskInvocationResult>> InvokeAsync(TTask task, 
        TaskExecutorContext context, CancellationToken cancellationToken);

    protected virtual Task<Result> PostProcessAsync(TTask task, TaskInvocationResult invocationResult, 
        TaskExecutorContext context, CancellationToken cancellationToken) 
        => Task.FromResult(Result.Ok());

    protected virtual Task<Result<object?>> ProcessOutputAsync(TTask task, 
        TaskInvocationResult invocationResult, TaskExecutorContext context, 
        CancellationToken cancellationToken)
        => Task.FromResult(Result<object?>.Ok(invocationResult.Data));
}
```

### 3. TaskExecutorRegistry

Resolves executors based on task type:

```csharp
public sealed class TaskExecutorRegistry : ITaskExecutorRegistry
{
    private readonly IEnumerable<ITaskExecutor> _executors;
    private readonly ILogger<TaskExecutorRegistry> _logger;

    public TaskExecutorRegistry(
        IEnumerable<ITaskExecutor> executors,
        ILogger<TaskExecutorRegistry> logger)
    {
        _executors = executors;
        _logger = logger;
    }

    public Result<ITaskExecutor> GetExecutor(TaskType taskType)
    {
        var executor = _executors.FirstOrDefault(e => e.TaskType == taskType);
        
        if (executor == null)
        {
            _logger.LogWarning("No executor found for task type {TaskType}", taskType);
            return Result<ITaskExecutor>.Fail(Error.NotFound(
                WorkflowErrorCodes.TaskExecution,
                $"No executor registered for task type: {taskType}"));
        }

        return Result<ITaskExecutor>.Ok(executor);
    }

    public bool HasExecutor(TaskType taskType)
    {
        return _executors.Any(e => e.TaskType == taskType);
    }
}
```

### 4. TaskExecutorContext

Context object passed to executors:

```csharp
public sealed class TaskExecutorContext
{
    public WorkflowTask Task { get; }
    public OnExecuteTask OnExecuteTask { get; }
    public ScriptContext ScriptContext { get; }
    public Guid? InstanceTransitionId { get; }
    public TaskTrigger TaskTrigger { get; }

    public TaskExecutorContext(
        WorkflowTask task,
        OnExecuteTask onExecuteTask,
        ScriptContext scriptContext,
        Guid? instanceTransitionId,
        TaskTrigger taskTrigger)
    {
        Task = task;
        OnExecuteTask = onExecuteTask;
        ScriptContext = scriptContext;
        InstanceTransitionId = instanceTransitionId;
        TaskTrigger = taskTrigger;
    }
}
```

### 5. StandardTaskResponse

Unified response from task execution:

```csharp
public sealed class StandardTaskResponse
{
    public bool IsSuccess { get; init; }
    public object? Data { get; init; }
    public int? StatusCode { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
    public string? ErrorMessage { get; init; }
    public long ExecutionDurationMs { get; init; }
    public string? TaskType { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}
```

## Built-in Executors

### DaprServiceTaskExecutor

Executes Dapr service invocation tasks via remote invoker:

```csharp
public sealed class DaprServiceTaskExecutor : TaskExecutorBase<DaprServiceTask>
{
    private readonly IRemoteInvokerService _remoteInvoker;
    private readonly IScriptEngine _scriptEngine;

    public DaprServiceTaskExecutor(
        IRemoteInvokerService remoteInvoker,
        IScriptEngine scriptEngine,
        ILogger<DaprServiceTaskExecutor> logger)
        : base(logger)
    {
        _remoteInvoker = remoteInvoker;
        _scriptEngine = scriptEngine;
    }

    public override TaskType TaskType => TaskType.DaprService;

    protected override async Task<Result> PrepareInputAsync(
        DaprServiceTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var mapping = context.OnExecuteTask.Mapping;
        if (mapping == null || string.IsNullOrEmpty(mapping.DecodedCode))
        {
            return Result.Ok();
        }

        var result = await ResultExtensions.TryAsync(async ct =>
        {
            var scriptRunner = await _scriptEngine.CompileToInstanceAsync<IMapping>(
                mapping.DecodedCode,
                cancellationToken: ct);

            await scriptRunner.InputHandler(task, context.ScriptContext);
        }, cancellationToken, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"DaprService task input handler failed: {ex.Message}"));

        return result;
    }

    protected override async Task<Result<TaskInvocationResult>> InvokeAsync(
        DaprServiceTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        // Map domain task to execution binding
        var envelopeResult = TaskBindingMapper.CreateEnvelope(task);
        if (!envelopeResult.IsSuccess)
        {
            return Result<TaskInvocationResult>.Fail(envelopeResult.Error);
        }

        // Create trace context for distributed tracing
        var traceContext = _remoteInvoker.CreateTraceContext(context.ScriptContext);

        // Invoke via remote execution service
        return await _remoteInvoker.InvokeAsync(
            TaskTypes.DaprService,
            task.Key,
            envelopeResult.Value!,
            traceContext,
            cancellationToken);
    }

    protected override async Task<Result<object?>> ProcessOutputAsync(
        DaprServiceTask task,
        TaskInvocationResult invocationResult,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var mapping = context.OnExecuteTask.Mapping;
        if (mapping == null || string.IsNullOrEmpty(mapping.DecodedCode))
        {
            return Result<object?>.Ok(invocationResult.Data);
        }

        // Update context with response for output mapping
        UpdateScriptContextWithResponse(task.Key, invocationResult, context.ScriptContext);

        var result = await ResultExtensions.TryAsync<object?>(async ct =>
        {
            var scriptRunner = await _scriptEngine.CompileToInstanceAsync<IMapping>(
                mapping.DecodedCode,
                cancellationToken: ct);

            var outputResponse = await scriptRunner.OutputHandler(context.ScriptContext);
            return outputResponse.Data;
        }, cancellationToken, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"DaprService task output handler failed: {ex.Message}"));

        return result;
    }
}
```

### ScriptTaskExecutor

Executes local C# scripts:

```csharp
public sealed class ScriptTaskExecutor : TaskExecutorBase<ScriptTask>
{
    private readonly IScriptEngine _scriptEngine;

    public ScriptTaskExecutor(
        IScriptEngine scriptEngine,
        ILogger<ScriptTaskExecutor> logger)
        : base(logger)
    {
        _scriptEngine = scriptEngine;
    }

    public override TaskType TaskType => TaskType.Script;

    protected override async Task<Result<TaskInvocationResult>> InvokeAsync(
        ScriptTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var result = await ResultExtensions.TryAsync(async ct =>
        {
            var scriptRunner = await _scriptEngine.CompileToInstanceAsync<IScriptExecution>(
                task.Script.DecodedCode,
                cancellationToken: ct);

            return await scriptRunner.ExecuteAsync(context.ScriptContext, ct);
        }, cancellationToken, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"Script execution failed: {ex.Message}"));

        if (!result.IsSuccess)
        {
            return Result<TaskInvocationResult>.Fail(result.Error);
        }

        return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Success(
            data: result.Value,
            taskType: TaskType.ToString()));
    }
}
```

### TriggerTaskExecutorBase

Base class for trigger tasks (StartTrigger, DirectTrigger, SubProcess):

```csharp
public abstract class TriggerTaskExecutorBase<TTask> : TaskExecutorBase<TTask>
    where TTask : WorkflowTask
{
    protected readonly IScriptEngine ScriptEngine;
    protected readonly IServiceScopeFactory ServiceScopeFactory;

    protected TriggerTaskExecutorBase(
        IScriptEngine scriptEngine,
        IServiceScopeFactory serviceScopeFactory,
        ILogger logger)
        : base(logger)
    {
        ScriptEngine = scriptEngine;
        ServiceScopeFactory = serviceScopeFactory;
    }

    protected override async Task<Result> PrepareInputAsync(
        TTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var mapping = context.OnExecuteTask.Mapping;
        if (mapping == null || string.IsNullOrEmpty(mapping.DecodedCode))
        {
            return Result.Ok();
        }

        var result = await ResultExtensions.TryAsync(async ct =>
        {
            var scriptRunner = await ScriptEngine.CompileToInstanceAsync<IMapping>(
                mapping.DecodedCode,
                cancellationToken: ct);

            await scriptRunner.InputHandler(task, context.ScriptContext);
        }, cancellationToken, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"Input handler failed for {TaskType}: {ex.Message}"));

        return result;
    }
}
```

## TaskBindingMapper

Maps domain `WorkflowTask` instances to execution `Binding` types:

```csharp
public static class TaskBindingMapper
{
    /// <summary>
    /// Creates a TaskEnvelope from a WorkflowTask.
    /// </summary>
    public static Result<TaskEnvelope> CreateEnvelope(WorkflowTask task)
    {
        return MapToBinding(task)
            .Map(result => new TaskEnvelope
            {
                TaskType = result.TaskType,
                TaskKey = task.Key,
                Binding = JsonSerializer.SerializeToElement(result.Binding, result.Binding.GetType())
            });
    }

    private static Result<(string TaskType, object Binding)> MapToBinding(WorkflowTask task)
    {
        try
        {
            var result = task switch
            {
                HttpTask http => (TaskTypes.Http, MapHttpTask(http)),
                DaprServiceTask daprService => (TaskTypes.DaprService, MapDaprServiceTask(daprService)),
                DaprBindingTask daprBinding => (TaskTypes.DaprBinding, MapDaprBindingTask(daprBinding)),
                DaprHttpEndpointTask daprHttp => (TaskTypes.DaprHttpEndpoint, MapDaprHttpEndpointTask(daprHttp)),
                DaprPubSubTask daprPubSub => (TaskTypes.DaprPubSub, MapDaprPubSubTask(daprPubSub)),
                NotificationTask notification => (TaskTypes.Notification, MapNotificationTask(notification)),
                StartTask startTask => (TaskTypes.StartTrigger, MapStartTask(startTask)),
                GetInstanceDataTask getData => (TaskTypes.GetInstanceData, MapGetInstanceDataTask(getData)),
                _ => throw new NotSupportedException($"Task type {task.GetType().Name} not supported")
            };

            return Result<(string TaskType, object Binding)>.Ok(result);
        }
        catch (Exception ex)
        {
            return Result<(string TaskType, object Binding)>.Fail(
                Error.Failure(WorkflowErrorCodes.TaskBindingMappingFailed, ex.Message));
        }
    }

    /// <summary>
    /// Maps DaprServiceTask to DaprServiceBinding.
    /// Note: HttpVerb → Method property mapping.
    /// </summary>
    private static DaprServiceBinding MapDaprServiceTask(DaprServiceTask task) => new()
    {
        AppId = task.AppId,
        MethodName = task.MethodName,
        Method = task.HttpVerb,
        QueryString = task.QueryString,
        Headers = task.Headers?.GetRawText(),
        Body = task.Body?.GetRawText()
    };
}
```

## Task Factory & Pooling

### TaskFactory

Creates isolated task instances for execution:

```csharp
public sealed class TaskFactory : ITaskFactory
{
    private readonly IComponentCacheStore _componentCacheStore;
    private readonly ILogger<TaskFactory> _logger;

    public async Task<Result<WorkflowTask>> CreateExecutionTaskAsync(
        Reference taskReference,
        CancellationToken cancellationToken)
    {
        var task = await _componentCacheStore.GetTaskAsync(taskReference, cancellationToken);
        
        if (task == null)
        {
            return Result<WorkflowTask>.Fail(Error.NotFound(
                WorkflowErrorCodes.TaskExecution,
                $"Task not found: {taskReference.Key}"));
        }

        // Clone to prevent cache contamination
        return Result<WorkflowTask>.Ok(task.Clone());
    }
}
```

### PooledTaskFactory

High-performance variant using object pooling:

```csharp
public sealed class PooledTaskFactory : ITaskFactory
{
    // Object pooling for high-throughput scenarios
    // 60-80% performance improvement under load
}
```

**Configuration:**
```json
{
  "TaskFactory": {
    "UseObjectPooling": true,
    "MaxPoolSize": 1000,
    "InitialPoolSize": 100,
    "PooledTaskTypes": ["DaprServiceTask", "HttpTask", "ScriptTask"]
  }
}
```

## Task Persistence Strategies

### ITaskPersistenceStrategy

```csharp
public interface ITaskPersistenceStrategy
{
    bool CanHandle(TaskTrigger taskTrigger);
    Task HandleCreationAsync(InstanceTask instanceTask, CancellationToken cancellationToken);
    Task HandleCompletionAsync(InstanceTask instanceTask, CancellationToken cancellationToken);
}
```

### StandardTaskPersistenceStrategy

For standard workflow tasks:

```csharp
public sealed class StandardTaskPersistenceStrategy : ITaskPersistenceStrategy
{
    public bool CanHandle(TaskTrigger taskTrigger)
    {
        return taskTrigger is TaskTrigger.OnExecute or TaskTrigger.OnEntry or TaskTrigger.OnExit;
    }

    public async Task HandleCreationAsync(InstanceTask instanceTask, CancellationToken cancellationToken)
    {
        await instanceTaskRepository.InsertAsync(instanceTask, true, cancellationToken);
    }

    public async Task HandleCompletionAsync(InstanceTask instanceTask, CancellationToken cancellationToken)
    {
        await instanceTaskRepository.UpdateAsync(instanceTask, true, cancellationToken);
    }
}
```

### ExtensionTaskPersistenceStrategy

For extension tasks:

```csharp
public sealed class ExtensionTaskPersistenceStrategy : ITaskPersistenceStrategy
{
    public bool CanHandle(TaskTrigger taskTrigger)
    {
        return taskTrigger == TaskTrigger.Extension;
    }

    public async Task HandleCreationAsync(InstanceTask instanceTask, CancellationToken cancellationToken)
    {
        // Extensions don't persist on creation
        await Task.CompletedTask;
    }

    public async Task HandleCompletionAsync(InstanceTask instanceTask, CancellationToken cancellationToken)
    {
        // Only persist completed extension tasks for audit
        await instanceTaskRepository.InsertAsync(instanceTask, true, cancellationToken);
    }
}
```

## Dependency Injection

```csharp
public static IServiceCollection AddTaskExecutionServices(this IServiceCollection services)
{
    // Task Coordinator
    services.AddScoped<ITaskCoordinator, TaskCoordinator>();
    
    // Executor Registry
    services.AddScoped<ITaskExecutorRegistry, TaskExecutorRegistry>();
    
    // Task Executors
    services.AddScoped<ITaskExecutor, DaprServiceTaskExecutor>();
    services.AddScoped<ITaskExecutor, DaprBindingTaskExecutor>();
    services.AddScoped<ITaskExecutor, DaprHttpEndpointTaskExecutor>();
    services.AddScoped<ITaskExecutor, DaprPubSubTaskExecutor>();
    services.AddScoped<ITaskExecutor, HttpTaskExecutor>();
    services.AddScoped<ITaskExecutor, ScriptTaskExecutor>();
    services.AddScoped<ITaskExecutor, NotificationTaskExecutor>();
    services.AddScoped<ITaskExecutor, HumanTaskExecutor>();
    
    // Trigger Executors
    services.AddScoped<ITaskExecutor, StartTriggerTaskExecutor>();
    services.AddScoped<ITaskExecutor, DirectTriggerTaskExecutor>();
    services.AddScoped<ITaskExecutor, SubProcessTaskExecutor>();
    services.AddScoped<ITaskExecutor, GetInstanceDataTaskExecutor>();
    
    // Evaluators
    services.AddScoped<IConditionEvaluator, ScriptConditionEvaluator>();
    services.AddScoped<ITimerEvaluator, ScriptTimerEvaluator>();
    
    // Remote Invoker
    services.AddScoped<IRemoteInvokerService, RemoteInvokerService>();
    
    // Task Factory
    services.ConfigureTaskFactory();
    
    // Persistence Strategies
    services.AddScoped<ITaskPersistenceStrategy, StandardTaskPersistenceStrategy>();
    services.AddScoped<ITaskPersistenceStrategy, ExtensionTaskPersistenceStrategy>();
    services.AddScoped<ITaskPersistenceStrategyFactory, TaskPersistenceStrategyFactory>();

    return services;
}
```

## Best Practices

### 1. Use Template Method Pattern

Override only the lifecycle methods you need:

```csharp
public sealed class MyCustomTaskExecutor : TaskExecutorBase<MyCustomTask>
{
    public override TaskType TaskType => TaskType.Custom;

    // Only override what's needed
    protected override async Task<Result<TaskInvocationResult>> InvokeAsync(
        MyCustomTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        // Custom invocation logic
    }
}
```

### 2. Use Result Pattern Throughout

```csharp
// Good: Consistent error handling
var result = await ResultExtensions.TryAsync(async ct =>
{
    return await externalService.CallAsync(request, ct);
}, cancellationToken, ex => Error.Failure("code", ex.Message));

if (!result.IsSuccess)
{
    Logger.LogError("Failed: {Error}", result.Error.Message);
}
```

### 3. Use TaskBindingMapper for Remote Execution

```csharp
// Good: Clean separation between domain and execution
var envelopeResult = TaskBindingMapper.CreateEnvelope(task);
if (envelopeResult.IsSuccess)
{
    await _remoteInvoker.InvokeAsync(taskType, taskKey, envelopeResult.Value!, traceContext, ct);
}
```

### 4. Update Script Context with Response

```csharp
// Good: Make response available for output mapping
private static void UpdateScriptContextWithResponse(
    string taskKey,
    TaskInvocationResult result,
    ScriptContext context)
{
    var response = new StandardTaskResponse
    {
        IsSuccess = result.IsSuccess,
        Data = result.Data,
        StatusCode = result.StatusCode,
        Headers = result.Headers,
        ErrorMessage = result.ErrorMessage
    };
    
    context.SetStandardResponse(response, taskKey.ToVariableName());
}
```

## Related Documentation

- [Task Invoker Architecture](./task-invoker-architecture.md) - Execution Service invokers
- [Scripting Engine](./scripting-engine.md) - Input/output mapping scripts
- [Task Factory Pooling](./task-factory-pooling.md) - Object pooling configuration
- [Result Pattern & Railway Programming](./result-pattern-railway.md) - Error handling
