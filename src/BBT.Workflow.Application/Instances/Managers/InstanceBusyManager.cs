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
    public async Task<InstanceStatus?> MarkBusyWithPropagationAsync(
        Guid instanceId, CancellationToken cancellationToken = default)
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
            if (instance is null)
                return null;

            if (instance.IsCompleted)
                return instance.Status;

            if (!instance.IsBusy)
            {
                // A level with no active SubFlow below it owns the client-visible status, so the
                // projection rides along in the same CAS. A level that HAS one does not: its
                // EffectiveStatus is the leaf's and is stamped after the walk returns.
                var ownsVisibleStatus = instance.Subflow is null;
                if (await instanceRepository.TryMarkBusyAsync(
                        instanceId,
                        cancellationToken,
                        ownsVisibleStatus ? InstanceStatus.Busy : null))
                {
                    logger.InstanceMarkedBusy(instance.Id);
                }

                await uow.CommitAsync(cancellationToken);
            }
        }

        return await PropagateAndStampAsync(instance, InstanceStatus.Busy, cancellationToken);
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
            if (!await instanceRepository.TryMarkBusyAsync(
                    instanceId,
                    cancellationToken,
                    current.Subflow is null ? InstanceStatus.Busy : null))
                return BusyMarkOutcome.AlreadyBusy;

            await uow.CommitAsync(cancellationToken);

            logger.InstanceMarkedBusy(current.Id);
            instance = current;
        }

        await PropagateAndStampAsync(instance, InstanceStatus.Busy, cancellationToken);

        return BusyMarkOutcome.Marked;
    }

    /// <inheritdoc />
    public async Task<bool> TryReleaseAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default,
        InstanceStatus? effectiveStatus = null)
    {
        await using var uow = uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });

        // Same compare-and-set collapse as MarkBusyAsync: the "IsBusy && !IsCompleted" guard
        // reduces to "Status == Busy" (Busy and the terminal statuses are mutually exclusive).
        // No transaction needed — the same reasoning as MarkBusyAsync applies.
        var flipped = await instanceRepository.TryReleaseBusyAsync(
            instanceId, cancellationToken, effectiveStatus);
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

            // The reserve this compensates stamped every level with Busy; undoing it must put the
            // projection back or the ancestors keep reporting a chain that is no longer reserved.
            await using var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });
            await instanceRepository.SetEffectiveStatusAsync(
                instance.Id, InstanceStatus.Active, cancellationToken);
            await uow.CommitAsync(cancellationToken);
            return;
        }

        // The leaf owns its own visible status, so the projection goes back with the flip.
        await TryReleaseAsync(instanceId, cancellationToken, InstanceStatus.Active);
    }

    /// <summary>
    /// Propagates the Busy mark to the active SubFlow (if any) via the instance command gateway and
    /// stamps THIS level's <see cref="Instance.EffectiveStatus"/> with whatever the bottom of the
    /// chain turned out to be.
    /// </summary>
    /// <remarks>
    /// The walk is top-down and synchronous, so the leaf's status is known by the time the recursive
    /// call returns — that return value is the whole reason the gateway answers with a status. An
    /// unknown answer (older runtime across a domain hop, or a level that reported nothing) writes
    /// NOTHING: assuming Busy there would park a client on a chain that may already be at rest.
    /// </remarks>
    /// <returns>The status a client polling <paramref name="instance"/> would observe.</returns>
    private async Task<InstanceStatus?> PropagateAndStampAsync(
        Instance instance,
        InstanceStatus ownStatus,
        CancellationToken cancellationToken)
    {
        var subflow = instance.Subflow;
        if (subflow is null)
            return ownStatus;

        var result = await instanceCommandGateway.MarkBusyAsync(ToBusyInput(subflow), cancellationToken);
        var leafStatus = result.IsSuccess
            ? InstanceStatus.TryFromCode(result.Value?.EffectiveStatusCode)
            : null;

        if (leafStatus is null)
            return null;

        await using var uow = uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });
        await instanceRepository.SetEffectiveStatusAsync(instance.Id, leafStatus, cancellationToken);
        await uow.CommitAsync(cancellationToken);
        return leafStatus;
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
