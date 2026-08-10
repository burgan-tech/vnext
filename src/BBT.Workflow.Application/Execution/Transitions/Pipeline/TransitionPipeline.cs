using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Continuations;
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
    private readonly IInstanceBusyManager _busyMarker;
    private readonly ITransitionContextFactory _contextFactory;
    private readonly IInstanceRepository _instanceRepository;
    private readonly IUnitOfWorkManager _uowManager;
    private readonly ITransitionValidationService _validationService;
    private readonly IPipelineProfileResolver _profileResolver;
    private readonly IStateNotificationScheduler _stateNotificationScheduler;
    private readonly WorkflowExecutionOptions _executionOptions;
    private readonly ILogger<TransitionPipeline> _logger;

    /// <summary>
    /// Maximum allowed chain depth for automatic transitions.
    /// Prevents infinite loops in recursive transition chains.
    /// </summary>
    private const int MaxChainDepth = 50;

    /// <summary>
    /// Pipeline error codes that represent a caller-actionable condition rather than an internal
    /// fault. When a step fails with one of these, the instance is still faulted but the failure is
    /// propagated to the caller so it maps to the intended HTTP status (e.g. 409) instead of being
    /// swallowed into the default "200 + Status=F" outcome. Extend deliberately — each entry must
    /// have a matching non-500 mapping in <c>AddExceptionHandling</c>.
    /// </summary>
    private static readonly HashSet<string> ClientFacingErrorCodes = new(StringComparer.Ordinal)
    {
        WorkflowErrorCodes.ResourceLockConflict,
    };

    private static bool IsClientFacingError(Error error)
        => error.Code is not null && ClientFacingErrorCodes.Contains(error.Code);

    /// <summary>
    /// Initializes a new instance of the TransitionPipeline.
    /// </summary>
    public TransitionPipeline(
        TransitionExecutor executor,
        ContinuationDispatcher continuationDispatcher,
        ITransitionLockScopeFactory lockScopeFactory,
        IReservedTransitionResolver reservedTransitionResolver,
        IInstanceBusyManager busyMarker,
        ITransitionContextFactory contextFactory,
        IInstanceRepository instanceRepository,
        IUnitOfWorkManager uowManager,
        ITransitionValidationService validationService,
        IPipelineProfileResolver profileResolver,
        IStateNotificationScheduler stateNotificationScheduler,
        Microsoft.Extensions.Options.IOptions<WorkflowExecutionOptions> executionOptions,
        ILogger<TransitionPipeline> logger)
    {
        _executor = executor;
        _continuationDispatcher = continuationDispatcher;
        _lockScopeFactory = lockScopeFactory;
        _reservedTransitionResolver = reservedTransitionResolver;
        _busyMarker = busyMarker;
        _contextFactory = contextFactory;
        _instanceRepository = instanceRepository;
        _uowManager = uowManager;
        _validationService = validationService;
        _profileResolver = profileResolver;
        _stateNotificationScheduler = stateNotificationScheduler;
        _executionOptions = executionOptions.Value;
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

        // 2) Reserved transitions acquire their own type-specific lock independently of the main
        //    flow lock. Post-commit work begins only after this pipeline returns and lock
        //    registration ends, so this path does not depend on nested callback reentrancy.
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

            // Mark the reserved key as held only for this pipeline invocation. AsyncLocal-scoped:
            // it expires when this method returns, before runner-owned post-commit work begins.
            ChainLockRegistry.Register(reservedKey);

            // A durable S8 checkpoint always belongs to the interrupted MAIN transition.
            // Reserved transitions (cancel/exit/update-data/timeout/long-poll ack) must never
            // resume from a foreign checkpoint — clear it in-memory so the executor builds
            // their plan from the top. Subflow / long-poll resumes are unaffected: they set an
            // explicit ResumeFrom directive, which takes precedence over the instance checkpoint.
            context.Instance.ClearResumePoint();

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

        // Mark the chain lock key as held for work within this pipeline invocation. AsyncLocal-
        // scoped registration expires when this method returns; runner-owned post-commit work
        // starts only after that handoff, once the lock registration has ended.
        ChainLockRegistry.Register(context.LockKey);

        // 4) Run the chain. SetBusyStep uses this already-loaded aggregate and persists the
        // reservation as the first mutating lifecycle step; avoid a second repository reload.
        return await RunChainAsync(context, lockScope, cancellationToken);
    }

    /// <summary>
    /// Runs the full transition chain (first + auto-chained) under a single lock scope.
    /// The lease is sized to cover the whole chain budget; between-hop TTL extension is
    /// opt-in (<see cref="WorkflowExecutionOptions.EnableLockLeaseExtension"/>) and only
    /// valid with providers that support atomic extension.
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

                // The instance is now faulted (F) regardless. For caller-actionable errors
                // (e.g. ResourceLockConflict) propagate the failure so the HTTP layer returns the
                // mapped status (409) instead of swallowing it into "200 + Status=F". All other
                // pipeline faults keep the existing 200 + Status=F behavior.
                if (IsClientFacingError(pipelineResult.Error))
                    return Result<TransitionExecutionContext>.Fail(pipelineResult.Error);

                return Result<TransitionExecutionContext>.Ok(context);
            }

            // A post-commit job marks the handoff boundary. The runner owns executing this
            // remote work after the originating UoW has committed and this lock scope ends.
            // Do not consume the jobs: it returns the intact directives to the runner.
            if (context.Directives.PostCommitJobs.Count > 0)
            {
                // Enqueued continuations must still be persisted in the originating UoW before
                // we return the barrier. Inline continuations remain in directives so the runner
                // can orchestrate them after the handoff.
                if (context.EnqueueContinuations)
                {
                    var enqueueResult = await _continuationDispatcher.DispatchAsync(
                        ContinuationMode.Enqueue, context, cancellationToken);
                    if (!enqueueResult.IsSuccess)
                        return Result<TransitionExecutionContext>.Fail(enqueueResult.Error);
                }

                return Result<TransitionExecutionContext>.Ok(context);
            }

            // Realize the continuation. Inline = in-process auto-chain (sync); Enqueue =
            // transition-per-job (the strategy persists the next transition to the outbox and
            // returns null, ending the in-process loop — a separate job resumes the chain).
            var continuationMode = context.EnqueueContinuations
                ? ContinuationMode.Enqueue
                : ContinuationMode.Inline;

            // Capture before dispatch: the Enqueue strategy CONSUMES NextTransition, so reading it
            // afterwards is unreliable. The chain has truly settled only when there was no next
            // transition AND the dispatcher produced no in-process continuation.
            var hadNextTransition = context.Directives.NextTransition is not null;

            var continuationResult = await _continuationDispatcher.DispatchAsync(
                continuationMode, context, cancellationToken);
            if (!continuationResult.IsSuccess)
                return Result<TransitionExecutionContext>.Fail(continuationResult.Error);

            if (continuationResult.Value is null)
            {
                // No further in-process work (chain complete or continuation enqueued) —
                // apply deferred status and release chain ownership if requested
                // (inside lock, no re-acquire needed).
                await TransitionSettlement.ApplyAsync(
                    context,
                    context.Directives.ConsumeResolvedStatus(),
                    context.Directives.ConsumeEndChain(),
                    scheduleNotification: !hadNextTransition,
                    _instanceRepository,
                    _stateNotificationScheduler,
                    _logger,
                    cancellationToken);

                return Result<TransitionExecutionContext>.Ok(context);
            }

            // Refresh the lock TTL before starting the next chained transition — only when the
            // provider supports atomic extension (opt-in). The default Dapr lock provider cannot
            // extend a held lock, so the lease is sized upfront to cover the full chain budget
            // (WorkflowExecutionOptions.GetEffectiveLockLeaseSeconds) instead. When extension IS
            // enabled, a failed extension means exclusivity may already be lost — stop the chain
            // rather than continue without a held lease; job re-delivery / the chain reaper
            // recover the instance.
            if (lockScope is not null && _executionOptions.EnableLockLeaseExtension)
            {
                var extended = await lockScope.ExtendAsync(cancellationToken);
                if (!extended)
                {
                    _logger.TransitionLockExtendFailed(context.InstanceId.ToString(), context.TransitionKey);
                    return Result<TransitionExecutionContext>.Fail(
                        WorkflowErrors.InstanceLockConflict(context.InstanceId));
                }
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

        if (workflowContext.ExpectedRevision is { } expectedRevision
            && context.Instance.Revision != expectedRevision)
        {
            return Result<TransitionExecutionContext>.Fail(
                WorkflowErrors.InstanceRevisionConflict(
                    context.InstanceId,
                    expectedRevision,
                    context.Instance.Revision));
        }

        // Admission already validated the immutable request payload. The execution reload must
        // still re-check state/actor policy, but repeating schema resolution here adds no safety.
        var validationResult = workflowContext.TransitionSchemaValidated
            ? await _validationService.ValidatePolicyAsync(context, cancellationToken)
            : await _validationService.ValidateAsync(context, cancellationToken);
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
    /// Uses a RequiresNew UoW scope so that any dirty state left on the current
    /// DbContext by the failed pipeline step does not block SaveChanges.
    /// </summary>
    private async Task MarkInstanceFaultedAsync(
        TransitionExecutionContext context,
        Error error,
        CancellationToken cancellationToken)
    {
        _logger.InstanceFaultedDueToPipelineError(context.InstanceId, error.Code, error.Message);

        await using var faultUow = _uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });

        // Reload in the new scope so we operate on a clean, tracked entity.
        var instance = await _instanceRepository.FindAsync(context.InstanceId, true, cancellationToken)
                       ?? context.Instance;

        if (!instance.HasActiveIncident)
        {
            var incident = InstanceIncidentFactory.Create(
                state: instance.GetCurrentState,
                transition: context.TransitionKey,
                taskKey: null,
                message: error.Message ?? "Unhandled pipeline error",
                errorCode: error.Code ?? "Pipeline:Unhandled",
                errorLayer: "Pipeline",
                traceId: context.TraceId);

            instance.AddIncident(incident);
        }

        instance.Fault(context.Domain, context.CallerMode == ExecMode.Sync);
        await _instanceRepository.UpdateAsync(instance, true, cancellationToken);
        await faultUow.CommitAsync(cancellationToken);

        _logger.InstanceFaultedSuccessfully(context.InstanceId);
    }

}
