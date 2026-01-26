# Auto Transition Condition Evaluation

## Overview

This document describes the auto transition condition evaluation system implemented in the workflow engine. The system evaluates automatic transition conditions within the `RunAutomaticTransitionsStep` pipeline step, using the Result pattern for exception-free error handling and **soft-fail behavior**.

## Motivation

Previously, automatic transition conditions were evaluated in the PreHandler, and when a condition was false, an exception was thrown. This approach had several issues:

1. **Exception misuse**: A false condition is a normal business outcome, not an error condition
2. **Performance**: Exception handling is expensive
3. **Logic placement**: PreHandler only knows about a single transition, making it difficult to implement "at least one must succeed" logic
4. **Unclear semantics**: Using exceptions for flow control obscures the actual business logic

## Key Behavior Change: Soft-Fail

The current implementation uses **soft-fail behavior**:

- If **no automatic transition condition is satisfied**, the pipeline **continues** and the instance **stays in the current state**
- This is logged as a warning, not treated as an error
- The workflow remains in a valid state, waiting for the next trigger

## Architecture

### Domain Layer

#### AutoConditionStatus Enum

```csharp
public enum AutoConditionStatus
{
    Satisfied,      // Condition is true, transition can execute
    NotSatisfied,   // Condition is false (normal business outcome)
    Failed          // Technical error during evaluation
}
```

#### AutoConditionEvaluation Struct

A readonly record struct that encapsulates the evaluation result:

```csharp
public readonly record struct AutoConditionEvaluation
{
    public string TransitionKey { get; init; }
    public AutoConditionStatus Status { get; init; }
    public Error? Error { get; init; }
    public bool IsSuccess => Status == AutoConditionStatus.Satisfied;
    public bool IsFailed => Status == AutoConditionStatus.Failed;
}
```

**Location**: `src/BBT.Workflow.Domain/Execution/Transitions/Evaluation/`

### Application Layer

#### IAutoConditionEvaluator Interface

Service interface for evaluating automatic transition conditions:

```csharp
public interface IAutoConditionEvaluator
{
    Task<Result<AutoConditionEvaluation>> EvaluateAsync(
        Transition transition,
        TransitionExecutionContext context,
        CancellationToken cancellationToken = default);
}
```

#### AutoConditionEvaluator Implementation

Concrete implementation that:
- Uses `ITaskConditionService` to execute condition scripts
- Uses `IScriptContextFactory` to build script contexts
- Returns structured evaluation results using the Result pattern
- Supports `DefaultAutoTransition` without a rule (auto-satisfied)
- Logs evaluation outcomes and errors
- Caches ScriptContext in the TransitionExecutionContext using `GetOrBuildScriptContextAsync`

**Location**: `src/BBT.Workflow.Application/Execution/Transitions/Evaluation/`

```csharp
public sealed class AutoConditionEvaluator(
    ITaskConditionService taskConditionService,
    IScriptContextFactory scriptContextFactory,
    IInstanceRepository instanceRepository,
    ILogger<AutoConditionEvaluator> logger,
    IRuntimeInfoProvider runtimeInfoProvider) : IAutoConditionEvaluator
{
    /// <summary>
    /// Evaluates the automatic transition condition.
    /// Railway chain: Validate Rule → Execute Script → Map to Evaluation
    /// </summary>
    public Task<Result<AutoConditionEvaluation>> EvaluateAsync(
        Transition transition,
        TransitionExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        return ValidateTransitionRule(transition)
            .BindAsync(_ => ExecuteConditionSafelyAsync(transition, context, cancellationToken));
    }
}
```

**Key Features**:
- **Railway Pattern**: Uses `BindAsync` for clean error chaining
- **TryAsync**: Wraps script execution safely with `ResultExtensions.TryAsync`
- **ScriptContext Caching**: Uses `context.GetOrBuildScriptContextAsync` to avoid rebuilding
- **Structured Error Handling**: Creates detailed errors with `ExecutionErrors.TransitionRuleEvaluationFailed`

#### RunAutomaticTransitionsStep

Updated pipeline step that:
1. Evaluates all automatic transitions for the target state
2. Finds the first satisfied transition
3. Requests the winning transition via `PipelineDirectives.RequestNextTransition`

**Location**: `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/Steps/`

```csharp
public sealed class RunAutomaticTransitionsStep(
    IAutoConditionEvaluator autoConditionEvaluator,
    ILogger<RunAutomaticTransitionsStep> logger) : ITransitionStep
{
    public int Order => LifecycleOrder.Auto; // 90

    [Trace]
    public async Task<Result<StepOutcome>> ExecuteAsync(
        TransitionExecutionContext context, 
        CancellationToken cancellationToken)
    {
        Activity.Current?.SetDisplayName($"[{Order}] {nameof(RunAutomaticTransitionsStep)}");

        // Check if target state has any automatic transitions
        if (context.Target?.AutoTransitions == null || !context.Target.AutoTransitions.Any())
        {
            return Result<StepOutcome>.Ok(StepOutcome.Continue());
        }

        // Railway chain: Evaluate all transitions -> Process winner (if exists)
        return await EvaluateAllTransitionsAsync(context, cancellationToken)
            .Map(evaluations => ProcessEvaluationResults(context, evaluations));
    }
}
```

**Note:** The step returns `StepOutcome.Continue()` after requesting `NextTransition`. The pipeline consumes the request after the current transition completes.

## Sync Dispatch Chain Execution

When an automatic transition is satisfied, it is **not** executed immediately. Instead:

1. **Request**: The winning transition is set as `NextTransition` on `PipelineDirectives`
2. **Finalize**: The current transition completes normally
3. **Chain**: `TransitionPipeline` consumes `NextTransition` and starts the next transition in the same sync dispatch loop

```csharp
private static void RequestWinningTransition(TransitionExecutionContext context, AutoConditionEvaluation winner)
{
    context.Directives.RequestNextTransition(
        new NextTransitionRequest(winner.TransitionKey, "auto"));
}
```

## Behavior

### Evaluation Flow

1. When a state has automatic transitions, `RunAutomaticTransitionsStep` evaluates them in `TriggerKind` order
2. For each transition:
   - If the rule is missing → **Fail** with validation error (pipeline stops)
   - If evaluation succeeds → Return **Satisfied** or **NotSatisfied** based on condition result
   - If a technical error occurs → **Fail** with technical error (pipeline stops)
3. After evaluations (short-circuits on first satisfied):
   - If at least one is **Satisfied** → Request the first satisfied transition for sync dispatch
   - If all are **NotSatisfied** → **Continue pipeline** (soft-fail), log warning, stay in current state
   - If any **Failed** → Fail immediately with the error

### Error Handling (Soft-Fail Model)

The system distinguishes three scenarios:

| Scenario | Status | Result | Behavior |
|----------|--------|--------|----------|
| Condition is true | Satisfied | NextTransition requested | Transition chained by `TransitionPipeline` |
| All conditions false | NotSatisfied | Pipeline continues | Instance stays in current state |
| Script error, missing rule | Failed | Pipeline fails | Error propagated to caller |

**Key Point**: When no automatic transition is satisfied, this is **not an error**. The instance remains in its current state, and the workflow can continue via other triggers (manual, timer, event).

## TransitionPipeline Integration

The `TransitionPipeline` owns the sync dispatch loop and executes chained transitions after the current pipeline completes:

```csharp
var nextTransition = context.Directives.ConsumeNextTransition();
if (nextTransition is null)
    return Result<TransitionExecutionContext>.Ok(context);

currentWorkflowContext = CreateNextWorkflowContext(context, nextTransition);
```

### Key Design Decisions

1. **Sync dispatch chain**: Automatic transitions are requested by directives and executed in the same pipeline loop
2. **Post-commit safety**: External calls run as post-commit jobs outside the distributed lock
3. **Deterministic chain**: Only the first satisfied auto transition is chained

## Key Benefits

1. **Soft-fail behavior**: No automatic transition satisfied is a valid outcome, not an error
2. **Clear semantics**: False conditions are business outcomes, not exceptions
3. **Better performance**: No exception throwing for normal flow
4. **Improved logging**: Structured logging of evaluation outcomes with `WorkflowLogs`
5. **Result pattern**: Exception-free error handling throughout
6. **Proper separation**: Business logic (which transition) vs technical concerns (how to evaluate)
7. **Testability**: Easier to unit test without catching exceptions
8. **Aether SDK integration**: Uses `[Trace]` aspect for automatic span creation
9. **Post-commit safety**: External side effects run after lock release

## Dependency Injection

The services are registered via `AddPipelineServices()` inside `AddApplicationModule()`:

```csharp
// In WorkflowApplicationModuleServiceCollectionExtensions.AddApplicationModule()
services.AddPipelineServices();
```

## Testing Considerations

When testing automatic transitions:

1. **Test all three outcomes**: Satisfied, NotSatisfied, and Failed
2. **Test multiple transitions**: Ensure first satisfied wins
3. **Test all NotSatisfied**: Ensure pipeline continues (soft-fail) and instance stays in current state
4. **Test technical errors**: Script compilation errors, missing rules should fail the pipeline
5. **Verify logging**: Check that `WorkflowLogs.AutoTransitionConditionNotSatisfied` is called when no winner
6. **Test sync dispatch chain**: Verify `NextTransitionRequest` triggers the next transition
7. **Test chain depth**: Verify `Execution.ChainDepth` increments across chained transitions

## Migration Notes

### PreHandler Changes

**Important**: No changes are required in the `AutomaticTransitionHandler.PreHandle` method. The condition evaluation logic has been completely moved to `RunAutomaticTransitionsStep`.

### Backward Compatibility

**Important behavioral change:**
- Previously: If no automatic transition condition was satisfied, the pipeline returned a validation error (HTTP 400)
- Now: If no automatic transition condition is satisfied, the pipeline **continues** and the instance **stays in the current state**

This is a **non-breaking change** for most workflows:
- Workflows that always have at least one satisfied auto-transition work identically
- Workflows that sometimes have no satisfied auto-transitions now stay in the current state instead of failing
- This allows workflows to be designed with optional auto-transitions

## Performance Impact

Expected performance improvements:
- **No exceptions**: Eliminates exception construction and stack unwinding for false conditions
- **Cached ScriptContext**: ScriptContext is created once and reused via `GetOrBuildScriptContextAsync`
- **Structured logging**: Better performance than exception-based logging
- **Sync dispatch chain**: No background job overhead for auto transitions

## Related Documentation

- [Transition Pipeline Architecture](../architecture/transition-pipeline.md)
- [Result Pattern](../sdk/result-pattern.md)
- [Scripting Engine](scripting-engine.md)
- [Task Executors](../implementation/task-executors.md)
