using System.Diagnostics;
using BBT.Aether.Aspects;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Execution.Validation;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Pipeline;

/// <summary>
/// Orchestrates the execution of transition lifecycle steps in a deterministic order.
/// Acquires a single request-scoped lock that covers the entire auto-chain —
/// no gap between chained transitions.
/// Reserved transitions bypass the outer lock unless they require an own scope (subflow resume).
/// </summary>
public class TransitionPipeline
{
    private readonly IReadOnlyList<ITransitionStep> _steps;
    private readonly ITransitionLockScopeFactory _lockScopeFactory;
    private readonly IReservedTransitionResolver _reservedTransitionResolver;
    private readonly IInstanceBusyMarker _busyMarker;
    private readonly ITransitionContextFactory _contextFactory;
    private readonly IPostCommitExecutor _postCommitExecutor;
    private readonly IInstanceRepository _instanceRepository;
    private readonly ITransitionValidationService _validationService;
    private readonly IPipelineProfileResolver _profileResolver;
    private readonly ILogger<TransitionPipeline> _logger;

    /// <summary>
    /// Maximum allowed chain depth for automatic transitions.
    /// Prevents infinite loops in recursive transition chains.
    /// </summary>
    private const int MaxChainDepth = 50;

    /// <summary>
    /// Initializes a new instance of the TransitionPipeline.
    /// </summary>
    public TransitionPipeline(
        IEnumerable<ITransitionStep> steps,
        ITransitionLockScopeFactory lockScopeFactory,
        IReservedTransitionResolver reservedTransitionResolver,
        IInstanceBusyMarker busyMarker,
        ITransitionContextFactory contextFactory,
        IPostCommitExecutor postCommitExecutor,
        IInstanceRepository instanceRepository,
        ITransitionValidationService validationService,
        IPipelineProfileResolver profileResolver,
        ILogger<TransitionPipeline> logger)
    {
        _steps = steps.OrderBy(s => s.Order).ToList();
        _lockScopeFactory = lockScopeFactory;
        _reservedTransitionResolver = reservedTransitionResolver;
        _busyMarker = busyMarker;
        _contextFactory = contextFactory;
        _postCommitExecutor = postCommitExecutor;
        _instanceRepository = instanceRepository;
        _validationService = validationService;
        _profileResolver = profileResolver;
        _logger = logger;
    }

    /// <summary>
    /// Executes the transition pipeline with a single request-scoped lock.
    /// The lock is acquired once before the first transition and held for the
    /// entire auto-chain, except reserved paths that bypass or use an independent scope.
    /// </summary>
    public async Task<Result<TransitionExecutionContext>> RunAsync(
        WorkflowExecutionContext workflowContext,
        CancellationToken cancellationToken)
    {
        // 1) Build the first context to decide reserved vs normal path
        var contextResult = await CreateAndValidateContextAsync(workflowContext, cancellationToken);
        if (!contextResult.IsSuccess)
            return Result<TransitionExecutionContext>.Fail(contextResult.Error);

        var context = contextResult.Value!;

        if (context.SkipImmediateExecution)
            return Result<TransitionExecutionContext>.Ok(context);

        // 2) Reserved transitions — each acquires its own type-specific lock that is independent
        //    of the main flow lock, so they can run even while a normal transition holds L1
        //    (e.g., sync subflow resume triggered inside the parent's post-commit phase).
        if (_reservedTransitionResolver.IsReserved(context))
        {
            var reservedKey = _reservedTransitionResolver.GetOwnLockKey(context);
            await using var ownLock = await _lockScopeFactory.AcquireAsync(reservedKey, cancellationToken);
            if (!ownLock.IsAcquired)
            {
                // Lock failure is already logged by TransitionLockScopeFactory with the full key;
                // avoid a duplicate log line for the same acquisition.
                return Result<TransitionExecutionContext>.Fail(
                    WorkflowErrors.InstanceLockConflict(context.InstanceId));
            }

            // SubFlow Resume resumes an already-Busy instance; confirm the busy mark.
            if (context.Directives.IsSubFlowResume)
                await _busyMarker.MarkBusyAsync(context.InstanceId, cancellationToken);

            return await RunChainAsync(context, workflowContext, ownLock, cancellationToken);
        }

        // 3) Normal transitions — acquire single lock for the entire chain
        await using var lockScope = await _lockScopeFactory.AcquireAsync(context.LockKey, cancellationToken);

        if (!lockScope.IsAcquired)
        {
            // Lock failure is already logged by TransitionLockScopeFactory with the full key;
            // avoid a duplicate log line for the same acquisition.
            return Result<TransitionExecutionContext>.Fail(
                WorkflowErrors.InstanceLockConflict(context.InstanceId));
        }

        // 4) Mark instance Busy immediately after lock acquisition
        await _busyMarker.MarkBusyAsync(context.InstanceId, cancellationToken);

        // 5) Run the entire chain under this lock scope
        return await RunChainAsync(context, workflowContext, lockScope, cancellationToken);
    }

    /// <summary>
    /// Runs the full transition chain (first + auto-chained) under a single lock scope.
    /// The lock is extended between chain iterations to prevent TTL expiry.
    /// </summary>
    private async Task<Result<TransitionExecutionContext>> RunChainAsync(
        TransitionExecutionContext initialContext,
        WorkflowExecutionContext initialWorkflowContext,
        ITransitionLockScope? lockScope,
        CancellationToken cancellationToken)
    {
        var context = initialContext;
        var currentWorkflowContext = initialWorkflowContext;

        while (true)
        {
            // Guard: Prevent infinite chain loops
            if (context.ChainDepth > MaxChainDepth)
            {
                _logger.TransitionChainDepthExceeded(context.ChainDepth, MaxChainDepth, context.TransitionKey);
                return Result<TransitionExecutionContext>.Fail(
                    WorkflowErrors.TransitionChainDepthExceeded(
                        context.ChainDepth, MaxChainDepth, context.TransitionKey));
            }

            // Execute pipeline steps for this transition
            var pipelineResult = await RunSingleTransitionAsync(context, cancellationToken);
            if (!pipelineResult.IsSuccess)
            {
                await MarkInstanceFaultedAsync(context, pipelineResult.Error, cancellationToken);
                return Result<TransitionExecutionContext>.Ok(context);
            }

            // Execute post-commit jobs (inside lock scope)
            var postCommitJobs = context.Directives.ConsumePostCommitJobs();
            if (postCommitJobs.Count > 0)
            {
                var postCommitResult = await _postCommitExecutor.ExecuteAsync(postCommitJobs, context, cancellationToken);
                if (!postCommitResult.IsSuccess)
                {
                    if (postCommitResult.FaultRequest is not null)
                    {
                        await MarkInstanceFaultedFromPostCommitAsync(context, postCommitResult.FaultRequest, cancellationToken);
                        return Result<TransitionExecutionContext>.Ok(context);
                    }

                    var error = postCommitResult.Error
                        ?? WorkflowErrors.ConfigInvalid(context.InstanceId, "Post-commit execution failed without error details");
                    return Result<TransitionExecutionContext>.Fail(error);
                }
            }

            // Check for next transition in the auto-chain
            var nextTransition = context.Directives.ConsumeNextTransition();
            if (nextTransition is null)
            {
                // Chain complete — apply deferred status (inside lock, no re-acquire needed)
                await ApplyResolvedStatusAsync(context, cancellationToken);
                return Result<TransitionExecutionContext>.Ok(context);
            }

            // Extend lock TTL before starting the next chained transition
            if (lockScope is not null)
            {
                await lockScope.ExtendAsync(cancellationToken);
            }

            // Build next context for the chained transition
            currentWorkflowContext = CreateNextWorkflowContext(context, nextTransition);
            var nextContextResult = await CreateAndValidateContextAsync(currentWorkflowContext, cancellationToken);
            if (!nextContextResult.IsSuccess)
                return Result<TransitionExecutionContext>.Fail(nextContextResult.Error);

            context = nextContextResult.Value!;
        }
    }

    /// <summary>
    /// Creates and validates a transition context from a workflow context.
    /// Shared by the initial entry and auto-chain iterations.
    /// </summary>
    private async Task<Result<TransitionExecutionContext>> CreateAndValidateContextAsync(
        WorkflowExecutionContext workflowContext,
        CancellationToken cancellationToken)
    {
        var contextResult = await _contextFactory.CreateAsync(workflowContext, cancellationToken);
        if (!contextResult.IsSuccess)
            return Result<TransitionExecutionContext>.Fail(contextResult.Error);

        var context = contextResult.Value!;

        var validationResult = await _validationService.ValidateAsync(context, cancellationToken);
        if (!validationResult.IsSuccess)
            return Result<TransitionExecutionContext>.Fail(validationResult.Error);

        context.Profile = _profileResolver.Resolve(workflowContext);
        return Result<TransitionExecutionContext>.Ok(context);
    }

    /// <summary>
    /// Executes a single transition's pipeline steps.
    /// </summary>
    [Trace]
    private async Task<Result> RunSingleTransitionAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        EnrichTelemetry(context);

        var profile = context.Profile ?? PipelineExecutionProfile.ForManual();
        var state = CreateInitialState(context, profile);

        using (_logger.BeginScope(BuildLogScope(context)))
        {
            try
            {
                while (state.HasMoreSteps())
                {
                    if (context.SkipImmediateExecution)
                        return Result.Ok();

                    var stepResult = await ExecuteStepWithBoundaryAsync(
                        state.CurrentStep, context, cancellationToken);

                    if (!stepResult.IsSuccess)
                        return Result.Fail(stepResult.Error);

                    var flowControl = DetermineFlowControl(stepResult.Value!, state.CurrentStep, context, state);

                    if (flowControl.ShouldStop)
                        break;

                    if (flowControl.ShouldReplan)
                    {
                        state = CreateInitialState(context, profile);
                        continue;
                    }

                    state = state.MoveNext();
                }

                return Result.Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in pipeline execution for workflow {WorkflowKey}",
                    context.Workflow.Key);
                return Result.Fail(Error.Failure("PipelineException", ex.Message));
            }
        }
    }

    /// <summary>
    /// Builds a log scope dictionary for the current transition.
    /// </summary>
    private static Dictionary<string, object> BuildLogScope(TransitionExecutionContext context)
    {
        var props = new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.Domain]    = context.Domain,
            [TelemetryConstants.TagNames.Flow]    = context.Workflow.Key,
            [TelemetryConstants.TagNames.FlowVersion]    = context.Workflow.Version,
            [TelemetryConstants.TagNames.InstanceId]    = context.InstanceId,
            [TelemetryConstants.TagNames.InstanceKey]    = context.Instance.Key ?? "N/A",
            [TelemetryConstants.TagNames.StateFrom]     = context.Transition?.From ?? context.Instance.GetCurrentState,
            [TelemetryConstants.TagNames.StateTo]       = context.Transition?.Target ?? "N/A",
            [TelemetryConstants.TagNames.TransitionKey] = context.TransitionKey,
            [TelemetryConstants.TagNames.TriggerType]    = context.Transition?.TriggerType.ToString() ?? "N/A",
            ["ChainDepth"] = context.ChainDepth,
            ["PipelineProfile"] = context.Profile?.Name ?? "unknown",
        };
        if (context.Headers.TryGetValue(TelemetryConstants.HeaderNames.ParentInstanceId, out var raw)
            && Guid.TryParse(raw, out var parentId))
        {
            props[TelemetryConstants.TagNames.ParentInstanceId] = parentId;
        }
        return props;
    }

    /// <summary>
    /// Executes a pipeline step with exception boundary.
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

        activity.SetTag("vnext.chain.depth", context.ChainDepth);
        activity.SetTag("vnext.pipeline.profile", context.Profile?.Name ?? "unknown");
        activity.SetTag("vnext.chain.id", context.ExecutionChainId);
        
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
            CallerMode = currentContext.CallerMode,
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
            IsReentry = true,
            IsErrorBoundaryTransition = string.Equals(nextTransition.Reason, TransitionRequestReasons.ErrorBoundary, StringComparison.OrdinalIgnoreCase)
        };
    }

    /// <summary>
    /// Creates initial pipeline state with execution plan.
    /// </summary>
    private PipelineState CreateInitialState(TransitionExecutionContext context, PipelineExecutionProfile profile)
    {
        var plan = BuildExecutionPlan(context, profile);
        _logger.PipelineExecutingWithProfile(profile.Name, plan.Count, context.ChainDepth);
        var excludedCount = _steps.Count - plan.Count;
        if (excludedCount > 0)
            _logger.ProfileExcludedSteps(profile.Name, excludedCount, context.TransitionKey);

        return new PipelineState(plan, 0);
    }

    /// <summary>
    /// Builds an execution plan by filtering and ordering steps based on context directives.
    /// </summary>
    private IReadOnlyList<ITransitionStep> BuildExecutionPlan(
        TransitionExecutionContext context,
        PipelineExecutionProfile profile)
    {
        var ordered = _steps
            .Where(s => !profile.ExcludedStepOrders.Contains(s.Order))
            .ToList();

        var startOrder = context.Directives.ConsumeResumeFrom();
        if (startOrder.HasValue)
            ordered = ordered.Where(s => s.Order >= startOrder.Value).ToList();

        if (context.Directives.TerminalReached)
        {
            var maxOrder = LifecycleOrder.Finalize;
            ordered = ordered.Where(s => s.Order <= maxOrder).ToList();
        }

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
    /// Determines flow control based on step outcome.
    /// </summary>
    private static FlowControl DetermineFlowControl(
        StepOutcome outcome,
        ITransitionStep step,
        TransitionExecutionContext context,
        PipelineState state)
    {
        outcome.MutateDirectives?.Invoke(context.Directives);

        if (outcome.StopPipeline)
            return FlowControl.Stop();

        if (outcome.SkipToOrder is { } skipTo)
        {
            context.Directives.RequestResumeFrom(skipTo);
            return FlowControl.Replan();
        }

        if (NeedsReplan(state.Plan, context.Directives))
        {
            context.Directives.RequestResumeFrom(step.Order + 1);
            return FlowControl.Replan();
        }

        return FlowControl.Continue();
    }

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
    /// Marks the workflow instance as faulted. Already within lock scope —
    /// no re-acquisition needed.
    /// </summary>
    private async Task MarkInstanceFaultedAsync(
        TransitionExecutionContext context,
        Error error,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "Marking instance {InstanceId} as faulted due to unhandled pipeline error: {ErrorCode} - {ErrorMessage}",
            context.InstanceId, error.Code, error.Message);

        if (!context.Instance.HasActiveIncident)
        {
            var incident = InstanceIncidentFactory.Create(
                state: context.Instance.GetCurrentState,
                transition: context.TransitionKey,
                taskKey: null,
                message: error.Message ?? "Unhandled pipeline error",
                errorCode: error.Code ?? "Pipeline:Unhandled",
                errorLayer: "Pipeline",
                traceId: context.TraceId);

            context.Instance.AddIncident(incident);
        }

        context.Instance.Fault(context.Domain);
        context.ExtractAndDeferInstanceEvents();
        await _instanceRepository.UpdateAsync(context.Instance, true, cancellationToken);

        _logger.LogInformation(
            "Instance {InstanceId} marked as faulted successfully. Client will receive Status = 'F'",
            context.InstanceId);
    }

    /// <summary>
    /// Marks the workflow instance as faulted due to a post-commit failure.
    /// Already within lock scope — no re-acquisition needed.
    /// Uses the context's instance directly since we never released the lock.
    /// </summary>
    private async Task MarkInstanceFaultedFromPostCommitAsync(
        TransitionExecutionContext context,
        PostCommitFaultRequest faultRequest,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "Marking instance {InstanceId} as faulted due to post-commit failure: {ErrorCode} - {ErrorMessage}",
            context.InstanceId, faultRequest.ErrorCode, faultRequest.ErrorMessage);

        context.Instance.Fault(context.Domain);
        context.ExtractAndDeferInstanceEvents();
        await _instanceRepository.UpdateAsync(context.Instance, true, cancellationToken);

        _logger.LogInformation(
            "Instance {InstanceId} marked as faulted successfully",
            context.InstanceId);
    }

    /// <summary>
    /// Applies the deferred resolved status to the instance.
    /// Already within lock scope — no re-acquisition needed.
    /// </summary>
    private async Task ApplyResolvedStatusAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var resolvedStatus = context.Directives.ConsumeResolvedStatus();
        if (resolvedStatus is null)
            return;

        if (context.Instance.IsCompleted)
            return;

        if (context.Target?.SubType == StateSubType.Busy)
            return;

        if (context.Instance.ActiveCorrelations.Any(c =>
                c.SubFlowType.Equals(SubFlowType.SubFlow) && !c.IsCompleted))
            return;

        context.Instance.Active();
        await _instanceRepository.UpdateAsync(context.Instance, true, cancellationToken);

        _logger.LogDebug(
            "Instance {InstanceId} resolved to Active after chain completion",
            context.InstanceId);
    }

    private readonly record struct PipelineState(IReadOnlyList<ITransitionStep> Plan, int Index)
    {
        public ITransitionStep CurrentStep => Plan[Index];
        public bool HasMoreSteps() => Index < Plan.Count;
        public PipelineState MoveNext() => this with { Index = Index + 1 };
    }

    private readonly record struct FlowControl(bool ShouldStop, bool ShouldReplan)
    {
        public static FlowControl Stop() => new(ShouldStop: true, ShouldReplan: false);
        public static FlowControl Replan() => new(ShouldStop: false, ShouldReplan: true);
        public static FlowControl Continue() => new(ShouldStop: false, ShouldReplan: false);
    }
}
