# Application Services

## Overview

The Application layer (`BBT.Workflow.Application`) exposes use-case oriented services that orchestrate domain objects, caching, scripting, and execution pipelines. All application services return `Result`/`ConditionalResult` (Railway pattern) and use Aether `ApplicationService` as the base class.

Instance operations follow a CQRS-style split with command and query services, while definition and function services expose administrative and read-only workflows.

## Module Map

- `Definitions`: publish and validate workflow components, cast to cache.
- `Instances`: command/query services, DTOs, and gateways.
- `Functions`: function execution over workflows.
- `Extensions`: runtime extension processing for instance responses.
- `Execution`: transition pipeline and execution services.
- `Tasks`: task coordination, execution engine, evaluators, persistence strategies.
- `SubFlow`: subflow/subprocess services.
- `Caching`, `Runtime`, `Scripting`, `Discovery`, `BackgroundJobs`: supporting infrastructure.

## Core Application Services

### 1. DefinitionAppService

Administrative service for workflow component publishing and cache invalidation.

```csharp
public interface IDefinitionAppService : IApplicationService
{
    Task<Result> PublishAsync(PublishInput input, CancellationToken cancellationToken = default);
    Task<Result> InvalidateCacheAsync(InvalidateCacheInput input, CancellationToken cancellationToken = default);
    Task<Result> ReInitializeAsync(CancellationToken cancellationToken = default);
}
```

Key behaviors:

- Uses `ICurrentSchema` and `IRuntimeInfoProvider` to target the correct schema.
- Validates components via `ComponentValidatorProcessor`.
- Casts published components to cache via `WorkflowCastProcessor`.

### 2. Instance Command Service

```csharp
public interface IInstanceCommandAppService : IApplicationService
{
    Task<Result<StartInstanceOutput>> StartAsync(
        StartInstanceInput input,
        CancellationToken cancellationToken = default);

    Task<Result<TransitionOutput>> TransitionAsync(
        string instance,
        string transitionKey,
        TransitionInput input,
        CancellationToken cancellationToken = default);
}
```

Notes:

- `StartAsync` performs idempotency checks and schema migration before execution.
- Transition execution is delegated to the transition pipeline (`IWorkflowExecutionService`).

### 3. Instance Query Service

```csharp
public interface IInstanceQueryAppService : IApplicationService
{
    Task<ConditionalResult<GetInstanceOutput>> GetInstanceAsync(
        GetInstanceInput input,
        CancellationToken cancellationToken = default);

    Task<Result<InstanceListWithGroupsResponse<GetInstanceOutput>>> GetInstanceListAsync(
        GetInstanceListInput input,
        CancellationToken cancellationToken = default);

    Task<Result<GetInstanceHistoryOutput>> GetInstanceHistoryAsync(
        GetInstanceHistoryInput input,
        CancellationToken cancellationToken = default);

    Task<ConditionalResult<GetInstanceDataOutput>> GetInstanceDataAsync(
        GetInstanceDataInput input,
        CancellationToken cancellationToken = default);

    Task<Result<GetInstanceStateOutput>> GetInstanceStateAsync(
        GetInstanceStateInput input,
        CancellationToken cancellationToken = default);

    Task<Result<GetViewOutput>> GetPlatformSpecificViewAsync(
        GetViewInput input,
        string? platform,
        string? transitionKey,
        CancellationToken cancellationToken = default);

    Task<Result<GetSchemaOutput>> GetSchemaAsync(
        GetSchemaInput input,
        string? transitionKey,
        CancellationToken cancellationToken = default);

    Task<Result<GetExtensionsOutput>> GetExtensionsAsync(
        GetExtensionsInput input,
        CancellationToken cancellationToken = default);
}
```

Notes:

- Supports ETag-based conditional reads.
- Uses optimized GraphQL-style filtering with optional grouping/aggregations.
- Extension processing is fail-fast (errors are propagated).

### 4. FunctionAppService

```csharp
public interface IFunctionAppService : IApplicationService
{
    Task<Result<Dictionary<string, dynamic?>>> GetFunctionByFunctionKeyAsync(
        string key,
        string flow,
        string domain,
        Dictionary<string, string?>? headers = null,
        Dictionary<string, string?>? queryParameters = null,
        CancellationToken cancellationToken = default);

    Task<Result<Dictionary<string, dynamic?>>> GetFunctionByInstanceAsync(
        string key,
        string flow,
        string domain,
        string instance,
        Dictionary<string, string?>? headers = null,
        Dictionary<string, string?>? queryParameters = null,
        CancellationToken cancellationToken = default);

    Task<Result<List<InstanceAndDataModel>>> GetDomainFunctionsAsync(
        string domain,
        CancellationToken cancellationToken = default);
}
```

`FunctionAppService` builds a `ScriptContext`, executes function tasks via `ITaskCoordinator`, and extracts function output from `ScriptContext.TaskResponse`.

### 5. Instance Extension Service

```csharp
public interface IInstanceExtensionService
{
    Task<Result<Dictionary<string, object>>> ProcessExtensionsAsync(
        string[]? extensionRequested,
        ScriptContext scriptContext,
        Definitions.Workflow workflow,
        ExtensionScope currentScope,
        CancellationToken cancellationToken = default);
}
```

Behavior:

- Executes core extensions first (runtime-wide).
- Executes workflow extensions second, skipping already executed core ones.
- Uses fail-fast behavior on extension failures.

### 6. SubFlow Services

SubFlow processing is split into focused services:

- `ISubflowStarter`, `ISubflowCompletionService`
- `ISubflowStateService`, `ISubflowForwardingService`
- `IChildSubflowCancellationService`

These services coordinate subflow/subprocess instance creation, completion, and state propagation.

## Execution and Task Coordination

### Transition Pipeline

Execution uses the pipeline architecture (`TransitionPipeline` + `IWorkflowExecutionService`). Scheduling, auto-transitions, and re-entry are handled by pipeline steps (see `Execution/Transitions`).

### Task Coordination

Task execution is split into:

- `TaskCoordinator`: groups tasks by order, runs sequential/parallel, and delegates execution.
- `TaskExecutionEngine`: executes a single task with error boundary resolution, persistence strategy, and metrics.
- `IConditionEvaluator` and `ITimerEvaluator`: script-based evaluators for conditions and timers.

## Remote Instance Services

Remote app services provide HTTP clients for instance endpoints:

- `IRemoteInstanceCommandAppService`
- `IRemoteInstanceQueryAppService`

Concrete implementations live in `BBT.Workflow.Infrastructure` and use `IDomainDiscoveryResolver` to locate the target runtime domain.

See [Remote Routing and Discovery](./remote-routing-and-discovery.md) for gateway routing, discovery, and remote client details.

## Dependency Injection

`AddApplicationModule()` wires application services, pipeline, tasks, cache, validators, and cast handlers:

```csharp
public static IServiceCollection AddApplicationModule(this IServiceCollection services)
{
    services.AddAetherApplication();
    services.AddPipelineServices();
    services.AddApplicationServices();
    services.AddCacheServices();
    services.AddTaskHandlers();
    services.AddComponentCacheHandlers();
    services.AddComponentValidators();
    return services;
}
```