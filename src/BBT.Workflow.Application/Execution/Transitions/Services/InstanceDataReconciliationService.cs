using BBT.Aether.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Execution.Transitions.Services;

public sealed class InstanceDataReconciliationService(
    IInstanceDataConcurrencyRepository repository) : IInstanceDataReconciliationService
{
    internal const int MaxAttempts = 5;

    public async Task<Result<InstanceDataReconciliationResult>> ApplyAsync(
        Instance instance,
        InstanceDataChangeSet changeSet,
        CancellationToken cancellationToken)
    {
        InstanceDataHead? head = changeSet.Baseline is null
            ? null
            : ToHead(instance.LatestData!);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (attempt > 1)
                head = await repository.GetLatestDataHeadAsync(instance.Id, cancellationToken);

            var working = instance.CreateReconciliationSnapshot(head);
            var appended = Replay(working, changeSet.Contributions);
            if (appended.Count == 0)
            {
                return Result<InstanceDataReconciliationResult>.Ok(
                    new InstanceDataReconciliationResult(
                        working.LatestData!,
                        [],
                        attempt,
                        attempt > 1));
            }

            var appendResult = await repository.TryAppendDataAsync(
                instance.Id,
                head?.DataId,
                head?.ETag,
                appended.Select(ToPrepared).ToArray(),
                cancellationToken);

            if (appendResult.Error is not null)
                return Result<InstanceDataReconciliationResult>.Fail(appendResult.Error.Value);

            if (appendResult.Status is ConditionalAppendStatus.Applied or ConditionalAppendStatus.NoChange)
            {
                return Result<InstanceDataReconciliationResult>.Ok(
                    new InstanceDataReconciliationResult(
                        appendResult.LatestData!,
                        appendResult.AppendedData,
                        attempt,
                        attempt > 1));
            }
        }

        return Result<InstanceDataReconciliationResult>.Fail(
            WorkflowErrors.InstanceDataConcurrencyConflict(instance.Id, MaxAttempts));
    }

    private static IReadOnlyList<InstanceData> Replay(
        Instance working,
        IReadOnlyList<InstanceDataContribution> contributions)
    {
        var appended = new List<InstanceData>();
        foreach (var contribution in contributions.OrderBy(x => x.Order))
        {
            var before = working.LatestData;
            var after = working.AddData(
                contribution.DataId,
                new JsonData(contribution.Input.Json),
                contribution.VersionStrategy);
            if (after.Id != before?.Id)
                appended.Add(after);
        }

        return appended;
    }

    private static InstanceDataHead ToHead(InstanceData data) =>
        new(
            data.Id,
            data.ETag,
            data.Version,
            data.VersionNo,
            data.HistorySequence,
            data.DataHash,
            new JsonData(data.Data.Json),
            data.EnteredAt);

    private static PreparedInstanceData ToPrepared(InstanceData data) =>
        new(
            data.Id,
            data.Version,
            data.HistorySequence,
            data.ETag,
            data.DataHash,
            new JsonData(data.Data.Json),
            data.EnteredAt,
            data.IsLatest);
}
