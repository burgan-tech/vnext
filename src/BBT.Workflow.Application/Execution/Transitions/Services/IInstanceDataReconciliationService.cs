using BBT.Aether.Results;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Execution.Transitions.Services;

public interface IInstanceDataReconciliationService
{
    Task<Result<InstanceDataReconciliationResult>> ApplyAsync(
        Instance instance,
        InstanceDataChangeSet changeSet,
        CancellationToken cancellationToken);
}

public sealed record InstanceDataReconciliationResult(
    InstanceData LatestData,
    IReadOnlyList<InstanceData> AppendedData,
    int AttemptCount,
    bool WasRebased);
