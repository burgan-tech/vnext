# Task Invoker Architecture

## Overview

The Task Invoker system provides a clean separation between task execution in the **Orchestration Service** and the actual task invocation in the **Execution Service**. This architecture enables:

- Independent scaling of execution infrastructure
- Clean API contracts via binding models
- Stateless execution with typed bindings
- Support for both local and remote task execution

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────┐
│                     Orchestration Service                                │
│  ┌──────────────────────────────────────────────────────────────────┐   │
│  │  TaskCoordinator                                                  │   │
│  │  ┌─────────────────┐  ┌─────────────────┐  ┌──────────────────┐  │   │
│  │  │ TaskExecutorBase │  │TaskBindingMapper│  │RemoteInvokerSvc  │  │   │
│  │  │   (Template)     │──│  (Domain→Exec)  │──│  (Dapr Call)     │  │   │
│  │  └─────────────────┘  └─────────────────┘  └──────────────────┘  │   │
│  └──────────────────────────────────────────────────────────────────┘   │
│                                    │                                     │
│                                    │ TaskEnvelope                        │
│                                    ▼                                     │
└─────────────────────────────────────────────────────────────────────────┘
                                     │
                           ┌─────────┴─────────┐
                           │   Dapr Service    │
                           │    Invocation     │
                           └─────────┬─────────┘
                                     │
┌─────────────────────────────────────────────────────────────────────────┐
│                       Execution Service                                  │
│  ┌──────────────────────────────────────────────────────────────────┐   │
│  │  TaskInvokerRegistry                                              │   │
│  │  ┌─────────────────┐  ┌─────────────────┐  ┌──────────────────┐  │   │
│  │  │ ITaskInvoker<T> │  │ ITaskInvoker<T> │  │  ITaskInvoker<T> │  │   │
│  │  │   HttpTask      │  │  DaprService    │  │   DaprBinding    │  │   │
│  │  └─────────────────┘  └─────────────────┘  └──────────────────┘  │   │
│  └──────────────────────────────────────────────────────────────────┘   │
│                                    │                                     │
│                                    ▼                                     │
│                         TaskInvocationResult                             │
└─────────────────────────────────────────────────────────────────────────┘
```

## Core Components

### 1. Task Types

Constants defining supported task types for invoker routing:

```csharp
public static class TaskTypes
{
    // Remote execution tasks
    public const string Http = "http";
    public const string DaprService = "daprservice";
    public const string DaprBinding = "daprbinding";
    public const string DaprHttpEndpoint = "daprhttpendpoint";
    public const string DaprPubSub = "daprpubsub";
    public const string Notification = "notification";

    // Trigger tasks (for cross-domain execution)
    public const string StartTrigger = "starttrigger";
    public const string DirectTrigger = "directtrigger";
    public const string SubProcess = "subprocess";
    public const string GetInstanceData = "getinstancedata";
    public const string GetInstances = "getinstances";
}
```

**Mapping Notes:**
- `StartTask` and `GetInstanceDataTask` are mapped directly to bindings.
- `DirectTriggerTask`, `SubProcessTask`, and `GetInstancesTask` require runtime context and are handled by dedicated remote executors with pre-built bindings.

### 2. TaskEnvelope

Transport container for task execution:

```csharp
public sealed class TaskEnvelope
{
    /// <summary>
    /// Task type discriminator for invoker resolution (e.g., "http", "daprservice").
    /// </summary>
    public required string TaskType { get; init; }
    
    /// <summary>
    /// Version of the binding schema.
    /// </summary>
    public string Version { get; init; } = "1.0.0";
    
    /// <summary>
    /// Task key for logging and tracing.
    /// </summary>
    public string TaskKey { get; init; }
    
    /// <summary>
    /// Raw binding configuration as JsonElement for dynamic deserialization.
    /// </summary>
    public required JsonElement Binding { get; init; }
}
```

### 3. TaskInvocationResult

Standardized result from task execution:

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
    
    // Factory methods
    public static TaskInvocationResult Success(
        object? data = null,
        string? body = null,
        int statusCode = 200,
        long executionDurationMs = 0,
        string? taskType = null,
        Dictionary<string, string>? headers = null,
        Dictionary<string, object>? metadata = null);
    
    public static TaskInvocationResult Failure(
        string error,
        int? statusCode = null,
        string? body = null,
        long executionDurationMs = 0,
        string? taskType = null,
        Dictionary<string, string>? headers = null,
        object? data = null,
        Dictionary<string, object>? metadata = null);
}
```

### 4. TaskInvokeRequest and Trace Context

Remote invocation wraps the envelope with trace context:

```csharp
public sealed class TaskTraceContext
{
    public Guid InstanceId { get; init; }
    public string Domain { get; init; } = string.Empty;
    public string WorkflowKey { get; init; } = string.Empty;
    public string? WorkflowVersion { get; init; }
}

public sealed class TaskInvokeRequest
{
    public required TaskEnvelope Envelope { get; init; }
    public TaskTraceContext? TraceContext { get; init; }
}
```

### 4. ITaskInvoker Interface

```csharp
/// <summary>
/// Non-generic task invoker interface for registry operations.
/// </summary>
public interface ITaskInvoker
{
    /// <summary>
    /// Task type this invoker handles (e.g., "http", "daprService").
    /// </summary>
    string TaskType { get; }
    
    /// <summary>
    /// The binding type this invoker expects.
    /// </summary>
    Type BindingType { get; }
    
    /// <summary>
    /// Invokes the task with raw JSON binding.
    /// </summary>
    Task<TaskInvocationResult> InvokeAsync(
        string? taskKey,
        JsonElement binding,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Strongly-typed task invoker interface.
/// </summary>
public interface ITaskInvoker<TBinding> : ITaskInvoker where TBinding : class
{
    /// <summary>
    /// Invokes the task with strongly-typed binding.
    /// </summary>
    Task<TaskInvocationResult> InvokeAsync(
        TaskDescriptor<TBinding> descriptor,
        CancellationToken cancellationToken = default);
}
```

### 5. TaskInvokerRegistry

Routes task invocations to appropriate invokers:

```csharp
public sealed class TaskInvokerRegistry : ITaskInvokerRegistry
{
    private readonly IReadOnlyDictionary<string, ITaskInvoker> _invokers;
    private readonly ILogger<TaskInvokerRegistry> _logger;

    public TaskInvokerRegistry(
        IEnumerable<ITaskInvoker> invokers,
        ILogger<TaskInvokerRegistry> logger)
    {
        _invokers = invokers.ToDictionary(
            i => i.TaskType,
            StringComparer.OrdinalIgnoreCase);
    }

    public ITaskInvoker? GetInvoker(string taskType)
        => _invokers.GetValueOrDefault(taskType);

    public bool HasInvoker(string taskType)
        => _invokers.ContainsKey(taskType);

    public async Task<TaskInvocationResult> InvokeAsync(
        TaskEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        if (!_invokers.TryGetValue(envelope.TaskType, out var invoker))
        {
            return TaskInvocationResult.Failure(
                $"No invoker registered for task type: {envelope.TaskType}");
        }
        
        return await invoker.InvokeAsync(
            envelope.TaskKey,
            envelope.Binding,
            cancellationToken);
    }
}
```

## Binding Types

### DaprServiceBinding

```csharp
public sealed class DaprServiceBinding
{
    public required string AppId { get; init; }
    public required string MethodName { get; init; }
    public string Method { get; init; } = "POST";
    public string? QueryString { get; init; }
    public string? Headers { get; init; }
    public string? Body { get; init; }
}
```

### DaprHttpEndpointBinding

```csharp
public sealed class DaprHttpEndpointBinding
{
    public required string EndpointName { get; init; }
    public required string Path { get; init; }
    public string Method { get; init; } = "GET";
    public string? Body { get; init; }
}
```

### HttpTaskBinding

```csharp
public sealed class HttpTaskBinding
{
    public required string Url { get; init; }
    public string Method { get; init; } = "GET";
    public string? Headers { get; init; }
    public string? Body { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
    public bool ValidateSSL { get; init; } = true;
}
```

### DaprBindingTaskBinding

```csharp
public sealed class DaprBindingTaskBinding
{
    public required string BindingName { get; init; }
    public string Operation { get; init; } = "create";
    public string? Body { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}
```

### DaprPubSubBinding

```csharp
public sealed class DaprPubSubBinding
{
    public required string PubSubName { get; init; }
    public required string TopicName { get; init; }
    public string? Body { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}
```

### NotificationBinding

```csharp
public sealed class NotificationBinding
{
    public string? BindingName { get; init; }
    public string Operation { get; init; } = "create";
    public string? Body { get; init; }
    public string[]? To { get; init; }
    public string? Subject { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
    public string? BindingKind { get; init; }
}
```

### Trigger Bindings

```csharp
public sealed class StartTriggerBinding
{
    public required string Domain { get; init; }
    public required string Workflow { get; init; }
    public string? Version { get; init; }
    public string? Key { get; init; }
    public object? Body { get; init; }
    public string[]? Tags { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
    public bool Sync { get; init; } = true;
    public bool UseDapr { get; init; } = false;
    public string? BaseUrl { get; init; }
    public string? DaprAppId { get; init; }
}

public sealed class DirectTriggerBinding
{
    public required string Domain { get; init; }
    public required string Workflow { get; init; }
    public required string TransitionName { get; init; }
    public Guid? InstanceId { get; set; }
    public string? Key { get; set; }
    public string? Version { get; set; }
    public string[]? Tags { get; set; }
    public JsonElement? Body { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
    public bool Sync { get; init; } = true;
    public bool UseDapr { get; init; } = false;
    public string? BaseUrl { get; init; }
    public string? DaprAppId { get; init; }
}

public sealed class GetInstanceDataBinding
{
    public required string Domain { get; init; }
    public required string Workflow { get; init; }
    public required string Instance { get; init; }
    public string[]? Extensions { get; init; }
    public string? ETag { get; init; }
    public bool UseDapr { get; init; } = false;
    public string? BaseUrl { get; init; }
    public string? DaprAppId { get; init; }
}

public sealed class GetInstancesBinding
{
    public required string Domain { get; init; }
    public required string Workflow { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 10;
    public string? Sort { get; init; }
    public string[]? Filter { get; init; }
    public bool UseDapr { get; init; } = false;
    public string? BaseUrl { get; init; }
    public string? DaprAppId { get; init; }
}

public sealed class SubProcessBinding
{
    public required string Domain { get; init; }
    public required string Workflow { get; init; }
    public string? Version { get; init; }
    public required Guid InstanceId { get; init; }
    public string? Callback { get; init; }
    public string? Key { get; set; }
    public JsonElement? Body { get; init; }
    public string[]? Tags { get; init; }
    public string? Headers { get; init; }
    public Dictionary<string, object>? ExtraProperties { get; init; }
    public bool Sync { get; init; } = true;
    public bool UseDapr { get; init; } = false;
    public string? BaseUrl { get; init; }
    public string? DaprAppId { get; init; }
}
```

## Invoker Implementations

### DaprServiceTaskInvoker

```csharp
public sealed class DaprServiceTaskInvoker(
    DaprClient daprClient,
    ILogger<DaprServiceTaskInvoker> logger,
    ITaskMetrics? metrics = null)
    : ITaskInvoker<DaprServiceBinding>
{
    public string TaskType => TaskTypes.DaprService;
    public Type BindingType => typeof(DaprServiceBinding);

    public async Task<TaskInvocationResult> InvokeAsync(
        TaskDescriptor<DaprServiceBinding> descriptor,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(descriptor.TaskKey, descriptor.Binding, cancellationToken);
    }

    public async Task<TaskInvocationResult> InvokeAsync(
        string? taskKey,
        JsonElement binding,
        CancellationToken cancellationToken = default)
    {
        var typedBinding = binding.Deserialize<DaprServiceBinding>()
            ?? throw new InvalidOperationException("Failed to deserialize DaprServiceBinding");
        
        return await ExecuteAsync(taskKey, typedBinding, cancellationToken);
    }

    private async Task<TaskInvocationResult> ExecuteAsync(
        string? taskKey,
        DaprServiceBinding binding,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var request = daprClient.CreateInvokeMethodRequest(
                new HttpMethod(binding.Method),
                binding.AppId,
                binding.MethodName);

            // Add query string
            if (!string.IsNullOrEmpty(binding.QueryString))
            {
                var uriBuilder = new UriBuilder(request.RequestUri!);
                uriBuilder.Query = binding.QueryString.TrimStart('?');
                request.RequestUri = uriBuilder.Uri;
            }

            // Add body for non-GET requests
            if (request.Method != HttpMethod.Get && !string.IsNullOrEmpty(binding.Body))
            {
                request.Content = new StringContent(binding.Body, Encoding.UTF8, "application/json");
            }

            // Add headers
            if (!string.IsNullOrEmpty(binding.Headers))
            {
                var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(binding.Headers);
                if (headers != null)
                {
                    foreach (var header in headers)
                    {
                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
            }

            var response = await daprClient.InvokeMethodAsync<object?>(request, cancellationToken);
            stopwatch.Stop();

            _metrics.RecordDaprServiceInvocation(binding.AppId, binding.MethodName, "success");

            return TaskInvocationResult.Success(
                data: response,
                body: response != null ? JsonSerializer.Serialize(response) : null,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics.RecordDaprServiceInvocation(binding.AppId, binding.MethodName, "failure");

            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType);
        }
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

    private static DaprServiceBinding MapDaprServiceTask(DaprServiceTask task) => new()
    {
        AppId = task.AppId,
        MethodName = task.MethodName,
        Method = task.HttpVerb,  // Note: Domain uses HttpVerb, Binding uses Method
        QueryString = task.QueryString,
        Headers = task.Headers?.GetRawText(),
        Body = task.Body?.GetRawText()
    };
}
```

## RemoteInvokerService

Handles remote task execution via Dapr service invocation:

```csharp
public sealed class RemoteInvokerService : IRemoteInvokerService
{
    private readonly DaprClient _daprClient;
    private readonly string _executionServiceAppId;
    private readonly ILogger<RemoteInvokerService> _logger;

    public RemoteInvokerService(
        DaprClient daprClient,
        IConfiguration configuration,
        ILogger<RemoteInvokerService> logger)
    {
        _daprClient = daprClient;
        _executionServiceAppId = configuration["ExecutionApi:AppId"] ?? "vnext-execution";
        _logger = logger;
    }

    public async Task<Result<TaskInvocationResult>> InvokeAsync(
        string taskType,
        string taskKey,
        TaskEnvelope envelope,
        TaskTraceContext traceContext,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var request = new TaskInvokeRequest
        {
            Envelope = envelope,
            TraceContext = traceContext
        };

        var result = await ResultExtensions.TryAsync(async ct =>
        {
            var httpRequest = _daprClient.CreateInvokeMethodRequest(
                _executionServiceAppId,
                $"/api/v1/execution/invoke/{taskType}/{taskKey}",
                request);

            httpRequest.Headers.Add(WorkflowInfo.Name, WorkflowInfo.Generate(
                traceContext.Domain ?? "unknown",
                traceContext.WorkflowKey ?? "unknown",
                traceContext.WorkflowVersion ?? "latest",
                traceContext.InstanceId));

            return await _daprClient.InvokeMethodAsync<TaskInvokeResponse>(httpRequest, ct);
        }, cancellationToken);

        stopwatch.Stop();

        if (!result.IsSuccess)
        {
            return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Failure(
                error: result.Error.Message ?? "Remote invocation failed",
                statusCode: 500,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: taskType));
        }

        var response = result.Value!;
        var remoteResult = new TaskInvocationResult
        {
            IsSuccess = response.Result!.IsSuccess,
            StatusCode = response.Result.StatusCode,
            Body = response.Result.Body,
            Data = response.Result.Data,
            ErrorMessage = response.Result.ErrorMessage,
            Headers = response.Result.Headers,
            TaskType = response.Result.TaskType,
            Metadata = response.Result.Metadata,
            ExecutionDurationMs = stopwatch.ElapsedMilliseconds
        };

        return Result<TaskInvocationResult>.Ok(remoteResult);
    }

    public TaskTraceContext CreateTraceContext(ScriptContext scriptContext)
    {
        return new TaskTraceContext
        {
            InstanceId = scriptContext.Instance.Id,
            Domain = scriptContext.Workflow.Domain,
            WorkflowKey = scriptContext.Workflow.Key,
            WorkflowVersion = scriptContext.Workflow.Version
        };
    }
}
```

## Local vs Remote Execution

### Local Execution Flow

```
TaskCoordinator
    │
    ├── TaskFactory.CreateExecutionTaskAsync()
    ├── TaskExecutorRegistry.GetExecutor()
    ├── TaskExecutor.ExecuteAsync()
    │       │
    │       ├── PrepareInputAsync() - Run input mapping script
    │       ├── InvokeAsync() - Execute task locally
    │       └── ProcessOutputAsync() - Run output mapping script
    │
    └── Apply result to context
```

### Remote Execution Flow

```
TaskCoordinator
    │
    ├── TaskFactory.CreateExecutionTaskAsync()
    ├── TaskExecutorRegistry.GetExecutor()
    ├── TaskExecutor.ExecuteAsync()
    │       │
    │       ├── PrepareInputAsync() - Run input mapping script
    │       ├── TaskBindingMapper.CreateEnvelope()
    │       ├── RemoteInvokerService.InvokeAsync()
    │       │       │
    │       │       ├── Create TaskEnvelope
    │       │       ├── Dapr Service Invocation → Execution Service
    │       │       └── Receive TaskInvocationResult
    │       │
    │       └── ProcessOutputAsync() - Run output mapping script
    │
    └── Apply result to context
```

## DI Registration

### Execution Service

```csharp
public static IServiceCollection AddExecutionServices(this IServiceCollection services)
{
    // Invoker Registry
    services.AddSingleton<ITaskInvokerRegistry, TaskInvokerRegistry>();
    
    // Task Invokers
    services.AddSingleton<ITaskInvoker, HttpTaskInvoker>();
    services.AddSingleton<ITaskInvoker, DaprServiceTaskInvoker>();
    services.AddSingleton<ITaskInvoker, DaprBindingTaskInvoker>();
    services.AddSingleton<ITaskInvoker, DaprHttpEndpointTaskInvoker>();
    services.AddSingleton<ITaskInvoker, DaprPubSubTaskInvoker>();
    services.AddSingleton<ITaskInvoker, NotificationTaskInvoker>();
    services.AddSingleton<ITaskInvoker, StartTriggerRemoteInvoker>();
    services.AddSingleton<ITaskInvoker, DirectTriggerRemoteInvoker>();
    services.AddSingleton<ITaskInvoker, SubProcessRemoteInvoker>();
    services.AddSingleton<ITaskInvoker, GetInstanceDataRemoteInvoker>();
    services.AddSingleton<ITaskInvoker, GetInstancesRemoteInvoker>();
    
    return services;
}
```

### Orchestration Service

```csharp
public static IServiceCollection AddOrchestrationServices(this IServiceCollection services)
{
    // Remote Invoker
    services.AddScoped<IRemoteInvokerService, RemoteInvokerService>();
    
    // Task Executors
    services.AddScoped<ITaskExecutorRegistry, TaskExecutorRegistry>();
    services.AddScoped<ITaskExecutor, HttpTaskExecutor>();
    services.AddScoped<ITaskExecutor, DaprServiceTaskExecutor>();
    // ... other executors
    
    return services;
}
```

## Best Practices

### 1. Use Typed Bindings

```csharp
// Good: Strongly-typed binding
var binding = new DaprServiceBinding
{
    AppId = "my-service",
    MethodName = "/api/v1/process",
    Method = "POST",
    Body = JsonSerializer.Serialize(payload)
};

// Avoid: Untyped dictionaries
var binding = new Dictionary<string, object>
{
    ["appId"] = "my-service",
    ["methodName"] = "/api/v1/process"
};
```

### 2. Include Metadata for Debugging

```csharp
return TaskInvocationResult.Failure(
    error: ex.Message,
    metadata: new Dictionary<string, object>
    {
        ["TaskType"] = TaskType,
        ["AppId"] = binding.AppId,
        ["MethodName"] = binding.MethodName,
        ["ExceptionType"] = ex.GetType().Name
    });
```

### 3. Use Metrics for Observability

```csharp
_metrics.RecordDaprServiceInvocation(binding.AppId, binding.MethodName, "success");
```

### 4. Handle Cancellation Properly

```csharp
catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
{
    _metrics.RecordDaprServiceInvocation(binding.AppId, binding.MethodName, "cancelled");
    
    return TaskInvocationResult.Failure(
        error: "Task execution was cancelled",
        metadata: new Dictionary<string, object> { ["Cancelled"] = true });
}
```

## Related Documentation

- [Task Executors](./task-executors.md) - Orchestration-side task execution
- [Architecture Overview](./architecture-overview.md) - Service separation
- [Result Pattern & Railway Programming](./result-pattern-railway.md) - Error handling

