using BBT.Aether.Uow;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.SubFlow;

/// <inheritdoc cref="IInstanceBusyPropagationService" />
public sealed class InstanceBusyPropagationService(
    IUnitOfWorkManager uowManager,
    IInstanceRepository instanceRepository,
    IInstanceCommandGateway instanceCommandGateway,
    ILogger<InstanceBusyPropagationService> logger)
    : IInstanceBusyPropagationService
{
    /// <inheritdoc />
    public async Task MarkBusyAsync(
        MarkBusyInput input,
        CancellationToken cancellationToken = default)
    {
        await using var uow = uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });

        var instance = await instanceRepository.FindWithActiveSubFlowAsync(
            input.InstanceId, cancellationToken);

        if (instance is null)
            return;

        if (instance is { IsBusy: false, IsCompleted: false })
        {
            instance.Busy();
            await instanceRepository.UpdateAsync(instance, false, cancellationToken);
        }

        await uow.CommitAsync(cancellationToken);

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
