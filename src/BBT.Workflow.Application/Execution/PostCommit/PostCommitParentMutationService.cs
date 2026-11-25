using System.Diagnostics;
using BBT.Aether.Events;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.PostCommit.Relay;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.SubFlow.Headers;
using BBT.Workflow.Telemetry;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.PostCommit;

/// <summary>
/// Applies post-handoff settlement and failure recovery to freshly reloaded parent state.
/// </summary>
/// <param name="carriedHeaderPolicy">Filters the request headers that ride the terminal event to the parent; null disables header capture, production DI always supplies it.</param>
/// <param name="relayDispatcher">Relays the events a fault commit raised to their receiver right after that commit; null leaves them to the outbox alone, production DI always supplies it.</param>
/// <param name="correlationRepository">Reads the correlation of a failed child start by id, so a start compensation can step aside for a child that already settled its own fault; null never steps aside (the parent is faulted as before), production DI always supplies it.</param>
public sealed class PostCommitParentMutationService(
    IUnitOfWorkManager uowManager,
    IInstanceRepository instanceRepository,
    IInstanceStatusLock instanceStatusLock,
    IStateNotificationScheduler stateNotificationScheduler,
    ILogger<PostCommitParentMutationService> logger,
    ISubItemEventDataResolver? subItemDataResolver = null,
    ICarriedHeaderPolicy? carriedHeaderPolicy = null,
    IPostCommitRelayDispatcher? relayDispatcher = null,
    IInstanceCorrelationRepository? correlationRepository = null) : IPostCommitParentMutationService
{
    public async Task<Result<TransitionOutput>> SettleAsync(
        PostCommitParentSnapshot source,
        ContinuationSet continuations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(continuations);

        return await MutateFreshAsync(source, NeedsLatestDataForSettle(source), async (instance, ct) =>
        {
            var context = CreateFreshContext(source, instance);
            // A synchronous child callback may already have settled the same parent state and
            // scheduled its notification. The outer barrier only owns a notification when it
            // changes a freshly reloaded Busy parent to its resting Active state.
            var shouldScheduleNotification =
                continuations.ResolvedStatus == InstanceStatus.Active && instance.IsBusy;
            await TransitionSettlement.ApplyAsync(
                context,
                continuations.ResolvedStatus,
                scheduleNotification: shouldScheduleNotification,
                instanceRepository,
                stateNotificationScheduler,
                logger,
                ct,
                // The episode rests here only when nothing continues it: an enqueued continuation
                // carries it to the next job, and a fresh parent that is no longer Busy was already
                // settled — and its episode already closed — by a synchronous child callback.
                chainSettled: !continuations.ContinuationEnqueued && instance.IsBusy);
            return context.Directives.Activation;
        }, cancellationToken);
    }

    public async Task<Result<TransitionOutput>> FaultAsync(
        PostCommitParentSnapshot source,
        PostCommitFaultRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<DomainEventEnvelope> faultEvents = [];

        var result = await MutateFreshAsync(source, includeLatestData: true, async (instance, ct) =>
        {
            // A synchronous callback may already have completed or faulted the parent. Its
            // authoritative terminal result wins over a later outer post-commit failure.
            if (instance.IsCompleted)
                return null;

            // The failed start created a child that then faulted and reported it here: its
            // settlement closed the correlation and ran this parent's error boundary, which may have
            // routed the parent to a recovery state that is not terminal. The boundary wins over the
            // start compensation (vnext#1256). This only happens across domains: a child in this
            // domain reports its fault through the Inbox, after the compensation has run.
            if (request.StartedSubflowCorrelationId is { } startedCorrelationId &&
                await IsSettledByTheChildsFaultAsync(instance, startedCorrelationId, ct))
            {
                logger.PostCommitStartFaultSupersededByChildSettlement(
                    instance.Id, startedCorrelationId, request.ErrorCode);
                return null;
            }

            // Fault() projects the active incident into the upward event; load it when one exists.
            await instanceRepository.LoadActiveIncidentsAsync(instance, ct);

            if (!instance.HasActiveIncident)
            {
                instance.AddIncident(InstanceIncidentFactory.Create(
                    state: instance.GetCurrentState,
                    transition: source.TransitionKey,
                    taskKey: null,
                    message: request.ErrorMessage ?? "Post-commit execution failed",
                    errorCode: request.ErrorCode,
                    errorLayer: "PostCommit",
                    stackTrace: request.StackTrace,
                    traceId: source.TraceId));
            }

            instance.Fault(source.Domain, source.CallerMode == ExecMode.Sync,
                subItemData: await subItemDataResolver.ResolveOrStoredAsync(instance, cancellationToken),
                carriedHeaders: carriedHeaderPolicy?.Capture(source.Headers));

            // Snapshot BEFORE the save: the SaveChanges sink drains and clears the aggregate's events.
            faultEvents = instance.GetDomainEvents().ToList();

            await instanceRepository.UpdateAsync(instance, true, ct);
            return new ActivationVerdict(
                TelemetryConstants.ActivationOutcomes.Faulted, CasFlipped: false, instance.GetCurrentState);
        }, cancellationToken);

        // After the commit, with the status lock and the unit of work released: when this instance
        // is a sub item, the fault above raised its parent's InstanceSubFaultedEvent in a unit of
        // work no runner relays from. Handing it to the dispatcher here is what reaches a parent in
        // another domain, whose fault the child domain's Inbox would drop (vnext#1256). A fault
        // that did not commit returns a failed result or throws, and relays nothing.
        if (result.IsSuccess && relayDispatcher is not null && faultEvents.Count > 0)
            await relayDispatcher.RelayAsync(faultEvents, cancellationToken);

        return result;
    }

    /// <summary>
    /// Whether the started SubFlow's correlation was closed by that child's own FAULT. Only then does
    /// the start compensation step aside; every other case faults the parent as before (fail-safe):
    /// a correlation that is still open (the child never came to exist or never reported), one that
    /// does not exist, one that belongs to another parent, a SubProcess correlation, and one closed by
    /// a completion or a cancel. The settlement reload carries open correlations only, so a closed one
    /// is read by id here rather than by widening that include.
    /// </summary>
    private async Task<bool> IsSettledByTheChildsFaultAsync(
        Instance instance,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (correlationRepository is null ||
            instance.ChildCorrelations.Any(c => c.Id == correlationId && !c.IsCompleted))
            return false;

        var correlation = await correlationRepository.FindAsync(correlationId, false, cancellationToken);
        return correlation is { IsCompleted: true, TerminalOutcome: SubItemTerminalOutcome.Faulted } &&
               correlation.ParentInstanceId == instance.Id &&
               correlation.SubFlowType.Equals(SubFlowType.SubFlow);
    }

    /// <summary>
    /// Decides whether the settlement reload must carry the parent's latest data row.
    /// <para>
    /// The mutation logic itself never reads instance data: <c>TransitionSettlement</c> reads
    /// status and open correlations, <c>TryReleaseBusyAsync</c> is a status CAS,
    /// <c>StateNotificationScheduler</c> takes its payload from the snapshot's <c>Data</c>, and
    /// <c>IsSubFlow</c>/<c>IsSubItem</c> derive from the <c>ExtraProperties</c> column. The only
    /// readers of <c>LatestData</c> sit downstream of the returned <c>TransitionOutput.PipelineInstance</c>,
    /// and that property is consumed by exactly one place: <c>InstanceCommandAppService.EnrichOutputCoreAsync</c>,
    /// reached only for <c>sync=true</c> requests. A job-driven (async) caller reads <c>IsSuccess</c>
    /// and drops the output.
    /// </para>
    /// <para>Decision matrix — revise here if a new reader of <c>LatestData</c> appears:</para>
    /// <list type="table">
    ///   <listheader><term>Path</term><description>Open correlations / Latest data</description></listheader>
    ///   <item><term>Settle, async caller</term>
    ///     <description>required / <b>not needed</b> — output is discarded by the job handler.</description></item>
    ///   <item><term>Settle, sync caller, parent rests Busy (open SubFlow)</term>
    ///     <description>required / not needed in principle — <c>EnrichOutputCoreAsync</c> ignores a Busy
    ///     <c>PipelineInstance</c> and re-reads the row; still loaded because the resting status is only
    ///     known after the reload (accepted cost on the sync path).</description></item>
    ///   <item><term>Settle, sync caller, settles to Active / already Completed</term>
    ///     <description>required / <b>required</b> — attributes + entity ETag are projected from it.</description></item>
    ///   <item><term>Fault, instance is not a SubFlow</term>
    ///     <description>required (child cascade) / not needed — loaded anyway, see below.</description></item>
    ///   <item><term>Fault, instance is a SubFlow</term>
    ///     <description>required / <b>required</b> — <c>Instance.Fault</c> publishes
    ///     <c>InstanceSubFaultedEvent.InstanceData</c> upward from <c>LatestData</c>.</description></item>
    /// </list>
    /// <para>
    /// Fault therefore always loads the data: whether the instance is a SubFlow is known only after
    /// the reload, faults are the exceptional path, and a second query to find out would cost more
    /// than the row it saves. Settle loads it only for a sync caller. Neither path may skip the
    /// correlations. A <c>LatestData</c> that is null because it was not loaded is silently wrong
    /// for a projection, so the sync gate must stay exact — do not widen the skip to "sync but Busy"
    /// without moving the decision after the reload.
    /// </para>
    /// </summary>
    private static bool NeedsLatestDataForSettle(PostCommitParentSnapshot source) =>
        source.CallerMode == ExecMode.Sync;

    private async Task<Result<TransitionOutput>> MutateFreshAsync(
        PostCommitParentSnapshot source,
        bool includeLatestData,
        Func<Instance, CancellationToken, Task<ActivationVerdict?>> mutation,
        CancellationToken cancellationToken)
    {
        // Post-commit mutations are status flips — the short status lock serializes them,
        // and the mutation commits inside this scope before release.
        await using var lockScope = await instanceStatusLock.AcquireAsync(source.LockKey, cancellationToken);
        if (!lockScope.IsAcquired)
            return Result<TransitionOutput>.Fail(WorkflowErrors.InstanceLockConflict(source.InstanceId));

        await using var uow = uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });

        // Open correlations always (settlement guard, fault cascade); the latest data row only when
        // the caller will project it — see the decision matrix on NeedsLatestDataForSettle.
        var instance = await instanceRepository.FindForPostCommitSettlementAsync(
            source.InstanceId,
            includeLatestData,
            cancellationToken);
        if (instance is null)
            return Result<TransitionOutput>.Fail(WorkflowErrors.InstanceNotFound(source.InstanceId.ToString()));

        var verdict = await mutation(instance, cancellationToken);
        ActivityContext commitContext;
        using (var commitActivity = PipelineStepActivityHelper.StartTransitionActivity(
                   "Uow.Commit", source.TransitionKey))
        {
            await uow.CommitAsync(cancellationToken);
            commitContext = commitActivity?.Context ?? default;
        }

        // Same rule as TransitionRunner: the episode closes once the status write is durable.
        if (verdict is not null)
        {
            ActivationActivity.Emit(
                PipelineStepActivityHelper.ActivitySource,
                verdict.Outcome,
                source.InstanceId,
                source.Domain,
                source.WorkflowKey,
                source.TransitionKey,
                verdict.StateTo ?? instance.GetCurrentState,
                verdict.CasFlipped,
                commitContext);
        }

        return Result<TransitionOutput>.Ok(new TransitionOutput
        {
            Id = instance.Id,
            Key = instance.Key,
            Status = instance.Status,
            PipelineInstance = instance
        });
    }

    private TransitionExecutionContext CreateFreshContext(
        PostCommitParentSnapshot source,
        Instance instance)
    {
        // The definition travels on the snapshot: the settlement runs after the originating scope
        // handed off its lock, so re-resolving here would repeat a read the caller already made.
        var workflow = source.Workflow;
        var freshState = workflow.FindState(instance.GetCurrentState);

        return new TransitionExecutionContext
        {
            Domain = source.Domain,
            WorkflowKey = source.WorkflowKey,
            InstanceId = source.InstanceId,
            TransitionKey = source.TransitionKey,
            CallerMode = source.CallerMode,
            Mode = source.CallerMode,
            TraceId = source.TraceId,
            Headers = source.Headers,
            RouteValues = source.RouteValues,
            Data = source.Data,
            Workflow = workflow,
            Instance = instance,
            Current = freshState!,
            Target = freshState,
            // Post-commit settlement always acts on behalf of the chain that handed off —
            // the owner of the Busy lifecycle. Without this, TransitionSettlement's
            // ownership guard would skip the settle and strand the parent Busy.
            OwnsStatus = true
        };
    }
}
