# Transition Pipeline Architecture

This documentation describes the **Transition Pipeline Architecture** implemented in the BBT.Workflow project.

## Overview

The architecture is designed to make transition execution **readable, testable, and extensible**. It reduces complexity on the `StateMachineExecutor` and defines Auto/Schedule **re-entry** scenarios as first-class citizens.

## Core Principles

- **SRP & Separation of Concerns:** Sync/Async mode selection ≠ Trigger type management ≠ Lifecycle step execution
- **Deterministic Lifecycle:** Well-defined and documented sequence
- **Context Rehydration:** Context is not carried over in Auto/Schedule re-entry; it is rebuilt in a new DI scope
- **No Service Locator:** Services are not in Context but injected into steps/handlers via DI
- **Idempotency & Lock:** Instance-based locking and idempotency is a core feature
- **Post-Commit Processing:** External side effects run outside the distributed lock via post-commit jobs
- **Sync Dispatch Chain:** Automatic transitions are chained within the pipeline using `NextTransitionRequest`

## Architecture Components

### 1. Core Interfaces

#### TriggerType Enum
```csharp
public enum TriggerType
{
    Manual = 0,     // User-triggered
    Automatic = 1,  // System-triggered automatically
    Scheduled = 2,  // Timer-triggered
    Event = 3       // Externally triggered by event
}
```

#### ITransitionStep
```csharp
public interface ITransitionStep
{
    int Order { get; }
    string Name => GetType().Name;
    Task<Result<StepOutcome>> ExecuteAsync(TransitionExecutionContext context, CancellationToken cancellationToken);
}
```

**Note:** Pipeline steps use the Result pattern, returning `Result<StepOutcome>` for exception-free error handling and flow control.

**Aether SDK Integration:** All pipeline steps use `[Trace]` attribute for automatic OpenTelemetry span creation:

```csharp
public sealed class ChangeStateStep : ITransitionStep
{
    public int Order => LifecycleOrder.ChangeState;

    [Trace]  // Aether SDK aspect for distributed tracing
    public async Task<Result<StepOutcome>> ExecuteAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        // Set display name for trace visualization
        Activity.Current?.SetDisplayName($"[{Order}] {nameof(ChangeStateStep)}");
        
        // Railway chain with Tap and OnSuccess for side effects
        return await Result.Ok(BuildStateTransitionInfo(context))
            .Tap(info => RecordTransitionMetric(context, info))
            .TapAsync(info => PerformStateChangeAsync(context, info, cancellationToken))
            .ThenAsync(_ => UpdateTargetStateInContext(context))
            .OnSuccess(_ => RecordStateEntryMetric(context))
            .OnSuccess(_ => LogStateChange(context))
            .MapAsync(_ => StepOutcome.Continue());
    }
}
```

#### ITransitionHandler
```csharp
public interface ITransitionHandler
{
    bool CanHandle(TriggerType triggerType);
    Task PreHandleAsync(TransitionExecutionContext context, CancellationToken cancellationToken);
    Task PostHandleAsync(TransitionExecutionContext context, CancellationToken cancellationToken);
}
```

### 2. TransitionRunner

The `TransitionRunner` executes a single transition in an isolated DI scope with a `RequiresNew` Unit of Work. Sync dispatch chaining is handled by `TransitionPipeline`, not by the runner.

#### ITransitionRunner Interface
```csharp
public interface ITransitionRunner
{
    /// <summary>
    /// Runs a transition in its own DI scope + RequiresNew UoW.
    /// </summary>
    Task<Result<TransitionOutput>> RunAsync(
        WorkflowExecutionContext context,
        CancellationToken cancellationToken = default);
}
```

#### TransitionRunner Implementation
```csharp
public sealed class TransitionRunner(
    IServiceScopeFactory scopeFactory) : ITransitionRunner
{
    [Trace]
    public async Task<Result<TransitionOutput>> RunAsync(
        WorkflowExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var hopResult = await ExecuteWithScopeAsync(context, cancellationToken);
        if (!hopResult.IsSuccess)
            return Result<TransitionOutput>.Fail(hopResult.Error);

        return Result<TransitionOutput>.Ok(hopResult.Value!.Output);
    }

    private async Task<Result<TransitionCoreOutput>> ExecuteWithScopeAsync(
        WorkflowExecutionContext context,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var uowManager = sp.GetRequiredService<IUnitOfWorkManager>();
        var core = sp.GetRequiredService<IWorkflowExecutionCore>();

        await using var uow = await uowManager.BeginAsync(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew },
            cancellationToken);

        var coreResult = await core.ExecuteTransitionCoreAsync(context, cancellationToken);
        if (!coreResult.IsSuccess)
            return Result<TransitionCoreOutput>.Fail(coreResult.Error);

        await uow.CommitAsync(cancellationToken);
        return coreResult;
    }
}
```

**Key Design Decisions:**
- **Isolated DI Scope**: Each transition runs in a fresh scope
- **RequiresNew UoW**: Isolation from ambient transactions
- **Sync Chain in Pipeline**: Automatic transition chaining is delegated to `TransitionPipeline`

### 3. IWorkflowExecutionCore

Core transition execution contract without UoW management:

```csharp
public interface IWorkflowExecutionCore
{
    /// <summary>
    /// Executes a single transition's core logic without managing UoW.
/// Returns the transition output from core execution.
    /// </summary>
    Task<Result<TransitionCoreOutput>> ExecuteTransitionCoreAsync(
        WorkflowExecutionContext context,
        CancellationToken cancellationToken = default);
}
```

#### TransitionCoreOutput
```csharp
/// <summary>
/// Output from core transition execution.
/// </summary>
public sealed record TransitionCoreOutput(TransitionOutput Output);
```

### 4. Pipeline Steps (Lifecycle Order)

All steps implement `ITransitionStep` and return `Result<StepOutcome>`. Orders are defined by `LifecycleOrder`:

| Order | Step | Description |
|-------|------|-------------|
| 5 | HandleCancelPreflightStep | Detects cancel transitions and short-circuits |
| 9 | HandleUpdateDataPreflightStep | Handles update-data transitions for SubFlow states |
| 10 | ForwardToActiveSubflowStep | Forwards to active subflow if exists |
| 19 | SetBusyStep | Marks instance Busy before processing |
| 20 | CreateTransitionRecordStep | Persists transition record |
| 30 | RunOnExecuteTasksStep | Runs transition OnExecute tasks |
| 40 | RunOnExitTasksStep | Runs current state OnExit tasks |
| 50 | ChangeStateStep | Changes instance state |
| 60 | RunOnEntryTasksStep | Runs target state OnEntry tasks |
| 70 | HandleSubFlowStep | Creates correlation and enqueues post-commit subflow start |
| 79 | ClearBusyOnResumeStep | Clears Busy status on resume |
| 80 | ScheduleTransitionsStep | Schedules timed transitions |
| 90 | RunAutomaticTransitionsStep | Evaluates auto transitions |
| 100 | HandleFinishStep | Handles workflow completion |
| 110 | FinalizeTransitionStep | Finalizes transition and cleanup |
| 111 | AfterEpilogueRefresh | Refreshes context after epilogue |
| 112 | ResolveAvailableStep | Sets instance Active when appropriate |

### 5. LifecycleOrder

Constants defining the standard execution order for transition lifecycle steps:

```csharp
public static class LifecycleOrder
{
    public const int Preflight = 5;
    public const int CheckParentUpdateDataTransition = ForwardToActiveSubflow - 1;
    public const int ForwardToActiveSubflow = 10;
    public const int SetBusy = CreateTransition - 1;
    public const int CreateTransition = 20;
    public const int OnExecute = 30;
    public const int OnExit = 40;
    public const int ChangeState = 50;
    public const int OnEntry = 60;
    public const int SubFlow = 70;
    public const int ClearBusyOnResumeStep = Schedule - 1;
    public const int Schedule = 80;
    public const int Auto = 90;
    public const int Finish = 100;
    public const int Finalize = 110;
    public const int AfterEpilogueRefresh = Finalize + 1;
    public const int ResolveAvailable = Finalize + 2;
}
```

### 6. Trigger Handlers

#### ManualTransitionHandler
- Policy/HMAC/Auth/Schema validation
- User authorization
- Audit logging

#### AutomaticTransitionHandler
- Chain depth control
- Execution metrics
- System state validation

```csharp
public sealed class AutomaticTransitionHandler(
    ITransitionValidationService validationService,
    ILogger<AutomaticTransitionHandler> logger) : TransitionHandlerBase(logger, validationService)
{
    public override bool CanHandle(TriggerType triggerType) => triggerType == TriggerType.Automatic;

    protected override async Task PreValidateAsync(TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        await ValidateSystemStateAsync(context);
        await base.PreValidateAsync(context, cancellationToken);
    }
}
```

#### ScheduledTransitionHandler
- Timing validation
- Schedule constraints
- Recurring schedule management

#### EventTransitionHandler
- Event source validation
- Event correlation
- Payload validation

### 7. Sync Dispatch Chain & Post-Commit Jobs

Automatic chaining is handled inside `TransitionPipeline` using `NextTransitionRequest`. External side effects are deferred via post-commit jobs.

#### NextTransitionRequest
```csharp
public sealed record NextTransitionRequest(
    string TransitionKey,
    string? Reason = null);
```

#### PipelineDirectives (excerpt)
```csharp
public sealed class PipelineDirectives
{
    public int? ResumeFromOrder { get; private set; }
    public EpilogueMode Epilogue { get; private set; } = EpilogueMode.Run;
    public NextTransitionRequest? NextTransition { get; private set; }
    public bool TerminalReached { get; private set; }
    public bool IsSubFlowResume { get; private set; }
    public bool BoundaryAbortRequested { get; private set; }

    public void RequestNextTransition(NextTransitionRequest request) => NextTransition = request;
    public void EnqueuePostCommit(IPostCommitJob job) { /* enqueue with idempotency */ }
    public IReadOnlyList<IPostCommitJob> ConsumePostCommitJobs() { /* ... */ }
}
```

### 8. TransitionPipeline

The pipeline orchestrates the execution of transition lifecycle steps in a deterministic order.

#### Core Responsibilities
- Orders all registered pipeline steps by their `Order` property
- Executes steps sequentially with proper error handling
- Manages dynamic re-planning based on directive changes
- Manages distributed lock acquisition/release per transition
- Executes post-commit jobs outside the lock
- Chains automatic transitions via `NextTransitionRequest`
- Provides structured logging and distributed tracing for each step
- Returns `Result` to indicate success or failure without throwing exceptions

#### Implementation

```csharp
public class TransitionPipeline
{
    private readonly IReadOnlyList<ITransitionStep> _steps;

    public TransitionPipeline(IEnumerable<ITransitionStep> steps)
    {
        _steps = steps.OrderBy(s => s.Order).ToList();
    }

    [Trace]
    public async Task<Result> RunAsync(TransitionExecutionContext context, CancellationToken cancellationToken)
    {
        var state = CreateInitialState(context);

        while (state.HasMoreSteps())
        {
            // Guard: Skip immediate execution requested
            if (context.SkipImmediateExecution)
                return Result.Ok();

            // Execute current step
            var stepResult = await ExecuteStepAsync(state.CurrentStep, context, cancellationToken);
            if (!stepResult.IsSuccess)
                return Result.Fail(stepResult.Error);

            // Determine flow control based on step outcome
            var flowControl = DetermineFlowControl(stepResult.Value!, state.CurrentStep, context, state);

            // Apply flow control decision
            if (flowControl.ShouldStop)
                break;

            if (flowControl.ShouldReplan)
            {
                state = CreateInitialState(context);
                continue;
            }

            state = state.MoveNext();
        }

        return Result.Ok();
    }
}
```

#### Execution Plan Building
```csharp
private IReadOnlyList<ITransitionStep> BuildExecutionPlan(TransitionExecutionContext context)
{
    var ordered = _steps.ToList();

    // 1) ResumeFrom start
    var startOrder = context.Directives.ConsumeResumeFrom();
    if (startOrder.HasValue)
        ordered = ordered.Where(s => s.Order >= startOrder.Value).ToList();

    // 2) Subflow terminal short circuit (until Finalize)
    if (context.Directives.TerminalReached)
    {
        var maxOrder = LifecycleOrder.Finalize;
        ordered = ordered.Where(s => s.Order <= maxOrder).ToList();
    }

    // 3) Epilogue policy
    if (context.Directives.Epilogue == EpilogueMode.Skip)
    {
        ordered = ordered
            .Where(s => s.Order != LifecycleOrder.Schedule &&
                        s.Order != LifecycleOrder.Auto)
            .ToList();
    }

    return ordered;
}
```

#### Pipeline State Management
```csharp
/// <summary>
/// Represents the current execution state of the pipeline.
/// Immutable record struct for functional state management.
/// </summary>
private readonly record struct PipelineState(IReadOnlyList<ITransitionStep> Plan, int Index)
{
    public ITransitionStep CurrentStep => Plan[Index];
    public bool HasMoreSteps() => Index < Plan.Count;
    public PipelineState MoveNext() => this with { Index = Index + 1 };
}

/// <summary>
/// Represents flow control decision after step execution.
/// Factory methods provide clear intent.
/// </summary>
private readonly record struct FlowControl(bool ShouldStop, bool ShouldReplan)
{
    public static FlowControl Stop() => new(ShouldStop: true, ShouldReplan: false);
    public static FlowControl Replan() => new(ShouldStop: false, ShouldReplan: true);
    public static FlowControl Continue() => new(ShouldStop: false, ShouldReplan: false);
}
```

### 9. PipelineDirectives

Directive system controlling pipeline behavior:

```csharp
public sealed class PipelineDirectives
{
    /// <summary>
    /// Gets the order number from which to resume pipeline execution.
    /// </summary>
    public int? ResumeFromOrder { get; private set; }
    
    /// <summary>
    /// Gets the epilogue execution mode.
    /// </summary>
    public EpilogueMode Epilogue { get; private set; } = EpilogueMode.Run;
    
    /// <summary>
    /// Gets a value indicating whether the pipeline has reached a terminal state.
    /// </summary>
    public bool TerminalReached { get; private set; }
    
    /// <summary>
    /// Gets a value indicating whether this execution is resuming from a subflow.
    /// </summary>
    public bool IsSubFlowResume { get; private set; }
    
    /// <summary>
    /// Gets the next transition request for sync dispatch chain.
    /// </summary>
    public NextTransitionRequest? NextTransition { get; private set; }
    
    /// <summary>
    /// Gets a value indicating whether an error boundary abort was requested.
    /// </summary>
    public bool BoundaryAbortRequested { get; private set; }
    
    // Methods (excerpt)
    public void RequestResumeFrom(int order);
    public int? ConsumeResumeFrom();
    public void RequestEpilogue(EpilogueMode mode);
    public void MarkTerminal();
    public void MarkAsSubFlowResume();
    public void RequestNextTransition(NextTransitionRequest request);
    public NextTransitionRequest? ConsumeNextTransition();
    public void EnqueuePostCommit(IPostCommitJob job);
    public IReadOnlyList<IPostCommitJob> ConsumePostCommitJobs();
    public void RequestBoundaryAbort();
}
```

#### Error Boundary Integration

Task steps use `BoundaryOutcomeHandler` to translate error boundary actions into pipeline directives:

- **Log/Ignore**: continue pipeline
- **Abort/Notify/Rollback with transition**: request `NextTransition` and skip to Finalize
- **Abort without transition**: request boundary abort and skip to Finalize without faulting

#### EpilogueMode
```csharp
public enum EpilogueMode
{
    Run = 0,          // Run Schedule and Auto steps normally
    Skip = 1,         // Skip Schedule and Auto steps
    DispatchOnly = 2  // Only dispatch, don't execute
}
```

### 10. StepOutcome

Pipeline step result reporting and flow control:

```csharp
public sealed class StepOutcome
{
    public bool StopPipeline { get; init; }
    public int? SkipToOrder { get; init; }
    public string? NextTransitionKey { get; init; }
    public Action<PipelineDirectives>? MutateDirectives { get; init; }
    
    // Factory methods
    public static StepOutcome Continue();
    public static StepOutcome Stop();
    public static StepOutcome SkipTo(int order);
    public static StepOutcome With(Action<PipelineDirectives> fx);
    public static StepOutcome SkipToFinalize();
}
```

**Usage Examples:**
```csharp
// Normal continuation
return Result<StepOutcome>.Ok(StepOutcome.Continue());

// Stop the pipeline
return Result<StepOutcome>.Ok(StepOutcome.Stop());

// Skip to a specific step
return Result<StepOutcome>.Ok(StepOutcome.SkipTo(LifecycleOrder.CreateTransition));

// Mutate directives
return Result<StepOutcome>.Ok(StepOutcome.With(d => d.RequestEpilogue(EpilogueMode.Skip)));

// Error case
return Result<StepOutcome>.Fail(Error.Validation("step.failed", "Validation failed"));
```

### 11. TransitionExecutionContext

Minimal, service-free context structure that carries only essential data and state:

```csharp
public sealed class TransitionExecutionContext
{
    // Identity (immutable)
    public string Domain { get; init; }
    public Guid InstanceId { get; init; }
    public string WorkflowKey { get; init; }
    public string TransitionKey { get; init; }
    public TriggerType Trigger { get; init; }
    public ExecutionActor Actor { get; set; }
    public string? InstanceKey { get; set; }
    public string[]? Tags { get; set; }
    
    // Correlation and tracing
    public string CorrelationId { get; init; }
    public string? CausationId { get; init; }
    public string ExecutionChainId { get; init; }
    public int ChainDepth { get; init; }
    public DateTimeOffset RequestedAt { get; init; }
    
    // Definitions (rehydrated)
    public Definitions.Workflow Workflow { get; init; }
    public State Current { get; set; }
    public State? Target { get; set; }
    public Transition? Transition { get; init; }
    
    // Instance snapshot
    public Instance Instance { get; set; }
    public string ConcurrencyToken { get; set; }
    public object? Data { get; set; }
    
    // Execution flags
    public bool SkipImmediateExecution { get; set; }
    public bool IsReentry { get; init; }
    
    // Telemetry + request data
    public string TraceId { get; init; }
    public string SpanId { get; init; }
    public IReadOnlyDictionary<string, string?> Headers { get; init; }
    public IReadOnlyDictionary<string, string?> RouteValues { get; init; }
    
    // Temporary storage
    public IDictionary<string, object?> Items { get; }
    public IDictionary<string, object?> Cache { get; }
    
    // Pipeline directives
    public PipelineDirectives Directives { get; }
    
    // Client response
    public ClientResponse? ClientResponse { get; set; }
    
    // Helper methods
    public string LockKey { get; }
    public JsonElement? DataElement { get; }
    public Task<ScriptContext> GetOrBuildScriptContextAsync(
        Func<CancellationToken, Task<ScriptContext>> factory,
        CancellationToken cancellationToken);
    public void ApplyScriptContextChanges(ScriptContext scriptContext);
    public void ClearCacheForFinalize();
}
```

**Key Design Principles:**
- **Service-Free**: Services are injected into steps/handlers, not stored in context
- **Minimal**: Contains only essential data needed across pipeline steps
- **Rehydrated**: Definitions and instance state are fetched fresh for each execution
- **Items vs Cache**: 
  - `Items`: Temporary storage shared between steps (cleared after execution)
  - `Cache`: Performance cache (ScriptContext, etc.) that can be cleared at finalize

## Execution Flow

### Complete Transition Flow

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                         WorkflowExecutionService                            │
│                    ExecuteTransitionAsync(context)                          │
└─────────────────────────────┬───────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                           TransitionRunner                                   │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │ ExecuteWithScopeAsync (isolated DI scope + RequiresNew UoW)           │  │
│  │ UoW Commit                                                           │  │
│  └───────────────────────────────────────────────────────────────────────┘  │
└─────────────────────────────┬───────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                      IWorkflowExecutionCore                                  │
│                  ExecuteTransitionCoreAsync(context)                         │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │ 1. GetExecutionStrategy(mode)                                         │  │
│  │ 2. ExecuteStrategyAsync(strategy, context)                            │  │
│  │ 3. BuildCoreOutputAsync                                              │  │
│  └───────────────────────────────────────────────────────────────────────┘  │
└─────────────────────────────┬───────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                       TransitionPipeline                                     │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │ Pipeline Steps (Order):                                               │  │
│  │   5  → ForwardToActiveSubflowStep                                     │  │
│  │   10 → HandleCancelPreflightStep                                      │  │
│  │   20 → CreateTransitionRecordStep                                     │  │
│  │   30 → RunOnExecuteTasksStep                                          │  │
│  │   40 → RunOnExitTasksStep                                             │  │
│  │   50 → ChangeStateStep                                                │  │
│  │   60 → RunOnEntryTasksStep                                            │  │
│  │   70 → HandleSubFlowStep                                              │  │
│  │   79 → ClearBusyOnResumeStep                                          │  │
│  │   80 → ScheduleTransitionsStep                                        │  │
│  │   90 → RunAutomaticTransitionsStep (NextTransitionRequest)            │  │
│  │  100 → HandleFinishStep                                               │  │
│  │  110 → FinalizeTransitionStep                                         │  │
│  │  111 → AfterEpilogueRefresh                                           │  │
│  │  112 → ResolveAvailableStep                                           │  │
│  └───────────────────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────────────────┘
```

### Sync Dispatch Chain Flow

```
Transition A completes
       │
       ▼
RunAutomaticTransitionsStep
       │
       ├── Evaluate all auto transitions
       │
       └── Winner found?
            │
            ├── Yes → Directives.RequestNextTransition(...)
            │
            └── No  → Continue
                       │
                       ▼
                 FinalizeTransitionStep
                       │
                       ▼
        TransitionPipeline consumes NextTransition
                       │
                       ▼
        CreateNextWorkflowContext(...) and repeat
```

## Integration Points (Execution, Gateway, Discovery, Caching)

### Execution Services
- **`TransitionContextFactory`** rehydrates workflow definitions via `IComponentCacheStore` and loads instances via `IInstanceRepository`.
- **`TransitionPipeline`** manages distributed locking, post-commit jobs, and sync dispatch chaining.
- **`PostCommitExecutor`** runs deferred jobs (e.g., SubFlow start/forward) outside the lock with idempotency checks.

### Gateway (Cross-Domain Routing)
- **`IInstanceCommandGateway`** routes start/transition/complete calls locally or remotely based on target domain.
- **`IInstanceQueryGateway`** routes query operations (instance, data, history, schema, views, extensions).

### Discovery (Endpoint Resolution)
- **`IDomainDiscoveryResolver`** resolves domain endpoints (URL or Dapr) with caching and fallback behavior.
- **`IDomainRegistrationService`** registers the current domain for discovery during runtime initialization.

### Caching (Definitions & Runtime)
- **`ComponentCacheStore`** reads definition artifacts (workflow, tasks, schemas, functions, views, extensions).
- **`RuntimeCacheBackend`** loads from the runtime service with smart version matching.
- **`RuntimeCacheInitializer`** seeds cache and triggers domain registration when discovery is enabled.

## Usage

### 1. DI Registration

```csharp
// All components are registered automatically
services.AddTransitionPipeline();

// Sync dispatch chain is handled by TransitionPipeline; no re-entry options required.
```

### 2. Transition Execution

```csharp
// Create execution context
var executionContext = new WorkflowExecutionContext
{
    Domain = "example",
    InstanceId = instanceId,
    WorkflowKey = "approval-workflow",
    TransitionKey = "approve",
    TriggerType = TriggerType.Manual,
    Mode = ExecMode.Sync, // or ExecMode.Async for background processing
    Actor = ExecutionActor.User,
    CorrelationId = Guid.NewGuid().ToString("N"),
    Data = jsonData,
    Headers = requestHeaders
};

// Execute transition using workflow execution service
var result = await workflowExecutionService.ExecuteTransitionAsync(executionContext, cancellationToken);

// Handle result
if (result.IsSuccess)
{
    var output = result.Value!;
    // Transition executed successfully
    // Access instance ID: output.Id
    // Access status: output.Status
}
else
{
    // Handle error
    var error = result.Error;
    // Log or process error: error.Code, error.Message
}
```

### 3. Adding a New Pipeline Step

```csharp
public sealed class CustomValidationStep : ITransitionStep
{
    private readonly IBusinessRuleValidator _validator;
    private readonly ILogger<CustomValidationStep> _logger;
    
    public CustomValidationStep(
        IBusinessRuleValidator validator,
        ILogger<CustomValidationStep> logger)
    {
        _validator = validator;
        _logger = logger;
    }
    
    public int Order => 25; // Between CreateTransition (20) and OnExecute (30)
    
    [Trace]
    public async Task<Result<StepOutcome>> ExecuteAsync(
        TransitionExecutionContext context, 
        CancellationToken cancellationToken)
    {
        Activity.Current?.SetDisplayName($"[{Order}] {nameof(CustomValidationStep)}");
        
        _logger.LogDebug("Validating business rules for transition {TransitionKey}", 
            context.TransitionKey);
        
        // Custom validation logic
        var validationResult = await _validator.ValidateAsync(context, cancellationToken);
        
        if (!validationResult.IsValid)
        {
            // Return error using Result pattern
            return Result<StepOutcome>.Fail(
                Error.Validation("business.rule.failed", validationResult.ErrorMessage));
        }
        
        // Normal continuation
        return Result<StepOutcome>.Ok(StepOutcome.Continue());
    }
}

// Register in DI:
services.AddScoped<ITransitionStep, CustomValidationStep>();
```

### 4. Adding a New Trigger Handler

```csharp
public sealed class WebhookTransitionHandler : TransitionHandlerBase
{
    public override bool CanHandle(TriggerType triggerType) => triggerType == TriggerType.Event;
    
    protected override async Task PreValidateAsync(TransitionExecutionContext context, CancellationToken cancellationToken)
    {
        // Webhook-specific validation
        await ValidateWebhookSignature(context, cancellationToken);
    }
}

// Register in DI:
services.AddScoped<ITransitionHandler, WebhookTransitionHandler>();
```

## Benefits

### 1. Separation of Concerns
- Each pipeline step has a single responsibility
- Trigger handlers only manage their own trigger types
- Execution strategies only handle sync/async differences
- TransitionPipeline focuses on orchestration, locking, and sync dispatch chain
- TransitionRunner manages UoW lifecycle only

### 2. Dynamic Flow Control
- Step-level flow control with StepOutcome
- Runtime behavior changes with PipelineDirectives
- Flexible execution scenarios with dynamic re-planning
- Optimized for SubFlow and automatic transition chains
- Support for skip-to semantics and conditional execution

### 3. Exception-Free Error Handling
- Result pattern throughout the pipeline
- No exceptions for flow control
- Explicit error propagation
- Better error tracking and debugging
- Consistent error handling across all steps

### 4. Post-Commit Safety
- External side effects are deferred as post-commit jobs
- Jobs run outside the distributed lock to avoid deadlocks
- Idempotency is enforced for repeatable post-commit actions
- Data consistency guaranteed across lock boundaries

### 5. Testability
- Each component can be tested independently
- Easy mocking with dependency injection
- Pipeline execution can be tested with different directive scenarios
- Different flow scenarios can be tested with StepOutcome
- Clear boundaries between components

### 6. Extensibility
- New pipeline steps can be easily added without modifying existing code
- New trigger handlers can be added for custom trigger types
- Custom flow control with StepOutcome factory methods
- Directive-based behavior customization
- Order-based step insertion at any point in lifecycle

### 7. Performance
- Sync dispatch chain avoids unnecessary background jobs
- Distributed locking at service level
- Idempotency control with concurrency tokens and post-commit job keys
- Dynamic step skipping based on directives
- Efficient plan building with minimal allocations
- Isolated DI scope prevents memory leaks

### 8. Observability
- Structured logging with correlation IDs
- OpenTelemetry distributed tracing with `[Trace]` aspect
- Detailed telemetry for each step with timing
- Re-planning events tracking
- Step-level spans for detailed trace analysis
- Comprehensive error logging with context

## Best Practices

### Pipeline Steps
- Keep steps focused on single responsibility
- Use dependency injection for services
- Return appropriate `StepOutcome` for flow control
- Use Result pattern for error handling
- Use `[Trace]` attribute for automatic span creation
- Set `Activity.Current?.SetDisplayName()` for trace visualization
- Use Railway extensions (`Tap`, `OnSuccess`, `BindAsync`) for clean chains
- Log entry/exit for observability

### Trigger Handlers
- Validate trigger-specific requirements in `PreHandleAsync`
- Perform cleanup in `PostHandleAsync`
- Don't modify instance state directly
- Let pipeline steps handle state changes

### Error Handling
- Use Result pattern for expected errors
- Let exceptions bubble for unexpected errors
- Provide meaningful error codes and messages
- Include context in error details

### Performance
- Cache expensive operations in `context.Cache`
- Use `context.Items` for step-to-step data sharing
- Clear cache in finalize step if needed
- Minimize database round-trips
- Use `GetOrBuildScriptContextAsync` for ScriptContext caching

## Related Documentation

- [Auto Transition Evaluation](./auto-transition-evaluation.md) - Automatic transition condition evaluation
- [Aether SDK Aspects](./aether-sdk-aspects.md) - Cross-cutting concerns with `[Trace]`, `[UnitOfWork]`, `[Log]`
- [Result Pattern & Railway Programming](./result-pattern-railway.md) - Error handling with Result pattern
- [Strategy Pattern Implementation](./strategy-pattern-implementation.md) - Execution strategy details
- [OpenTelemetry Logging](./opentelemetry-logging.md) - Distributed tracing and logging
- [Background Jobs](./background-jobs.md) - Dapr Jobs integration
- [Task Executors](./task-executors.md) - Task execution system
