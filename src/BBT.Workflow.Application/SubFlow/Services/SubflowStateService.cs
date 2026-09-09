using BBT.Aether.Events;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.PostCommit.Relay;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.SubFlow;

/// <inheritdoc cref="ISubflowStateService" />
public sealed class SubflowStateService(
    IUnitOfWorkManager uowManager,
    IInstanceRepository instanceRepository,
    ITransitionLockScopeFactory transitionLockScopeFactory,
    IPostCommitRelayDispatcher relayDispatcher,
    IOptions<WorkflowExecutionOptions> executionOptions,
    ILogger<SubflowStateService> logger)
    : ISubflowStateService
{
    private LockAcquireWait TerminalLockWait => executionOptions.Value.SubItemTerminalLockRetry.ToLockAcquireWait();

    /// <inheritdoc />
    public async Task UpdateParentStateAsync(
        SubFlowStateChangedInput input,
        CancellationToken cancellationToken = default)
    {
        using var activity = SubFlowActivityHelper.StartActivity($"SubFlow.StateChange/{input.Domain}/{input.Flow}");
        SubFlowActivityHelper.EnrichWithStateChange(
            activity,
            input.SubInstanceId,
            input.ParentInstanceId,
            input.Domain,
            input.Flow,
            input.NewState);

        using (logger.BeginScope(new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.Domain] = input.Domain,
            [TelemetryConstants.TagNames.Flow] = input.Flow,
            [TelemetryConstants.TagNames.FlowVersion] = input.Version ?? "N/A",
            [TelemetryConstants.TagNames.InstanceId] = input.ParentInstanceId,
            [TelemetryConstants.TagNames.ParentInstanceId] = input.ParentInstanceId,
            [TelemetryConstants.TagNames.SubflowInstanceId] = input.SubInstanceId
        }))
        {
            try
            {
                logger.SubFlowStateChangeReceived(
                    input.SubInstanceId,
                    input.ParentInstanceId,
                    input.NewState);

                var upwardEvents = await ApplyAsync(input, activity, cancellationToken);
                if (upwardEvents is null)
                    return;

                // Outside the lock and after the commit: when this parent is ITSELF a subflow, the
                // write above raised its own InstanceSubStateChangedEvent for the grandparent. Those
                // events ride the outbox like every other event; handing them to the dispatcher here
                // is the second call site that makes the fast path walk the whole ancestor chain
                // instead of stopping one level up. Relaying after the lock is released keeps each
                // level's lock held only for its own write.
                await relayDispatcher.RelayAsync(upwardEvents, cancellationToken);

                SubFlowActivityHelper.SetSuccess(activity);
            }
            catch (Exception ex)
            {
                SubFlowActivityHelper.SetError(activity, ex.Message, ex);
                throw;
            }
        }
    }

    /// <summary>
    /// Applies the state change under the per-sub-item lock. Returns the events the write raised for
    /// the level above (empty when this parent is not itself a subflow), or <c>null</c> when the
    /// change was not applied at all.
    /// </summary>
    private async Task<IReadOnlyList<DomainEventEnvelope>?> ApplyAsync(
        SubFlowStateChangedInput input,
        System.Diagnostics.Activity? activity,
        CancellationToken cancellationToken)
    {
        // Same per-sub-item key the three terminal paths take (SubflowCompletionService,
        // SubflowFaultService, SubflowCancellationService). This was the ONLY parent-mutation path
        // that mutated the correlation + parent pair with no lock at all, which is what made its
        // read-check-write guard a real TOCTOU: two deliveries could both read the same
        // SubFlowStateChangedAt, both pass the check, and the older one land last. Sub-item
        // granularity, so sibling subflows never serialize against each other; and the write order
        // (one SaveChanges batch, Instances before InstancesCorrelations) stays identical to the
        // terminal paths', so there is no lock-order inversion between them.
        var lockKey = $"vnext:{input.Domain}:{input.Flow}:{input.ParentInstanceId}:sub:{input.SubInstanceId:N}";

        // Bounded wait rather than fail-fast, for the same reason the terminal paths wait: the
        // critical section is one short transaction, and a duplicate that arrives while the original
        // is still inside it cannot see the pending write — failing immediately would push it into a
        // full broker re-delivery cycle instead.
        await using var lockScope = await transitionLockScopeFactory.AcquireAsync(
            lockKey, TerminalLockWait, cancellationToken);

        if (!lockScope.IsAcquired)
        {
            logger.SubFlowStateChangeLockNotAcquired(lockKey, input.SubInstanceId);
            activity?.SetTag("vnext.subflow.result", "lock_not_acquired");
            throw new SubflowTerminalLockNotAcquiredException(
                input.Domain,
                input.Flow,
                input.ParentInstanceId.ToString(),
                "StateChange");
        }

        // Transactional, unlike the terminal paths': without a transaction Aether's
        // CommitWithoutTransactionAsync stages the outbox rows AFTER UpdateAsync(autoSave) has
        // already committed the Instance row — documented there as not atomic. The upward
        // InstanceSubStateChangedEvent this hop raises for the grandparent rides those rows, and the
        // relay above hands the same event to the fast path, so the two must become durable together
        // or not at all.
        await using var uow = uowManager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = true
        });

        // Narrow load: only this child's OPEN correlation, no DataList. The default detail load
        // pulls the whole instance-data history unsplit, which this path never reads.
        var parentInstance = await instanceRepository.FindForSubflowStateChangeAsync(
            input.ParentInstanceId, input.SubInstanceId, cancellationToken);

        if (parentInstance == null)
        {
            logger.SubFlowStateChangeParentNotFound(input.ParentInstanceId, input.SubInstanceId);
            activity?.SetTag("vnext.subflow.result", "parent_not_found");
            return null;
        }

        var correlation = parentInstance.FindCorrelationBySubInstanceId(input.SubInstanceId);
        if (correlation == null)
        {
            logger.SubFlowStateChangeCorrelationNotFound(input.SubInstanceId, input.ParentInstanceId);
            activity?.SetTag("vnext.subflow.result", "correlation_not_found");
            return null;
        }

        // Out-of-order event detection using timestamp:
        // If correlation already has a state update with a later timestamp,
        // this event is out-of-order/stale - reject it to prevent downgrade.
        // Both sides are truncated to microseconds to avoid false rejections caused
        // by precision loss when DateTime (100ns ticks) is stored in PostgreSQL (microseconds).
        // Equal timestamps are ACCEPTED and re-applied: a duplicate delivery carries the same
        // ChangedAt, re-applying it is idempotent, and rejecting it would close the only recovery
        // path a redelivery has.
        if (correlation.SubFlowStateChangedAt.HasValue &&
            TruncateToMicroseconds(input.ChangedAt) < TruncateToMicroseconds(correlation.SubFlowStateChangedAt.Value))
        {
            logger.SubFlowStateChangeOutOfOrder(
                input.SubInstanceId,
                input.ChangedAt,
                correlation.SubFlowStateChangedAt.Value,
                correlation.SubFlowCurrentState,
                input.NewState);
            activity?.SetTag("vnext.subflow.result", "out_of_order");
            return null;
        }

        // Update correlation's SubFlowCurrentState with timestamp
        correlation.UpdateSubFlowState(input.NewState, input.ChangedAt);

        // Propagate EffectiveState with type and subtype to parent. When the parent is itself a
        // SubFlow this raises the upward InstanceSubStateChangedEvent for ITS parent.
        parentInstance.PropagateEffectiveStateToParent(input.NewState, input.NewStateType, input.NewStateSubType);

        // Snapshot BEFORE the save: the SaveChanges sink drains and clears the aggregate's events.
        var upwardEvents = parentInstance.GetDomainEvents().ToList();

        await instanceRepository.UpdateAsync(parentInstance, true, cancellationToken);
        await uow.CommitAsync(cancellationToken);

        logger.SubFlowStateChangeApplied(
            input.SubInstanceId,
            input.ParentInstanceId,
            input.NewState);

        return upwardEvents;
    }

    // Truncates a DateTime to microsecond precision (removes sub-microsecond ticks).
    // PostgreSQL stores timestamps with microsecond precision; .NET DateTime uses 100ns ticks.
    // Without truncation, a stored-then-read value may differ by up to 900ns from the original,
    // causing valid equal-timestamp events to appear out-of-order.
    private static DateTime TruncateToMicroseconds(DateTime dt)
        => new(dt.Ticks - dt.Ticks % 10, dt.Kind);
}
