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

        // This method is called with the instance status lock held. Read inside the isolated UoW
        // so the decision below is based on the database state observed under that lock, rather
        // than on an entity tracked by the caller's ambient UoW before the lock was acquired.
        var result = await instanceRepository.GetResultAsync(
            instanceId.ToString(), includeDetails: false, cancellationToken);

        if (!result.IsSuccess || result.Value is null)
        {
            logger.InstanceNotFoundForBusyMarker(instanceId);
            return false;
        }

        var instance = result.Value;
        if (instance.IsBusy || instance.IsCompleted)
            return false;

        instance.Busy();
        await instanceRepository.UpdateAsync(instance, false, cancellationToken);
        await uow.CommitAsync(cancellationToken);

        logger.InstanceMarkedBusy(instanceId);
        return true;
    }

    /// <inheritdoc />
    public async Task MarkBusyWithPropagationAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        Instance? instance;

        await using (var uow = uowManager.Begin(
                         new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
        {
            instance = await instanceRepository.FindWithActiveSubFlowAsync(instanceId, cancellationToken);

            if (instance is null)
                return;

            if (instance is { IsBusy: false, IsCompleted: false })
            {
                instance.Busy();
                await instanceRepository.UpdateAsync(instance, false, cancellationToken);
                await uow.CommitAsync(cancellationToken);

                logger.InstanceMarkedBusy(instance.Id);
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

            current.Busy();
            await instanceRepository.UpdateAsync(current, false, cancellationToken);
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

        var result = await instanceRepository.GetResultAsync(
            instanceId.ToString(), includeDetails: false, cancellationToken);

        if (!result.IsSuccess || result.Value is null)
            return false;

        var instance = result.Value;
        if (!instance.IsBusy || instance.IsCompleted)
            return false;

        instance.Active();
        await instanceRepository.UpdateAsync(instance, false, cancellationToken);
        await uow.CommitAsync(cancellationToken);

        return true;
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
