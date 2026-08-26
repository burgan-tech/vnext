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
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

        // Called with the instance status lock held. The old shape loaded the aggregate to check
        // IsBusy/IsCompleted and then wrote Status — but the guard reduces to "Status == Active"
        // (Busy is excluded by IsBusy; Completed/Faulted/Passive by IsCompleted), so the whole
        // read-check-write is one compare-and-set: guard in the WHERE, decision from the database
        // state under the lock, no aggregate load at all.
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
                         new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
        {
            // The load stays: the subflow propagation below walks the correlation navigation.
            // Only the WRITE is set-based now — the tracked-update path rewrote the full row.
            instance = await instanceRepository.FindWithActiveSubFlowAsync(instanceId, cancellationToken);

            if (instance is null)
                return;

            if (instance is { IsBusy: false, IsCompleted: false })
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
                         new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
        {
            var current = await instanceRepository.FindWithActiveSubFlowAsync(instanceId, cancellationToken);

            if (current is null || current.IsCompleted)
                return BusyMarkOutcome.Skipped;

            // Authoritative second check: callers hold the distributed status lock while this
            // transaction is active. A concurrent request that won the race is observed here.
            if (current.IsBusy)
                return BusyMarkOutcome.AlreadyBusy;

            // Set-based CAS; the WHERE re-verifies Active, so a racer that slipped past the
            // in-memory check above still resolves to AlreadyBusy instead of a double flip.
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
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

        // Same compare-and-set collapse as MarkBusyAsync: the "IsBusy && !IsCompleted" guard
        // reduces to "Status == Busy" (Busy and the terminal statuses are mutually exclusive).
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
