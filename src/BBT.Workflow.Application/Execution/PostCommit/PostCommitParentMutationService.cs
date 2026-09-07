using System.Diagnostics;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Telemetry;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.PostCommit;

/// <summary>
/// Applies post-handoff settlement and failure recovery to freshly reloaded parent state.
/// </summary>
public sealed class PostCommitParentMutationService(
    IUnitOfWorkManager uowManager,
    IInstanceRepository instanceRepository,
    IInstanceStatusLock instanceStatusLock,
    IStateNotificationScheduler stateNotificationScheduler,
    ILogger<PostCommitParentMutationService> logger) : IPostCommitParentMutationService
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

        return await MutateFreshAsync(source, includeLatestData: true, async (instance, ct) =>
        {
            // A synchronous callback may already have completed or faulted the parent. Its
            // authoritative terminal result wins over a later outer post-commit failure.
            if (instance.IsCompleted)
                return null;

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

            instance.Fault(source.Domain, source.CallerMode == ExecMode.Sync);
            await instanceRepository.UpdateAsync(instance, true, ct);
            return new ActivationVerdict(
                TelemetryConstants.ActivationOutcomes.Faulted, CasFlipped: false, instance.GetCurrentState);
        }, cancellationToken);
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
