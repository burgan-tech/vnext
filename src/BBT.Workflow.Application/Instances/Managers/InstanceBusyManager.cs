using BBT.Aether.Uow;
using BBT.Workflow.Gateway;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Instances;

/// <inheritdoc cref="IInstanceBusyManager" />
public sealed class InstanceBusyManager(
    IInstanceRepository instanceRepository,
    IUnitOfWorkManager uowManager,
    IInstanceCommandGateway instanceCommandGateway,
    ILogger<InstanceBusyManager> logger) : IInstanceBusyManager
{
    /// <inheritdoc />
    public async Task<bool> MarkBusyAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        await using var uow = uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });

        // The old shape loaded the aggregate to check IsBusy/IsCompleted and then wrote Status —
        // but the guard reduces to "Status == Active" (Busy is excluded by IsBusy;
        // Completed/Faulted/Passive by IsCompleted), so the whole read-check-write is one
        // compare-and-set: guard in the WHERE, no aggregate load at all. The CAS is
        // self-sufficient regardless of whether a caller also holds the distributed status lock
        // (some do — AcceptAsync's BypassBusyCheck case; some don't — TakeOverAsync, subflow
        // resume) — no database transaction needed either, since there is nothing else here for
        // one to make atomic alongside.
        var flipped = await instanceRepository.TryMarkBusyAsync(instanceId, cancellationToken);
        await uow.CommitAsync(cancellationToken);

        if (flipped)
        {
            logger.InstanceMarkedBusy(instanceId);
        }

        return flipped;
    }

    /// <inheritdoc />
    public async Task MarkBusyWithPropagationAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        Instance? instance;

        await using (var uow = uowManager.Begin(
                         new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew }))
        {
            // The load stays: the subflow propagation below walks the correlation navigation.
            // Only the WRITE is set-based now — the tracked-update path rewrote the full row.
            // No database transaction across the read and the write: the read is informational
            // (propagation target, fail-fast classification), not a condition the write's CAS
            // depends on, so there is nothing for a shared transaction to protect.
            instance = await instanceRepository.FindWithActiveSubFlowAsync(instanceId, cancellationToken);

            // A completed instance must not have Busy propagated to its subflow — there is
            // nothing left to resume, and a terminal parent's correlation is being closed, not
            // extended. (An already-Busy, still-live parent is different: propagation still runs
            // for it below — see MarkBusyWithPropagationAsync_WhenAlreadyBusyParent_ShouldStillPropagateToSubflow.)
            if (instance is null || instance.IsCompleted)
                return;

            if (!instance.IsBusy)
            {
                if (await instanceRepository.TryMarkBusyAsync(instanceId, cancellationToken))
                {
                    logger.InstanceMarkedBusy(instance.Id);
                }

                await uow.CommitAsync(cancellationToken);
            }
        }

        await PropagateToSubflowAsync(instance, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<BusyMarkOutcome> TryMarkBusyWithPropagationAsync(
        Guid instanceId, CancellationToken cancellationToken = default)
    {
        Instance instance;

        await using (var uow = uowManager.Begin(
                         new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew }))
        {
            // Informational read only — not a condition the write below depends on, so it does
            // not need to share a database transaction with the CAS that follows. It exists to
            // classify Skipped vs. AlreadyBusy for the caller and to load the subflow correlation
            // for propagation; a racer that slips past this in-memory check still resolves
            // correctly at the CAS's own WHERE clause, with or without a shared transaction.
            var current = await instanceRepository.FindWithActiveSubFlowAsync(instanceId, cancellationToken);

            if (current is null || current.IsCompleted)
                return BusyMarkOutcome.Skipped;

            if (current.IsBusy)
                return BusyMarkOutcome.AlreadyBusy;

            // Set-based CAS; the WHERE re-verifies Active, so a racer that slipped past the
            // in-memory check above still resolves to AlreadyBusy instead of a double flip. This
            // is the actual mutual-exclusion mechanism — not the transaction, not an external lock.
            if (!await instanceRepository.TryMarkBusyAsync(instanceId, cancellationToken))
                return BusyMarkOutcome.AlreadyBusy;

            await uow.CommitAsync(cancellationToken);

            logger.InstanceMarkedBusy(current.Id);
            instance = current;
        }

        await PropagateToSubflowAsync(instance, cancellationToken);

        return BusyMarkOutcome.Marked;
    }

    /// <inheritdoc />
    public async Task<bool> TryReleaseAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        await using var uow = uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });

        // Same compare-and-set collapse as MarkBusyAsync: the "IsBusy && !IsCompleted" guard
        // reduces to "Status == Busy" (Busy and the terminal statuses are mutually exclusive).
        // No transaction needed — the same reasoning as MarkBusyAsync applies.
        var flipped = await instanceRepository.TryReleaseBusyAsync(instanceId, cancellationToken);
        await uow.CommitAsync(cancellationToken);

        return flipped;
    }

    /// <inheritdoc />
    public async Task ReleaseWithPropagationAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var instance = await instanceRepository.FindWithActiveSubFlowAsync(instanceId, cancellationToken);

        if (instance is null)
            return;

        // An instance with an open SubFlow correlation is Busy for that subflow's lifetime by
        // design — the chain reserve never flipped it, so releasing it here would settle a parent
        // that is legitimately mid-subflow. Recurse past it and release the leaf only.
        if (instance.Subflow is not null)
        {
            await PropagateReleaseToSubflowAsync(instance, cancellationToken);
            return;
        }

        await TryReleaseAsync(instanceId, cancellationToken);
    }

    /// <summary>
    /// Propagates the Busy mark to the active SubFlow (if any) via the instance command gateway.
    /// </summary>
    private async Task PropagateToSubflowAsync(Instance instance, CancellationToken cancellationToken)
    {
        var subflow = instance.Subflow;
        if (subflow is not null)
        {
            await instanceCommandGateway.MarkBusyAsync(ToBusyInput(subflow), cancellationToken);
        }
    }

    /// <summary>
    /// Propagates the release to the active SubFlow (if any) via the instance command gateway.
    /// </summary>
    private async Task PropagateReleaseToSubflowAsync(Instance instance, CancellationToken cancellationToken)
    {
        var subflow = instance.Subflow;
        if (subflow is not null)
        {
            await instanceCommandGateway.ReleaseBusyAsync(ToBusyInput(subflow), cancellationToken);
        }
    }

    private static MarkBusyInput ToBusyInput(InstanceCorrelation subflow)
        => new()
        {
            Domain = subflow.SubFlowDomain,
            Workflow = subflow.SubFlowName,
            InstanceId = subflow.SubFlowInstanceId,
            Version = subflow.SubFlowVersion
        };
}
