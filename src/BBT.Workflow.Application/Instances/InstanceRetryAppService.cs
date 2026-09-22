using System.Diagnostics;
using BBT.Aether.Application.Services;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Gateway;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;
using BBT.Workflow.Authorization;

namespace BBT.Workflow.Instances;

/// <summary>
/// Application service for retrying faulted workflow instances.
/// Supports two scenarios:
/// 1. Instance is Faulted → retry the instance's incomplete transition
/// 2. Instance has a Faulted SubFlow → retry the SubFlow (delegated to gateway)
/// </summary>
/// <remarks>
/// <para>
/// <b>The subflow-restart branch's one invariant</b> (see <see cref="RestartMissingSubflowChildAsync"/>):
/// once the parent has been re-armed Busy for the restart, no exit may leave it Busy or Active while
/// it carries neither an active incident nor a live child. Every exit from that point on either
/// succeeds (the child now exists) or re-faults the parent — including a thrown exception, which is
/// caught, not merely a returned <see cref="Result"/> failure.
/// </para>
/// </remarks>
public sealed class InstanceRetryAppService(
    IServiceProvider serviceProvider,
    IRuntimeInfoProvider runtimeInfoProvider,
    IInstanceRepository instanceRepository,
    IInstanceIncidentRepository instanceIncidentRepository,
    IInstanceTransitionRepository instanceTransitionRepository,
    IInstanceQueryGateway instanceQueryGateway,
    IInstanceRetryGateway instanceRetryGateway,
    IComponentCacheStore componentCacheStore,
    IWorkflowExecutionService workflowExecutionService,
    ICallerRoleResolver callerRoleResolver,
    IServiceScopeFactory scopeFactory,
    IInstanceStatusLock instanceStatusLock,
    ILogger<InstanceRetryAppService> logger)
    : ApplicationService(serviceProvider), IInstanceRetryAppService
{
    /// <summary>
    /// Bounded attempts to re-fault the parent after a failed subflow restart. The status lock
    /// itself already retries internally (<see cref="IInstanceStatusLock.AcquireAsync"/>), so this
    /// is a second, outer bound for the rare case where contention outlasts that — not a first line
    /// of defense.
    /// </summary>
    private const int MaxFaultCompensationAttempts = 3;

    /// <inheritdoc />
    public async Task<Result<RetryInstanceOutput>> RetryAsync(
        RetryInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        // Validate domain
        runtimeInfoProvider.Check(input.Domain);

        logger.InstanceRetryRequested(input.Instance, input.Workflow);

        // Step 1: Load instance with details (including correlations), NO-TRACKING.
        //
        // Read-only on purpose. Every write on this path is performed by an inner unit of work: the
        // unfault is a set-based CAS, and the retried transition runs in the pipeline's own scope.
        // A tracked load here made the ambient request unit of work hold a copy of the aggregate
        // that Unfault() had set to Active — and its commit, at the very end of the request, wrote
        // that stale Active over the Faulted the re-failed retry had just persisted. The instance
        // was then left Active, parked, with an open incident and no way to retry it again.
        var instanceResult = await instanceRepository.GetResultAsReadOnlyAsync(input.Instance, cancellationToken);
        if (!instanceResult.IsSuccess)
            return Result<RetryInstanceOutput>.Fail(instanceResult.Error);

        var instance = instanceResult.Value!;

        // Step 2: Scenario 1 - Instance itself is Faulted
        if (instance.Status.Equals(InstanceStatus.Faulted))
        {
            return await RetryFaultedInstanceAsync(instance, input, cancellationToken);
        }

        // Step 3: Scenario 2 - Check if instance has an active SubFlow
        var subflowCorrelation = instance.Subflow;
        if (subflowCorrelation == null)
        {
            // No SubFlow + Instance not Faulted → Error
            return Result<RetryInstanceOutput>.Fail(Error.Validation(
                WorkflowErrorCodes.InstanceNotFaulted,
                $"Instance {input.Instance} is not in faulted state. Current status: {instance.Status.Code}",
                input.Instance));
        }

        // Step 4: SubFlow exists - check if it's Faulted and retry it
        return await RetrySubFlowAsync(instance, subflowCorrelation, input, cancellationToken);
    }

    /// <summary>
    /// Retries a SubFlow that is in faulted state.
    /// Uses IInstanceQueryGateway to support cross-domain SubFlow queries.
    /// </summary>
    private async Task<Result<RetryInstanceOutput>> RetrySubFlowAsync(
        Instance parentInstance,
        InstanceCorrelation subflowCorrelation,
        RetryInstanceInput input,
        CancellationToken cancellationToken)
    {
        var callerRoles = await callerRoleResolver.ResolveRolesAsync(input.Headers, cancellationToken);
        if (!callerRoles.IsSuccess)
            return Result<RetryInstanceOutput>.Fail(callerRoles.Error);

        var probeResult = await ProbeSubflowStateAsync(subflowCorrelation, input, callerRoles.Value, cancellationToken);
        if (!probeResult.IsSuccess)
            return Result<RetryInstanceOutput>.Fail(probeResult.Error);

        // SubFlow not Faulted → Error (both instance and subflow are not faulted)
        if (probeResult.Value!.Status == null || !probeResult.Value.Status.Equals(InstanceStatus.Faulted))
        {
            return Result<RetryInstanceOutput>.Fail(Error.Validation(
                WorkflowErrorCodes.InstanceNotFaulted,
                $"Instance {input.Instance} is not in faulted state. Current status: {parentInstance.Status.Code}",
                input.Instance));
        }

        return await DelegateSubflowRetryToGatewayAsync(subflowCorrelation, input, cancellationToken);
    }

    /// <summary>
    /// Queries the SubFlow child's current state through <see cref="IInstanceQueryGateway"/>
    /// (supports cross-domain SubFlow queries). Shared by <see cref="RetrySubFlowAsync"/> (parent
    /// itself is not Faulted, subflow independently faulted) and the Faulted-parent path in
    /// <see cref="RetryFaultedInstanceAsync"/>, which must know whether the child exists at all
    /// BEFORE unfaulting the parent — see the class remarks on why order matters there.
    /// </summary>
    private async Task<Result<GetInstanceStateOutput>> ProbeSubflowStateAsync(
        InstanceCorrelation subflowCorrelation,
        RetryInstanceInput input,
        string[]? callerRoles,
        CancellationToken cancellationToken)
    {
        var subflowStateInput = new GetFunctionWithInstanceInput
        {
            Domain = subflowCorrelation.SubFlowDomain,
            Workflow = subflowCorrelation.SubFlowName,
            Version = subflowCorrelation.SubFlowVersion,
            Instance = subflowCorrelation.SubFlowInstanceId.ToString(),
            Headers = input.Headers,
            QueryParams = input.RouteValues,
            Role = ICallerRoleResolver.SingleRoleOf(callerRoles),
            Roles = callerRoles
        };

        // Retry descends rarely, but leaving one descent unspanned would cost more than it saves:
        // "this trace contains no Subflow.Descend" has to mean "nothing descended", or it means
        // nothing at all.
        using var descent = InstanceReadActivityHelper.StartDescendScope(
            runtimeInfoProvider,
            subflowCorrelation.SubFlowDomain,
            subflowCorrelation.SubFlowName,
            subflowCorrelation.SubFlowInstanceId.ToString(),
            subflowCorrelation.ParentInstanceId.ToString(),
            TelemetryConstants.DescentFunctions.State);

        var subflowStateResult = await instanceQueryGateway.GetFunctionWithStateAsync(
            subflowStateInput,
            cancellationToken);

        return subflowStateResult.Result;
    }

    /// <summary>
    /// Delegates the actual retry of an already-verified-Faulted SubFlow child to
    /// <see cref="IInstanceRetryGateway"/> (routes local or remote based on domain).
    /// </summary>
    private async Task<Result<RetryInstanceOutput>> DelegateSubflowRetryToGatewayAsync(
        InstanceCorrelation subflowCorrelation,
        RetryInstanceInput input,
        CancellationToken cancellationToken)
    {
        var subflowInput = new RetryInstanceInput
        {
            Domain = subflowCorrelation.SubFlowDomain,
            Workflow = subflowCorrelation.SubFlowName,
            Instance = subflowCorrelation.SubFlowInstanceId.ToString(),
            Sync = true,
            Data = input.Data,
            Headers = input.Headers,
            RouteValues = input.RouteValues
        };

        logger.InstanceRetryRequested(subflowCorrelation.SubFlowInstanceId.ToString(), subflowCorrelation.SubFlowName);

        return await instanceRetryGateway.RetryAsync(subflowInput, cancellationToken);
    }

    /// <summary>
    /// True when <paramref name="error"/> is the "instance not found" answer
    /// (<see cref="WorkflowErrors.InstanceNotFound"/>) a SubFlow-state probe returns for a
    /// correlation whose child was never created — as opposed to any other probe failure (network,
    /// cross-domain unavailability, ...) which must NOT be treated as "restart the child".
    /// Internal (not private), via <c>InternalsVisibleTo</c>, so a focused unit test can pin the
    /// classification directly instead of driving the whole app service through its heavy
    /// <c>ApplicationService</c>/unit-of-work infrastructure.
    /// </summary>
    internal static bool IsChildInstanceMissing(Error error) =>
        error.Prefix == ErrorCodes.Prefixes.NotFound &&
        error.Code == WorkflowErrorCodes.NotFoundInstanceData;

    /// <summary>
    /// Finds the transition that most recently moved the instance into <paramref name="parentState"/>
    /// — i.e. the transition <c>HandleSubFlowStep</c> ran under when it committed the correlation.
    /// <see cref="InstanceTransition.ToState"/> is populated only for a transition that actually
    /// completed (null for failed/incomplete ones), so filtering on it alone is enough; the caller
    /// passes transitions ordered ascending by <c>StartedAt</c> and this takes the LAST match in
    /// case the same state was re-entered more than once (e.g. an idempotent re-entry).
    /// </summary>
    internal static InstanceTransitionSlim? FindOriginatingTransition(
        IReadOnlyList<InstanceTransitionSlim> transitions,
        string parentState) =>
        transitions.LastOrDefault(t => t.ToState == parentState);

    /// <summary>
    /// Retries a faulted instance by re-executing its incomplete transition.
    /// If the instance has an active SubFlow correlation (fault propagated upward from SubFlow),
    /// unfaults the parent and cascades retry to the SubFlow instead.
    /// </summary>
    private async Task<Result<RetryInstanceOutput>> RetryFaultedInstanceAsync(
        Instance instance,
        RetryInstanceInput input,
        CancellationToken cancellationToken)
    {
        // Check if the fault originated from a SubFlow (correlation stays open on fault)
        if (instance.HasActiveSubFlow)
        {
            return await RetryFaultedInstanceWithActiveSubFlowAsync(instance, instance.Subflow!, input, cancellationToken);
        }

        // Standard path: Load workflow -> Find incomplete transition -> Unfault -> Execute
        return await LoadWorkflowAsync(input, instance, cancellationToken)
            .BindAsync(data => FindIncompleteTransitionAsync(data.Instance, data.Workflow, cancellationToken))
            .BindAsync(data => UnfaultAndPersistAsync(data, cancellationToken))
            .BindAsync(data => ExecuteRetryAsync(data, input, cancellationToken));
    }

    /// <summary>
    /// Retries a Faulted instance that carries an open SubFlow correlation.
    /// <para>
    /// The correlation is committed by <c>HandleSubFlowStep</c> in its own unit of work strictly
    /// BEFORE the post-commit <c>StartSubflowJob</c> that would actually create the child ever
    /// runs. So a correlation whose child was never created (the post-commit start failed before
    /// creating it) is indistinguishable, from the correlation alone, from a live one — the only
    /// way to tell them apart is to ask the child whether it exists.
    /// </para>
    /// <para>
    /// The child is therefore probed BEFORE the parent is unfaulted. Unfaulting first (the previous
    /// behaviour) left the parent unfaulted, with its incident resolved, the moment the probe came
    /// back 404 — worse than before the retry call: no longer Faulted (so a second retry is refused
    /// as "not faulted"), yet parked in the SubFlow state with nothing to show for it, its fault
    /// history erased. Probing first means a missing child never causes that side effect: the
    /// parent is touched only on a path that can actually proceed.
    /// </para>
    /// </summary>
    private async Task<Result<RetryInstanceOutput>> RetryFaultedInstanceWithActiveSubFlowAsync(
        Instance instance,
        InstanceCorrelation subflowCorrelation,
        RetryInstanceInput input,
        CancellationToken cancellationToken)
    {
        var callerRoles = await callerRoleResolver.ResolveRolesAsync(input.Headers, cancellationToken);
        if (!callerRoles.IsSuccess)
            return Result<RetryInstanceOutput>.Fail(callerRoles.Error);

        var probeResult = await ProbeSubflowStateAsync(subflowCorrelation, input, callerRoles.Value, cancellationToken);

        if (!probeResult.IsSuccess)
        {
            if (!IsChildInstanceMissing(probeResult.Error))
            {
                // Some other probe failure (network hiccup, cross-domain unavailability, ...): we
                // learned nothing that licenses touching the parent, so leave it exactly as found.
                return Result<RetryInstanceOutput>.Fail(probeResult.Error);
            }

            logger.SubFlowChildMissingOnRetry(instance.Id, subflowCorrelation.Id, subflowCorrelation.SubFlowInstanceId);
            return await RestartMissingSubflowChildAsync(instance, subflowCorrelation, input, cancellationToken);
        }

        // Child exists. SubFlow not Faulted → Error (neither the parent nor the subflow is faulted).
        if (probeResult.Value!.Status == null || !probeResult.Value.Status.Equals(InstanceStatus.Faulted))
        {
            return Result<RetryInstanceOutput>.Fail(Error.Validation(
                WorkflowErrorCodes.InstanceNotFaulted,
                $"Instance {input.Instance} is not in faulted state. Current status: {instance.Status.Code}",
                input.Instance));
        }

        // Child exists and is Faulted: unfault the parent — committed before the child retry runs,
        // exactly as before — then delegate exactly as the always-had-a-child path does.
        var unfaultResult = await UnfaultParentAndResolveIncidentsAsync(instance, cancellationToken);
        if (!unfaultResult.IsSuccess)
            return Result<RetryInstanceOutput>.Fail(unfaultResult.Error);

        return await DelegateSubflowRetryToGatewayAsync(subflowCorrelation, input, cancellationToken);
    }

    /// <summary>
    /// Unfaults the parent and resolves its incidents in one committed unit of work. Must be
    /// committed before anything that reloads the instance independently (the child retry gateway
    /// call, or the subflow-restart post-commit handler): the pipeline and <c>GetActiveAsync</c>
    /// both reject an instance whose status is still terminal, and Faulted is terminal.
    /// </summary>
    private async Task<Result> UnfaultParentAndResolveIncidentsAsync(Instance instance, CancellationToken cancellationToken)
    {
        await using var uow = UnitOfWorkManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

        // Incidents live in their own table and no load path includes them; the CAS below resolves
        // what is materialized here.
        await instanceRepository.LoadActiveIncidentsAsync(instance, cancellationToken);
        if (!await instanceRepository.TryUnfaultAsync(instance, cancellationToken))
        {
            return Result.Fail(Error.Validation(
                WorkflowErrorCodes.InstanceNotFaulted,
                $"Instance {instance.Id} is no longer in faulted state",
                instance.Id.ToString()));
        }

        var resolvedCount = await instanceIncidentRepository.ResolveAllAsync(
            instance.Id, DateTime.UtcNow, cancellationToken);
        await uow.CommitAsync(cancellationToken);

        logger.InstanceUnfaulted(instance.Id);
        if (resolvedCount > 0)
            logger.IncidentsResolved(instance.Id, resolvedCount);

        return Result.Ok();
    }

    /// <summary>
    /// Restarts the missing subflow start for an EXISTING correlation. The correlation already
    /// carries the pre-generated <c>SubFlowInstanceId</c> that <c>ISubflowStarter</c> passes
    /// with <c>StrictIdempotency</c>, so re-running the start for this same correlation creates the
    /// child the correlation always pointed at — it does not create a second correlation, and it
    /// does not re-run the parent's own transition (its tasks already ran once; re-running them
    /// would duplicate their side effects).
    /// <para>
    /// <b>Per-exit invariant.</b> Before the Busy re-arm below is committed, nothing has been
    /// mutated: any failure here (workflow load, transition lookup, the unfault/Busy CAS itself)
    /// returns cleanly with the instance still Faulted — still retryable, still carrying its
    /// incident. AFTER that commit, the parent is Busy with NO incident, so every exit from that
    /// point on is one of exactly two shapes: success (the child now exists), or a call to
    /// <see cref="FaultParentAfterFailedRestartAsync"/> that puts a fresh incident back and returns
    /// the ORIGINAL error. That includes a thrown exception — caught here, not just a returned
    /// <see cref="Result"/> failure — because an uncaught throw is indistinguishable, at the
    /// database, from silently abandoning the instance Busy forever.
    /// </para>
    /// </summary>
    private async Task<Result<RetryInstanceOutput>> RestartMissingSubflowChildAsync(
        Instance instance,
        InstanceCorrelation subflowCorrelation,
        RetryInstanceInput input,
        CancellationToken cancellationToken)
    {
        // Need the workflow definition both to build the restart's execution context (carried, so
        // CreateAsync does not re-resolve it) and, if the restart itself fails, to fault the parent
        // through the same PostCommitParentSnapshot-based path the runtime already uses for a
        // failed post-commit StartSubflowJob.
        var workflowResult = await componentCacheStore.GetFlowAsync(
            input.Domain, instance.Flow, instance.FlowVersion, cancellationToken);
        if (!workflowResult.IsSuccess)
            return Result<RetryInstanceOutput>.Fail(workflowResult.Error);

        var workflow = workflowResult.Value!;

        // The transition that originally moved the instance into the SubFlow state is the one
        // HandleSubFlowStep ran under; there is no "reopen" for it (FinishedAt is already set, and
        // genuinely so — the transition itself completed, only its post-commit continuation did
        // not), so this is a lookup, never a mutation.
        var transitions = await instanceTransitionRepository.GetByInstanceIdAsReadOnlyAsync(instance.Id, cancellationToken);
        var originatingTransition = FindOriginatingTransition(transitions, subflowCorrelation.ParentState);
        if (originatingTransition is null)
        {
            logger.SubFlowRestartTransitionNotResolved(instance.Id, subflowCorrelation.ParentState);
            return Result<RetryInstanceOutput>.Fail(Error.Failure(
                WorkflowErrorCodes.ExecutionStepFailed,
                $"Could not resolve the transition that moved instance {instance.Id} into state " +
                $"'{subflowCorrelation.ParentState}'; cannot restart the missing subflow child"));
        }

        // Snapshot identity once, up front, so both the (rare) direct lock-conflict return below and
        // the compensating fault after a failed restart spell the SAME lock key
        // (PostCommitParentSnapshot.LockKey) instead of two hand-formatted copies that could drift.
        var snapshot = new PostCommitParentSnapshot(
            input.Domain,
            instance.Flow,
            workflow.Version,
            instance.Id,
            originatingTransition.TransitionId,
            ExecMode.Sync,
            Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"),
            input.Headers,
            input.RouteValues,
            data: null,
            workflow);

        // Unfault + re-arm Busy under the same short status lock every other status flip uses ("one
        // lock, at the status change" — see .claude/rules/vnext-workflow-developer.md). A live
        // blocking SubFlow's parent is Busy for the child's entire lifetime by design
        // (Instance.AddCorrelation already set that on this still-open correlation, the first time
        // around); leaving it merely Active here would misrepresent an instance that is, once
        // again, waiting on a child, and would let a concurrent request race the restart.
        //
        // Nothing before this block's commit has mutated anything (TryUnfaultAsync/TryMarkBusyAsync
        // are set-based CAS inside a transactional UoW that is never committed on a failing return),
        // so every early return here leaves the instance exactly as found: still Faulted, still
        // carrying its incident, still retryable. The invariant only starts to apply once this
        // commits.
        var lockScope = await instanceStatusLock.AcquireAsync(snapshot.LockKey, cancellationToken);
        await using (lockScope)
        {
            if (!lockScope.IsAcquired)
                return Result<RetryInstanceOutput>.Fail(WorkflowErrors.InstanceLockConflict(instance.Id));

            await using var uow = UnitOfWorkManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

            await instanceRepository.LoadActiveIncidentsAsync(instance, cancellationToken);
            if (!await instanceRepository.TryUnfaultAsync(instance, cancellationToken))
            {
                return Result<RetryInstanceOutput>.Fail(Error.Validation(
                    WorkflowErrorCodes.InstanceNotFaulted,
                    $"Instance {instance.Id} is no longer in faulted state",
                    instance.Id.ToString()));
            }

            if (!await instanceRepository.TryMarkBusyAsync(instance, cancellationToken))
            {
                return Result<RetryInstanceOutput>.Fail(Error.Validation(
                    WorkflowErrorCodes.InstanceBusy,
                    $"Instance {instance.Id} could not be re-armed Busy for the subflow restart",
                    instance.Id.ToString()));
            }

            var resolvedCount = await instanceIncidentRepository.ResolveAllAsync(
                instance.Id, DateTime.UtcNow, cancellationToken);
            await uow.CommitAsync(cancellationToken);

            logger.InstanceUnfaulted(instance.Id);
            if (resolvedCount > 0)
                logger.IncidentsResolved(instance.Id, resolvedCount);
        }
        // Lock released here — post-commit work (the actual subflow start, including any remote
        // call) runs lock-free, same discipline as every other post-commit handler.
        //
        // ── Point of no return: from here, EVERY exit must succeed or fault the parent. ──

        Result<RetryInstanceOutput> restartResult;
        try
        {
            restartResult = await RunSubflowRestartAsync(
                input, instance, workflow, originatingTransition, subflowCorrelation, cancellationToken);
        }
        catch (Exception ex)
        {
            // Anything that throws here — a null dereference in a mapping, a transient infra fault,
            // whatever — is exactly as dangerous as a returned Result.Fail from this point on: the
            // parent is Busy, its incident already resolved. Convert it instead of letting it escape.
            restartResult = Result<RetryInstanceOutput>.Fail(Error.Failure(
                WorkflowErrorCodes.ExecutionStepFailed,
                $"Subflow restart for instance {instance.Id} threw: {ex.Message}",
                ex.ToString()));
        }

        if (restartResult.IsSuccess)
        {
            logger.SubFlowRestartSucceeded(instance.Id, subflowCorrelation.Id, subflowCorrelation.SubFlowInstanceId);
            return restartResult;
        }

        await FaultParentAfterFailedRestartAsync(snapshot, subflowCorrelation, restartResult.Error);
        return restartResult;
    }

    /// <summary>
    /// Builds the restart's execution context and invokes the subflow-start handler, both resolved
    /// from a FRESH DI scope/unit of work (<see cref="IServiceScopeFactory.ExecuteWithWorkflowAsync"/>)
    /// rather than this app service's own ambient one.
    /// <para>
    /// <c>TransitionContextFactory.CreateAsync</c> and <c>StartSubflowJobHandler.HandleAsync</c>
    /// both reload the instance as a TRACKED entity (<c>GetActiveAsync</c>,
    /// <c>FindForSubflowStartAsync</c>). Doing that in the ambient scope would hand a copy of the
    /// aggregate to the request's OWN unit of work — precisely the <c>Instance:100027</c> hazard
    /// <see cref="RetryAsync"/>'s own remarks warn about, where a stale ambient copy overwrote a
    /// status a fresher write had just persisted. A fresh scope (same mechanism
    /// <c>TransitionRunner.MutateParentAsync</c> already uses for <see cref="IPostCommitParentMutationService"/>)
    /// gives both calls their own DbContext instead.
    /// </para>
    /// <para>
    /// <b>Deliberately NOT routed through <see cref="IPostCommitExecutor"/>.</b> Its idempotency
    /// store keys on <c>subflow:{CorrelationId}</c> and treats ANY existing entry — including one
    /// the original attempt already marked Failed — as a duplicate to skip, which would make this
    /// retry a silent no-op forever (the store has a 24h TTL and no notion of "failed, therefore
    /// eligible for a deliberate retry" versus "already handled, this is a duplicate delivery"). A
    /// retry is an explicit, caller-initiated re-attempt, not an accidental redelivery, so it must
    /// bypass that gate. The failure-policy classification is bypassed for the same reason
    /// <c>TransitionRunner.CompensateFailedCoordinationAsync</c> already ignores it for a
    /// <see cref="StartSubflowJob"/>: a Validation-classified failure (e.g. a schema violation) must
    /// still fault the parent, not be waved through as "the client's problem".
    /// </para>
    /// </summary>
    private Task<Result<RetryInstanceOutput>> RunSubflowRestartAsync(
        RetryInstanceInput input,
        Instance instance,
        WorkflowDefinition workflow,
        InstanceTransitionSlim originatingTransition,
        InstanceCorrelation subflowCorrelation,
        CancellationToken cancellationToken)
    {
        // Data intentionally omitted: a restart re-runs the ORIGINAL automatic hop, which never
        // carried a caller-supplied body of its own — "a re-run of a start should look like that
        // start". Threading the retry request's own data into the child's input mapping fed it
        // something the original hop never had, which was scope creep beyond this fix.
        var restartExecutionContext = new WorkflowExecutionContext
        {
            Domain = input.Domain,
            InstanceId = instance.Id.ToString(),
            WorkflowKey = instance.Flow,
            WorkflowVersion = instance.FlowVersion,
            ResolvedWorkflow = workflow,
            TransitionKey = originatingTransition.TransitionId,
            TriggerType = TriggerType.Manual, // Retry is always manual
            Mode = ExecMode.Sync,
            CallerMode = ExecMode.Sync,
            Actor = Shared.ExecutionActor.User,
            CorrelationId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Headers = input.Headers,
            RouteValues = input.RouteValues,
            IsReentry = true,
            Execution = new ExecutionInfo
            {
                ExecutionChainId = Guid.NewGuid().ToString("N"),
                ChainDepth = 0
            }
        };

        return scopeFactory.ExecuteWithWorkflowAsync<RetryInstanceOutput>(
            input.Domain,
            instance.Flow,
            instance.FlowVersion,
            async (sp, ct) =>
            {
                var freshContextFactory = sp.GetRequiredService<ITransitionContextFactory>();
                var freshStartSubflowJobHandler = sp.GetRequiredService<IPostCommitHandler<StartSubflowJob>>();

                var contextResult = await freshContextFactory.CreateAsync(restartExecutionContext, ct);
                if (!contextResult.IsSuccess)
                    return Result<RetryInstanceOutput>.Fail(contextResult.Error);

                var transitionContext = contextResult.Value!;

                // ResolveTransition can miss both the state-scoped and the whole-workflow lookup
                // (an ill-timed definition change, an unexpected key) and CreateAsync still answers
                // Ok with Transition null — StartSubflowJobHandler dereferences it unconditionally,
                // so this must be caught here, not left to throw two calls downstream.
                if (transitionContext.Transition is null)
                {
                    return Result<RetryInstanceOutput>.Fail(Error.Failure(
                        WorkflowErrorCodes.ExecutionStepFailed,
                        $"Could not resolve transition '{originatingTransition.TransitionId}' in workflow " +
                        $"'{instance.Flow}' while restarting the subflow start for instance {instance.Id}"));
                }

                var startJob = new StartSubflowJob(
                    subflowCorrelation.Id, subflowCorrelation.ParentState, PostCommitContinuationBehavior.HandoffToChild);

                var handleResult = await freshStartSubflowJobHandler.HandleAsync(startJob, transitionContext, ct);
                if (!handleResult.IsSuccess)
                    return Result<RetryInstanceOutput>.Fail(handleResult.Error);

                return Result<RetryInstanceOutput>.Ok(new RetryInstanceOutput
                {
                    Id = instance.Id,
                    Status = transitionContext.Instance.Status,
                    // No transition record was actually re-executed — the original transition already
                    // completed normally; only its post-commit continuation is being redone here.
                    // Guid.Empty is the truthful answer within the existing (non-breaking) DTO shape.
                    RetriedTransitionId = Guid.Empty
                });
            },
            cancellationToken,
            resolvedWorkflow: workflow);
    }

    /// <summary>
    /// The restart itself failed (or threw). Leaving the parent silently Busy — re-armed above, with
    /// nothing now running to ever clear it — would strand it exactly the way the original CRITICAL
    /// described, permanently this time: <see cref="RetryAsync"/> only routes a Faulted instance
    /// back into this branch, so a Busy, non-Faulted parent can never re-enter it. Re-fault it
    /// through the SAME <see cref="IPostCommitParentMutationService"/> path
    /// <c>TransitionRunner.CompensateFailedCoordinationAsync</c> already uses for a failed
    /// post-commit <c>StartSubflowJob</c> — resolved from a fresh scope exactly as that caller does
    /// it (<c>MutateParentAsync</c>) — so the parent again carries a fresh incident and is, once
    /// more, visible and retryable.
    /// <para>
    /// <see cref="IInstanceStatusLock.AcquireAsync"/> already retries internally before reporting a
    /// conflict, so a failure here means contention outlasted that. <see cref="MaxFaultCompensationAttempts"/>
    /// gives it a second, outer bound. If every attempt is exhausted, the parent is left Busy with no
    /// incident and no live child — the exact strand this fix exists to prevent, now unavoidable
    /// without a human — and that is logged at a level and EventId meant to be alerted on directly,
    /// not merely noted.
    /// </para>
    /// </summary>
    private async Task FaultParentAfterFailedRestartAsync(
        PostCommitParentSnapshot snapshot,
        InstanceCorrelation subflowCorrelation,
        Error error)
    {
        logger.SubFlowRestartFailed(snapshot.InstanceId, subflowCorrelation.Id, error.Message ?? error.Code);

        var faultRequest = new PostCommitFaultRequest(error.Code, error.Message ?? "Subflow restart failed", error.Detail);

        for (var attempt = 1; attempt <= MaxFaultCompensationAttempts; attempt++)
        {
            // Deliberately CancellationToken.None: this is cleanup after a failure, not new work on
            // behalf of the caller, and must not be abandoned just because the inbound request's
            // token was cancelled — the alternative is the exact permanent strand this path exists
            // to prevent.
            var faultResult = await scopeFactory.ExecuteWithWorkflowAsync<TransitionOutput>(
                snapshot.Domain,
                snapshot.WorkflowKey,
                snapshot.WorkflowVersion,
                async (sp, ct) =>
                {
                    var mutationService = sp.GetRequiredService<IPostCommitParentMutationService>();
                    return await mutationService.FaultAsync(snapshot, faultRequest, ct);
                },
                CancellationToken.None,
                resolvedWorkflow: snapshot.Workflow);

            if (faultResult.IsSuccess)
                return;

            if (attempt < MaxFaultCompensationAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), CancellationToken.None);
                continue;
            }

            logger.SubFlowRestartCompensationExhausted(
                snapshot.InstanceId, subflowCorrelation.Id, MaxFaultCompensationAttempts, faultResult.Error.Code);
        }
    }

    /// <summary>
    /// Load the workflow definition.
    /// </summary>
    private async Task<Result<(Instance Instance, WorkflowDefinition Workflow)>> LoadWorkflowAsync(
        RetryInstanceInput input,
        Instance instance,
        CancellationToken cancellationToken)
    {
        var workflowResult = await componentCacheStore.GetFlowAsync(
            input.Domain,
            input.Workflow,
            instance.FlowVersion,
            cancellationToken);

        return workflowResult.Map(workflow => (instance, workflow));
    }

    /// <summary>
    /// Find the incomplete transition (faulted transition) for the instance.
    /// </summary>
    private async Task<Result<(Instance Instance, WorkflowDefinition Workflow, InstanceTransition Transition)>> 
        FindIncompleteTransitionAsync(
            Instance instance,
            WorkflowDefinition workflow,
            CancellationToken cancellationToken)
    {
        var transition = await instanceTransitionRepository.GetLatestIncompleteAsync(
            instance.Id,
            cancellationToken);

        if (transition == null)
        {
            return Result<(Instance, WorkflowDefinition, InstanceTransition)>.Fail(
                Error.Validation(
                    WorkflowErrorCodes.NoIncompleteTransitionFound,
                    $"No incomplete transition found for instance {instance.Id}",
                    instance.Id.ToString()));
        }

        return Result<(Instance, WorkflowDefinition, InstanceTransition)>.Ok((instance, workflow, transition));
    }

    /// <summary>
    /// Unfault the instance and persist the change.
    /// </summary>
    private async Task<Result<(Instance Instance, WorkflowDefinition Workflow, InstanceTransition Transition)>>
        UnfaultAndPersistAsync(
            (Instance Instance, WorkflowDefinition Workflow, InstanceTransition Transition) data,
            CancellationToken cancellationToken)
    {
        await using var uow = UnitOfWorkManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

        // See RetryFaultedInstanceAsync: the incidents must be loaded before the CAS resolves them.
        await instanceRepository.LoadActiveIncidentsAsync(data.Instance, cancellationToken);

        // Set-based CAS instead of UpdateAsync. Two reasons: the aggregate is detached here (loaded
        // no-tracking in the ambient scope), so Set.Update would rewrite the entire graph — every
        // InstanceData row included — for a four-column flip; and the CAS makes "was still Faulted"
        // part of the write rather than a check that can go stale.
        if (!await instanceRepository.TryUnfaultAsync(data.Instance, cancellationToken))
        {
            return Result<(Instance, WorkflowDefinition, InstanceTransition)>.Fail(
                Error.Validation(
                    WorkflowErrorCodes.InstanceNotFaulted,
                    $"Instance {data.Instance.Id} could not be unfaulted",
                    data.Instance.Id.ToString()));
        }

        // Unconditional and idempotent: the predicate excludes resolved rows, so this closes exactly
        // what was open. The aggregate's own Resolve() calls persist nothing on this path — the rows
        // were read outside a change tracker and are deliberately kept off its navigation.
        var resolvedCount = await instanceIncidentRepository.ResolveAllAsync(
            data.Instance.Id, DateTime.UtcNow, cancellationToken);

        logger.InstanceUnfaulted(data.Instance.Id);

        // Must be committed before ExecuteRetryAsync: the pipeline's instance load rejects a
        // terminal instance, and Faulted counts as terminal.
        await uow.CommitAsync(cancellationToken);

        if (resolvedCount > 0)
            logger.IncidentsResolved(data.Instance.Id, resolvedCount);

        return Result<(Instance, WorkflowDefinition, InstanceTransition)>.Ok(data);
    }

    /// <summary>
    /// Execute the transition retry using the workflow execution service.
    /// </summary>
    private async Task<Result<RetryInstanceOutput>> ExecuteRetryAsync(
        (Instance Instance, WorkflowDefinition Workflow, InstanceTransition Transition) data,
        RetryInstanceInput input,
        CancellationToken cancellationToken)
    {
        var context = new WorkflowExecutionContext
        {
            Domain = input.Domain,
            InstanceId = data.Instance.Id.ToString(),
            WorkflowKey = data.Instance.Flow,
            WorkflowVersion = data.Instance.FlowVersion,
            TransitionKey = data.Transition.TransitionId,
            TriggerType = TriggerType.Manual, // Retry is always manual
            Mode = input.Sync ? ExecMode.Sync : ExecMode.Async,
            CallerMode = input.Sync ? ExecMode.Sync : ExecMode.Async,
            Actor = Shared.ExecutionActor.User,
            CorrelationId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Headers = input.Headers,
            RouteValues = input.RouteValues,
            IsReentry = true, // Retry is a re-entry scenario
            Data = input.Data != null 
                ? new TransitionDataInfo(input.Data.Key, input.Data.Attributes) { Tags = input.Data.Tags } 
                : null,
            Execution = new ExecutionInfo
            {
                ExecutionChainId = Guid.NewGuid().ToString("N"),
                ChainDepth = 0
            },
            Retry = new RetryInfo
            {
                TransitionId = data.Transition.Id
            }
        };

        // The retry request opened the activation episode; name it so the span reads `retry`.
        using var episode = WorkflowTraceLane.UseEpisode(TelemetryConstants.ActivationTriggers.Retry, data.Transition.TransitionId);

        var result = await workflowExecutionService.ExecuteTransitionAsync(context, cancellationToken);

        if (!result.IsSuccess)
            return Result<RetryInstanceOutput>.Fail(result.Error);

        // Reload instance to get current status
        var refreshedInstance = await instanceRepository.FindByIdentifierAsReadOnlyAsync(
            data.Instance.Id.ToString(),
            cancellationToken);

        var currentStatus = refreshedInstance?.Status ?? result.Value!.Status;

        if (currentStatus?.Equals(InstanceStatus.Faulted) == true)
        {
            logger.InstanceRetryFailed(data.Instance.Id, "Instance faulted again after retry");
        }
        else
        {
            logger.InstanceRetrySucceeded(data.Instance.Id);
        }

        return Result<RetryInstanceOutput>.Ok(new RetryInstanceOutput
        {
            Id = result.Value!.Id,
            Status = currentStatus,
            RetriedTransitionId = data.Transition.Id
        });
    }
}
