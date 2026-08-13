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
    public async Task MarkBusyAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var result = await instanceRepository.GetResultAsync(
            instanceId.ToString(), includeDetails: false, cancellationToken);

        if (!result.IsSuccess || result.Value is null)
        {
            logger.InstanceNotFoundForBusyMarker(instanceId);
            return;
        }

        var instance = result.Value;
        if (instance.IsBusy || instance.IsCompleted)
            return;

        await using var uow = uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

        instance.Busy();
        await instanceRepository.UpdateAsync(instance, false, cancellationToken);
        await uow.CommitAsync(cancellationToken);

        logger.InstanceMarkedBusy(instanceId);
    }

    /// <inheritdoc />
    public async Task MarkBusyWithPropagationAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var instance = await instanceRepository.FindWithActiveSubFlowAsync(instanceId, cancellationToken);

        if (instance is null)
            return;

        if (instance is { IsBusy: false, IsCompleted: false })
        {
            await MarkBusyCoreAsync(instance, cancellationToken);
        }

        await PropagateToSubflowAsync(instance, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<BusyMarkOutcome> TryMarkBusyWithPropagationAsync(
        Guid instanceId, CancellationToken cancellationToken = default)
    {
        var instance = await instanceRepository.FindWithActiveSubFlowAsync(instanceId, cancellationToken);

        if (instance is null || instance.IsCompleted)
            return BusyMarkOutcome.Skipped;

        if (instance.IsBusy)
            return BusyMarkOutcome.AlreadyBusy;

        await MarkBusyCoreAsync(instance, cancellationToken);
        await PropagateToSubflowAsync(instance, cancellationToken);

        return BusyMarkOutcome.Marked;
    }

    /// <inheritdoc />
    public async Task<bool> TryReleaseAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var result = await instanceRepository.GetResultAsync(
            instanceId.ToString(), includeDetails: false, cancellationToken);

        if (!result.IsSuccess || result.Value is null)
            return false;

        var instance = result.Value;
        if (!instance.IsBusy || instance.IsCompleted)
            return false;

        await using var uow = uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

        instance.Active();
        await instanceRepository.UpdateAsync(instance, false, cancellationToken);
        await uow.CommitAsync(cancellationToken);

        return true;
    }

    /// <summary>
    /// Persists the Busy flip for an already-loaded instance in an isolated RequiresNew transaction.
    /// </summary>
    private async Task MarkBusyCoreAsync(Instance instance, CancellationToken cancellationToken)
    {
        await using var uow = uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

        instance.Busy();
        await instanceRepository.UpdateAsync(instance, false, cancellationToken);
        await uow.CommitAsync(cancellationToken);

        logger.InstanceMarkedBusy(instance.Id);
    }

    /// <summary>
    /// Propagates the Busy mark to the active SubFlow (if any) via the instance command gateway.
    /// </summary>
    private async Task PropagateToSubflowAsync(Instance instance, CancellationToken cancellationToken)
    {
        var subflow = instance.Subflow;
        if (subflow is not null)
        {
            await instanceCommandGateway.MarkBusyAsync(new MarkBusyInput
            {
                Domain = subflow.SubFlowDomain,
                Workflow = subflow.SubFlowName,
                InstanceId = subflow.SubFlowInstanceId,
                Version = subflow.SubFlowVersion
            }, cancellationToken);
        }
    }
}
