using System.Diagnostics;
using BBT.Aether.Aspects;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Continuations;
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
    private readonly TransitionExecutor _executor;
    private readonly ContinuationDispatcher _continuationDispatcher;
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
        TransitionExecutor executor,
        ContinuationDispatcher continuationDispatcher,
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
        _executor = executor;
        _continuationDispatcher = continuationDispatcher;
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
            // (Long-poll acknowledge resume is intentionally NOT re-marked here: the paused
            // instance is already Busy, and a redundant resume that no-ops must not strand an
            // already-advanced instance in Busy.)
            if (context.Directives.IsSubFlowResume)
                await _busyMarker.MarkBusyAsync(context.InstanceId, cancellationToken);

            return await RunChainAsync(context, ownLock, cancellationToken);
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
        return await RunChainAsync(context, lockScope, cancellationToken);
    }

    /// <summary>
    /// Runs the full transition chain (first + auto-chained) under a single lock scope.
    /// The lock is extended between chain iterations to prevent TTL expiry.
    /// </summary>
    private async Task<Result<TransitionExecutionContext>> RunChainAsync(
        TransitionExecutionContext initialContext,
        ITransitionLockScope? lockScope,
        CancellationToken cancellationToken)
    {
        var context = initialContext;

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
            var pipelineResult = await _executor.ExecuteOneAsync(context, cancellationToken);
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

            // Realize the continuation. Inline = in-process auto-chain (sync); Enqueue =
            // transition-per-job (the strategy persists the next transition to the outbox and
            // returns null, ending the in-process loop — a separate job resumes the chain).
            var continuationMode = context.EnqueueContinuations
                ? ContinuationMode.Enqueue
                : ContinuationMode.Inline;

            var continuationResult = await _continuationDispatcher.DispatchAsync(
                continuationMode, context, cancellationToken);
            if (!continuationResult.IsSuccess)
                return Result<TransitionExecutionContext>.Fail(continuationResult.Error);

            if (continuationResult.Value is null)
            {
                // No further in-process work (chain complete or continuation enqueued) —
                // apply deferred status and release chain ownership if requested
                // (inside lock, no re-acquire needed).
                await ApplyResolvedStatusAsync(context, cancellationToken);
                await ApplyChainOwnershipAsync(context, cancellationToken);
                return Result<TransitionExecutionContext>.Ok(context);
            }

            // Extend lock TTL before starting the next chained transition
            if (lockScope is not null)
            {
                await lockScope.ExtendAsync(cancellationToken);
            }

            // Rebuild and validate the next chained transition context (single source of truth).
            var nextContextResult = await CreateAndValidateContextAsync(continuationResult.Value, cancellationToken);
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
        context.EnqueueContinuations = workflowContext.EnqueueContinuations;
        context.ChainToken = workflowContext.ChainToken;
        return Result<TransitionExecutionContext>.Ok(context);
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

    /// <summary>
    /// Releases the durable chain-ownership token when the pipeline has come to rest while the
    /// instance stays Busy (e.g. a Busy-subtype state). The auto-chain has finished, so the token
    /// must be cleared — otherwise the chain-token gate would reject legitimate foreign transitions
    /// and the ChainReaper would treat the resting instance as stuck. The instance status is left
    /// unchanged (still Busy). Already within lock scope — no re-acquisition needed.
    /// </summary>
    private async Task ApplyChainOwnershipAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Directives.ConsumeEndChain())
            return;

        if (context.Instance.IsCompleted || !context.Instance.ChainToken.HasValue)
            return;

        context.Instance.EndChain();
        await _instanceRepository.UpdateAsync(context.Instance, true, cancellationToken);

        _logger.LogDebug(
            "Instance {InstanceId} released chain ownership at rest (stays Busy)",
            context.InstanceId);
    }
}
