using System.Diagnostics;
using BBT.Aether.Aspects;
using BBT.Aether.DistributedLock;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Execution.Validation;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Pipeline;

/// <summary>
/// Orchestrates the execution of transition lifecycle steps in a deterministic order.
/// Manages distributed locks and sync dispatch chain for automatic transitions.
/// Each step in the pipeline performs a specific operation during the transition.
/// Uses Result pattern for exception-free error handling.
/// Error boundary handling is delegated to TaskCoordinator at task level.
/// </summary>
public class TransitionPipeline
{
    private readonly IReadOnlyList<ITransitionStep> _steps;
    private readonly IDistributedLockService _distributedLockService;
    private readonly ITransitionContextFactory _contextFactory;
    private readonly IPostCommitExecutor _postCommitExecutor;
    private readonly IInstanceRepository _instanceRepository;
    private readonly ITransitionValidationService _validationService;
    private readonly ILogger<TransitionPipeline> _logger;

    /// <summary>
    /// Default lock lease duration in seconds.
    /// </summary>
    private const int DefaultLockLeaseSeconds = 60;

    /// <summary>
    /// Maximum allowed chain depth for automatic transitions.
    /// Prevents infinite loops in recursive transition chains.
    /// </summary>
    private const int MaxChainDepth = 50;

    /// <summary>
    /// Initializes a new instance of the TransitionPipeline.
    /// </summary>
    /// <param name="steps">The collection of pipeline steps to execute.</param>
    /// <param name="distributedLockService">Service for distributed locking.</param>
    /// <param name="contextFactory">Factory for creating transition contexts.</param>
    /// <param name="postCommitExecutor">Executor for post-commit jobs.</param>
    /// <param name="instanceRepository">Repository for instance operations.</param>
    /// <param name="validationService">Service for transition validation.</param>
    /// <param name="logger">Logger instance.</param>
    public TransitionPipeline(
        IEnumerable<ITransitionStep> steps,
        IDistributedLockService distributedLockService,
        ITransitionContextFactory contextFactory,
        IPostCommitExecutor postCommitExecutor,
        IInstanceRepository instanceRepository,
        ITransitionValidationService validationService,
        ILogger<TransitionPipeline> logger)
    {
        _steps = steps.OrderBy(s => s.Order).ToList();
        _distributedLockService = distributedLockService;
        _contextFactory = contextFactory;
        _postCommitExecutor = postCommitExecutor;
        _instanceRepository = instanceRepository;
        _validationService = validationService;
        _logger = logger;
    }

    /// <summary>
    /// Executes the transition pipeline with distributed locking and sync dispatch chain.
    /// Manages lock acquisition/release per transition and chains automatic transitions.
    /// </summary>
    /// <param name="workflowContext">The workflow execution context containing request details.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>A Result containing the final TransitionExecutionContext or an error.</returns>
    public async Task<Result<TransitionExecutionContext>> RunAsync(
        WorkflowExecutionContext workflowContext,
        CancellationToken cancellationToken)
    {
        var currentWorkflowContext = workflowContext;
        bool pipelineFaulted = false;

        // Sync dispatch loop: chains automatic transitions under the same trace
        while (true)
        {
            // Guard: Prevent infinite chain loops (defense in depth)
            var currentDepth = currentWorkflowContext.Execution?.ChainDepth ?? 0;
            if (currentDepth > MaxChainDepth)
            {
                _logger.TransitionChainDepthExceeded(
                    currentDepth,
                    MaxChainDepth,
                    currentWorkflowContext.TransitionKey);
                return Result<TransitionExecutionContext>.Fail(
                    WorkflowErrors.TransitionChainDepthExceeded(
                        currentDepth,
                        MaxChainDepth,
                        currentWorkflowContext.TransitionKey));
            }

            // 1) Create TransitionExecutionContext
            var contextResult = await _contextFactory.CreateAsync(currentWorkflowContext, cancellationToken);
            if (!contextResult.IsSuccess)
                return Result<TransitionExecutionContext>.Fail(contextResult.Error);

            var context = contextResult.Value!;

            // 1.5) VALIDATION GUARD - Validate trigger type and execution rules
            // This ensures all transitions (including auto-chained) are validated
            var triggerValidationResult = await _validationService.ValidateAsync(context, cancellationToken);
            if (!triggerValidationResult.IsSuccess)
                return Result<TransitionExecutionContext>.Fail(triggerValidationResult.Error);

            // Guard: Skip immediate execution requested (e.g., scheduled transitions)
            if (context.SkipImmediateExecution)
                return Result<TransitionExecutionContext>.Ok(context);

            // Post-commit jobs collected during pipeline execution
            IReadOnlyList<IPostCommitJob> postCommitJobs = [];

            // 2) Execute with distributed lock
            var lockAcquired = await _distributedLockService.ExecuteWithLockAsync(
                context.LockKey,
                async () =>
                {
                    // 3) Execute pipeline steps
                    var pipelineResult = await RunSingleTransitionAsync(context, cancellationToken);
                    if (!pipelineResult.IsSuccess)
                    {
                        // Mark instance as faulted instead of propagating error
                        // This allows returning OK response with Status = "F" to client
                        await MarkInstanceFaultedAsync(context, pipelineResult.Error, cancellationToken);
                        pipelineFaulted = true;
                        return;
                    }

                    // 4) Consume post-commit jobs before lock release
                    postCommitJobs = context.Directives.ConsumePostCommitJobs();
                },
                DefaultLockLeaseSeconds,
                cancellationToken);

            if (!lockAcquired)
            {
                _logger.InstanceLockFailed(context.InstanceId.ToString());
                return Result<TransitionExecutionContext>.Fail(
                    WorkflowErrors.InstanceLockConflict(context.InstanceId));
            }

            // Pipeline faulted: return success with faulted instance (client sees Status = "F")
            if (pipelineFaulted)
                return Result<TransitionExecutionContext>.Ok(context);

            // 5) Execute post-commit jobs (outside lock - avoids deadlocks for remote calls)
            if (postCommitJobs.Count > 0)
            {
                var postCommitResult = await _postCommitExecutor.ExecuteAsync(postCommitJobs, context, cancellationToken);
                if (!postCommitResult.IsSuccess)
                {
                    // System error with fault request: reacquire lock and mark instance as faulted
                    if (postCommitResult.FaultRequest is not null)
                    {
                        await MarkInstanceFaultedWithLockAsync(
                            context,
                            postCommitResult.FaultRequest,
                            cancellationToken);
                        
                        // Return OK with faulted instance - client sees Status = "F"
                        return Result<TransitionExecutionContext>.Ok(context);
                    }

                    // Client error (no fault request): return error to client without faulting instance
                    // context.ClientResponse already contains the error details set by the handler
                    var error = postCommitResult.Error ?? WorkflowErrors.ConfigInvalid(context.InstanceId, "Post-commit execution failed without error details");
                    return Result<TransitionExecutionContext>.Fail(error);
                }
            }

            // 6) Check for next transition in sync dispatch chain
            var nextTransition = context.Directives.ConsumeNextTransition();
            if (nextTransition is null)
                return Result<TransitionExecutionContext>.Ok(context);

            // 7) Create new WorkflowExecutionContext for next transition
            currentWorkflowContext = CreateNextWorkflowContext(context, nextTransition);
            pipelineFaulted = false; // Reset for next iteration
        }
    }

    /// <summary>
    /// Executes a single transition's pipeline steps.
    /// Includes global error boundary wrapper for unhandled exceptions.
    /// </summary>
    [Trace]
    private async Task<Result> RunSingleTransitionAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        EnrichTelemetry(context);

        var state = CreateInitialState(context);

        try
        {
            while (state.HasMoreSteps())
            {
                // Guard: Skip immediate execution requested
                if (context.SkipImmediateExecution)
                    return Result.Ok();

                // Execute current step with error boundary handling
                var stepResult = await ExecuteStepWithBoundaryAsync(
                    state.CurrentStep, context, cancellationToken);

                if (!stepResult.IsSuccess)
                {
                    // Check if boundary abort was requested (handled but no transition)
                    // Pipeline stops but instance is NOT marked as faulted
                    if (context.Directives.BoundaryAbortRequested)
                    {
                        _logger.LogInformation(
                            "Boundary abort requested for workflow {WorkflowKey}. Stopping pipeline without fault.",
                            context.Workflow.Key);
                        return Result.Ok();
                    }

                    // Unhandled error - this will cause fault
                    return Result.Fail(stepResult.Error);
                }

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
        catch (Exception ex)
        {
            // Unhandled exception - propagate as error
            _logger.LogError(ex, "Unhandled exception in pipeline execution for workflow {WorkflowKey}", 
                context.Workflow.Key);
            return Result.Fail(Error.Failure("PipelineException", ex.Message));
        }
    }

    /// <summary>
    /// Executes a pipeline step.
    /// Error boundary handling is now delegated to TaskCoordinator for task steps.
    /// </summary>
    private async Task<Result<StepOutcome>> ExecuteStepWithBoundaryAsync(
        ITransitionStep step,
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            return await step.ExecuteAsync(context, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in step {StepName}", step.Name);
            return Result<StepOutcome>.Fail(Error.Failure(ex.GetType().Name, ex.Message));
        }
    }

    private static void EnrichTelemetry(TransitionExecutionContext context)
    {
        var activity = Activity.Current;
        if (activity is null) return;
        
        activity.SetTag(TelemetryConstants.TagNames.Flow, context.Workflow.Key);
        activity.SetTag(TelemetryConstants.TagNames.FlowVersion, context.Workflow.Version);
        activity.SetTag(TelemetryConstants.TagNames.InstanceId, context.InstanceId.ToString());
        activity.SetTag(TelemetryConstants.TagNames.TransitionKey, context.TransitionKey);
        if (context.Transition != null)
        {
            activity.SetTag(TelemetryConstants.TagNames.TriggerType, context.Transition.TriggerType.ToString());
        }
        
        activity.SetBaggage(TelemetryConstants.TagNames.Flow, context.Workflow.Key);
        activity.SetBaggage(TelemetryConstants.TagNames.FlowVersion, context.Workflow.Version);
        activity.SetBaggage(TelemetryConstants.TagNames.InstanceId, context.InstanceId.ToString());
        
        activity.SetDisplayName($"transition/{context.TransitionKey}");
    }

    /// <summary>
    /// Creates a new WorkflowExecutionContext for the next transition in the chain.
    /// </summary>
    private static WorkflowExecutionContext CreateNextWorkflowContext(
        TransitionExecutionContext currentContext,
        NextTransitionRequest nextTransition)
    {
        return new WorkflowExecutionContext
        {
            Domain = currentContext.Domain,
            InstanceId = currentContext.InstanceId.ToString(),
            WorkflowKey = currentContext.WorkflowKey,
            WorkflowVersion = currentContext.Workflow.Version,
            TransitionKey = nextTransition.TransitionKey,
            TriggerType = TriggerType.Automatic,
            Mode = ExecMode.Sync,
            Actor = Shared.ExecutionActor.System,
            CorrelationId = currentContext.CorrelationId,
            CausationId = currentContext.ExecutionChainId,
            RequestedAt = DateTimeOffset.UtcNow,
            Headers = currentContext.Headers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            Execution = new ExecutionInfo
            {
                ExecutionChainId = currentContext.ExecutionChainId,
                ChainDepth = currentContext.ChainDepth + 1,
                ResumeFrom = null
            },
            IsReentry = true
        };
    }

    /// <summary>
    /// Creates initial pipeline state with execution plan.
    /// </summary>
    private PipelineState CreateInitialState(TransitionExecutionContext context)
        => new(BuildExecutionPlan(context), 0);

    /// <summary>
    /// Builds an execution plan by filtering and ordering steps based on context directives.
    /// Handles resume points, epilogue modes, and terminal states.
    /// </summary>
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

    /// <summary>
    /// Executes a single pipeline step.
    /// Delegates to step implementation.
    /// </summary>
    private static Task<Result<StepOutcome>> ExecuteStepAsync(
        ITransitionStep step,
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
        => step.ExecuteAsync(context, cancellationToken);

    /// <summary>
    /// Determines flow control based on step outcome.
    /// Applies directive mutations and returns appropriate flow control decision.
    /// Sync method - no async operations needed.
    /// </summary>
    private static FlowControl DetermineFlowControl(
        StepOutcome outcome,
        ITransitionStep step,
        TransitionExecutionContext context,
        PipelineState state)
    {
        // Apply directive mutations from outcome
        outcome.MutateDirectives?.Invoke(context.Directives);

        // 1) Stop pipeline?
        if (outcome.StopPipeline)
            return FlowControl.Stop();

        // 2) Skip to specific order? (e.g., restart from CreateTransition after inline auto)
        if (outcome.SkipToOrder is { } skipTo)
        {
            context.Directives.RequestResumeFrom(skipTo);
            return FlowControl.Replan();
        }

        // 3) Directives changed requiring replan?
        if (NeedsReplan(state.Plan, context.Directives))
        {
            context.Directives.RequestResumeFrom(step.Order + 1);
            return FlowControl.Replan();
        }

        // Continue to next step
        return FlowControl.Continue();
    }

    /// <summary>
    /// Determines if the execution plan needs to be rebuilt.
    /// Checks for terminal state, epilogue mode changes, and resume requests.
    /// </summary>
    private static bool NeedsReplan(IReadOnlyList<ITransitionStep> currentPlan, PipelineDirectives d)
    {
        if (d.TerminalReached)
            return true;

        if (d.Epilogue == EpilogueMode.Skip &&
            currentPlan.Any(s => s.Order == LifecycleOrder.Schedule || s.Order == LifecycleOrder.Auto))
            return true;

        if (d.ResumeFromOrder is not null)
            return true;

        return false;
    }

    /// <summary>
    /// Marks the workflow instance as faulted within an existing lock scope.
    /// Called when pipeline execution fails after all error boundary actions are exhausted.
    /// Client will receive OK response with Status = "F" instead of an exception.
    /// </summary>
    /// <param name="context">The transition execution context.</param>
    /// <param name="error">The error that caused the fault.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task MarkInstanceFaultedAsync(
        TransitionExecutionContext context,
        Error error,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "Marking instance {InstanceId} as faulted due to unhandled pipeline error: {ErrorCode} - {ErrorMessage}",
            context.InstanceId,
            error.Code,
            error.Message);

        // Already within lock scope - update instance directly
        context.Instance.Fault(context.Domain);
        await _instanceRepository.UpdateAsync(context.Instance, true, cancellationToken);

        _logger.LogInformation(
            "Instance {InstanceId} marked as faulted successfully. Client will receive Status = 'F'",
            context.InstanceId);
    }

    /// <summary>
    /// Marks the workflow instance as faulted within a lock scope.
    /// Reacquires the distributed lock to ensure consistent state update.
    /// Used for post-commit failures that occur outside the main lock.
    /// </summary>
    /// <param name="context">The transition execution context.</param>
    /// <param name="faultRequest">The fault request containing error details.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task MarkInstanceFaultedWithLockAsync(
        TransitionExecutionContext context,
        PostCommitFaultRequest faultRequest,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "Marking instance {InstanceId} as faulted due to post-commit failure: {ErrorCode} - {ErrorMessage}",
            context.InstanceId,
            faultRequest.ErrorCode,
            faultRequest.ErrorMessage);

        var lockAcquired = await _distributedLockService.ExecuteWithLockAsync(
            context.LockKey,
            async () =>
            {
                var instanceResult = await _instanceRepository.GetResultAsync(
                    context.InstanceId.ToString(),
                    includeDetails: false,
                    cancellationToken);

                if (instanceResult is { IsSuccess: true, Value: not null })
                {
                    instanceResult.Value.Fault(context.Domain);
                    await _instanceRepository.UpdateAsync(instanceResult.Value, true, cancellationToken);

                    _logger.LogInformation(
                        "Instance {InstanceId} marked as faulted successfully",
                        context.InstanceId);
                }
                else
                {
                    _logger.LogError(
                        "Failed to load instance {InstanceId} for fault marking: {Error}",
                        context.InstanceId,
                        instanceResult.Error.Message);
                }
            },
            DefaultLockLeaseSeconds,
            cancellationToken);

        if (!lockAcquired)
        {
            _logger.LogError(
                "Failed to acquire lock for marking instance {InstanceId} as faulted",
                context.InstanceId);
        }
    }

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
}