using BBT.Aether.Uow;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Infrastructure.Execution.Locks;

/// <summary>
/// Marks an instance as Busy immediately after lock acquisition,
/// using an isolated unit of work so the status is visible to concurrent readers
/// even before the pipeline's main UoW commits.
/// </summary>
public sealed class InstanceBusyMarker(
    IInstanceRepository instanceRepository,
    IUnitOfWorkManager unitOfWorkManager,
    ILogger<InstanceBusyMarker> logger) : IInstanceBusyMarker
{
    /// <inheritdoc />
    public async Task MarkBusyAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var instanceResult = await instanceRepository.GetResultAsync(
            instanceId.ToString(), includeDetails: false, cancellationToken);

        if (!instanceResult.IsSuccess || instanceResult.Value is null)
        {
            logger.LogWarning("Instance {InstanceId} not found for early Busy marker", instanceId);
            return;
        }

        var instance = instanceResult.Value;

        if (instance.IsBusy || instance.IsCompleted)
        {
            logger.LogDebug(
                "Instance {InstanceId} is already {Status}, skipping early Busy marker",
                instanceId, instance.Status.Description);
            return;
        }

        await using var innerUow = await unitOfWorkManager.BeginAsync(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew },
            cancellationToken);

        instance.Busy();
        await instanceRepository.UpdateAsync(instance, false, cancellationToken);
        await innerUow.CommitAsync(cancellationToken);

        logger.LogDebug(
            "Instance {InstanceId} marked Busy via early marker (pre-pipeline)",
            instanceId);
    }
}
