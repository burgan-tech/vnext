using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Policies;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Shared;
using BBT.Workflow.Validation;
using System.Diagnostics;
using BBT.Aether.Results;

namespace BBT.Workflow.Execution.Validation;

/// <inheritdoc />
public class TransitionValidationService(
    TransitionExecutionPolicy transitionExecutionPolicy,
    IJsonSchemaValidator schemaValidator,
    IComponentCacheStore componentCacheStore) : ITransitionValidationService
{
    /// <inheritdoc />
    /// <summary>
    /// Validates a transition execution context.
    /// Railway chain: Schema Validation → State Machine Policy Validation
    /// </summary>
    public async Task<Result> ValidateAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        // 1. Schema Validation
        var schemaResult = await ValidateTransitionSchemaAsync(context, cancellationToken);
        if (!schemaResult.IsSuccess)
            return schemaResult;

        // 2. State Machine Validation using Specification Pattern
        // Includes: Actor authorization, state transition rules, SubFlow bypass, etc.
        var policyResult = transitionExecutionPolicy.Validate(context);
        if (!policyResult.IsSuccess)
            return policyResult;

        return Result.Ok();
    }

    /// <summary>
    /// Validates transition data against JSON schema.
    /// Chains GetSchemaAsync Result into validation.
    /// </summary>
    private async Task<Result> ValidateTransitionSchemaAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        // Guard: No schema defined
        if (context.Transition?.Schema is null)
            return Result.Ok();

        var schemaResult = await componentCacheStore.GetSchemaAsync(
            context.Transition.Schema, cancellationToken);

        if (!schemaResult.IsSuccess)
            return Result.Fail(schemaResult.Error);

        return schemaValidator.Validate(schemaResult.Value!.Schema, context.DataElement);
    }

    /// <inheritdoc />
    /// <summary>
    /// Validates a start transition.
    /// Railway chain: Get Initial State → Build Context → Validate
    /// </summary>
    public async Task<Result> ValidateStartTransitionAsync(
        Definitions.Workflow workflow,
        Instance instance,
        Transition transition,
        object? data,
        IRuntimeInfoProvider runtimeInfoProvider,
        IReadOnlyDictionary<string, string?>? headers = null,
        CancellationToken cancellationToken = default)
    {
        var contextResult = workflow.GetInitialState()
            .Map(initialState => BuildStartTransitionContext(
                workflow, instance, transition, initialState,
                data, runtimeInfoProvider, headers));

        if (!contextResult.IsSuccess)
            return Result.Fail(contextResult.Error);

        return await ValidateAsync(contextResult.Value!, cancellationToken);
    }

    /// <summary>
    /// Builds a TransitionExecutionContext for start transition validation.
    /// </summary>
    private TransitionExecutionContext BuildStartTransitionContext(
        Definitions.Workflow workflow,
        Instance instance,
        Transition transition,
        State initialState,
        object? data,
        IRuntimeInfoProvider runtimeInfoProvider,
        IReadOnlyDictionary<string, string?>? headers)
    {
        var (traceId, spanId) = InitializeTelemetry();

        return new TransitionExecutionContext
        {
            // Identity
            Domain = runtimeInfoProvider.Domain,
            InstanceId = instance.Id,
            WorkflowKey = workflow.Key,
            TransitionKey = transition.Key,
            Trigger = TriggerType.Manual, // Start transitions are always manual
            Actor = ExecutionActor.User, // Start transitions are always user-initiated
            CorrelationId = Guid.NewGuid().ToString("N"),
            CausationId = null,
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            ChainDepth = 0,
            RequestedAt = DateTimeOffset.UtcNow,

            // Definitions
            Workflow = workflow,
            Current = initialState,
            Transition = transition,

            // Instance state
            Instance = instance,
            Data = data,

            // Flags
            IsReentry = false, // Start transitions are never re-entry

            // Telemetry
            TraceId = traceId,
            SpanId = spanId,
            Headers = headers ?? new Dictionary<string, string?>()
        };
    }

    /// <summary>
    /// Initializes telemetry context for distributed tracing.
    /// </summary>
    private static (string TraceId, string SpanId) InitializeTelemetry()
    {
        var activity = Activity.Current;
        var traceId = activity?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        var spanId = activity?.SpanId.ToString() ?? Guid.NewGuid().ToString("N")[..16];

        return (traceId, spanId);
    }

    /// <inheritdoc />
    /// <summary>
    /// Validates trigger type rules and execution context.
    /// </summary>
    public Task<Result> ValidateTriggerTypeAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var result = context.Trigger switch
        {
            TriggerType.Manual => ValidateManualTrigger(context),
            TriggerType.Automatic => ValidateAutomaticTrigger(context),
            TriggerType.Scheduled => ValidateScheduledTrigger(context),
            TriggerType.Event => ValidateEventTrigger(context),
            _ => Result.Fail(Error.NotSupported(
                "UnsupportedTriggerType",
                $"Trigger type {context.Trigger} is not supported"))
        };

        return Task.FromResult(result);
    }

    /// <summary>
    /// Validates manual trigger execution rules.
    /// Manual transitions should be initiated by User actors.
    /// </summary>
    private static Result ValidateManualTrigger(TransitionExecutionContext context)
    {
        // Actor should be User for manual transitions
        if (context.Actor != ExecutionActor.User)
        {
            return Result.Fail(WorkflowErrors.InvalidActorForManualTransition(
                context.InstanceId,
                context.Actor));
        }

        return Result.Ok();
    }

    /// <summary>
    /// Validates automatic trigger execution rules.
    /// Automatic transitions should be initiated by System actors and respect chain depth limits.
    /// </summary>
    private static Result ValidateAutomaticTrigger(TransitionExecutionContext context)
    {
        // Actor should be System for automatic transitions
        if (context.Actor != ExecutionActor.System)
        {
            return Result.Fail(WorkflowErrors.InvalidActorForAutomaticTransition(
                context.InstanceId,
                context.Actor));
        }

        // Chain depth check (defense in depth - also checked in pipeline)
        const int maxChainDepth = 50;
        if (context.ChainDepth > maxChainDepth)
        {
            return Result.Fail(WorkflowErrors.TransitionChainDepthExceeded(
                context.ChainDepth,
                maxChainDepth,
                context.TransitionKey));
        }

        return Result.Ok();
    }

    /// <summary>
    /// Validates scheduled trigger execution rules.
    /// Scheduled transitions should be initiated by System actors.
    /// Sets SkipImmediateExecution flag if not a re-entry call (initial schedule request).
    /// </summary>
    private static Result ValidateScheduledTrigger(TransitionExecutionContext context)
    {
        // Actor should be System for scheduled transitions
        if (context.Actor != ExecutionActor.System)
        {
            return Result.Fail(WorkflowErrors.InvalidActorForScheduledTransition(
                context.InstanceId,
                context.Actor));
        }

        // Skip immediate execution if not re-entry (first call = schedule only)
        // When background job executes (IsReentry=true), it will execute immediately
        if (!context.IsReentry)
        {
            context.SkipImmediateExecution = true;
        }

        return Result.Ok();
    }

    /// <summary>
    /// Validates event trigger execution rules.
    /// Event transitions should be initiated by User actors (external event sources).
    /// </summary>
    private static Result ValidateEventTrigger(TransitionExecutionContext context)
    {
        // Actor should be User for event transitions (external webhooks/events)
        if (context.Actor != ExecutionActor.User)
        {
            return Result.Fail(WorkflowErrors.InvalidActorForEventTransition(
                context.InstanceId,
                context.Actor));
        }

        return Result.Ok();
    }
}