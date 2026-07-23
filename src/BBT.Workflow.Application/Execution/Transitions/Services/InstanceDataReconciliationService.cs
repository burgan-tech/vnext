using System.Diagnostics;
using BBT.Aether.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Monitoring;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Transitions.Services;

public sealed class InstanceDataReconciliationService(
    IInstanceDataConcurrencyRepository repository,
    ILogger<InstanceDataReconciliationService> logger,
    IWorkflowMetrics metrics) : IInstanceDataReconciliationService
{
    internal const int MaxAttempts = 5;

    public async Task<Result<InstanceDataReconciliationResult>> ApplyAsync(
        Instance instance,
        InstanceDataChangeSet changeSet,
        CancellationToken cancellationToken)
    {
        if (changeSet.Contributions.Count == 0)
        {
            throw new InvalidOperationException(
                "Instance data reconciliation requires at least one contribution.");
        }

        var contributions = changeSet.Contributions.OrderBy(x => x.Order).ToArray();
        var head = GetValidatedInitialHead(instance, changeSet);

        // Observability context: the pipeline step and transition key are read from the
        // ambient Activity tags so the approved reconciliation interface stays unchanged;
        // "unknown" is the documented fallback when the tags are not set.
        var pipelineStep = Activity.Current?.GetTagItem("workflow.pipeline.step")?.ToString() ?? "unknown";
        var transitionKey = Activity.Current?.GetTagItem("workflow.transition.key")?.ToString() ?? "unknown";
        var startTimestamp = Stopwatch.GetTimestamp();
        var conflictCount = 0;

        void RecordMetric(string result, bool rebased, int attempts) =>
            metrics.RecordInstanceDataReconciliation(
                instance.Flow,
                pipelineStep,
                result,
                rebased,
                attempts,
                Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds,
                contributions.Length,
                conflictCount);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (attempt > 1)
                head = await repository.GetLatestDataHeadAsync(instance.Id, cancellationToken);

            var working = instance.CreateReconciliationSnapshot(head);
            if (attempt > 1 && IsCompleteBatchAlreadyApplied(head, contributions))
            {
                RecordMetric("applied", rebased: true, attempt);
                return Result<InstanceDataReconciliationResult>.Ok(
                    new InstanceDataReconciliationResult(
                        working.LatestData!,
                        [],
                        attempt,
                        true));
            }

            var appended = Replay(working, contributions);
            if (appended.Count == 0)
            {
                RecordMetric("no_change", rebased: attempt > 1, attempt);
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
            {
                RecordMetric("failed", rebased: attempt > 1, attempt);
                return Result<InstanceDataReconciliationResult>.Fail(appendResult.Error.Value);
            }

            if (appendResult.Status is ConditionalAppendStatus.Applied or ConditionalAppendStatus.NoChange)
            {
                var latestData = appendResult.LatestData ?? throw new InvalidOperationException(
                    $"Conditional append status '{appendResult.Status}' requires a latest data row.");
                RecordMetric(
                    appendResult.Status == ConditionalAppendStatus.Applied ? "applied" : "no_change",
                    rebased: attempt > 1,
                    attempt);
                return Result<InstanceDataReconciliationResult>.Ok(
                    new InstanceDataReconciliationResult(
                        latestData,
                        appendResult.AppendedData,
                        attempt,
                        attempt > 1));
            }

            if (appendResult.Status != ConditionalAppendStatus.Conflict)
            {
                throw new InvalidOperationException(
                    $"Unsupported conditional append status '{appendResult.Status}'.");
            }

            conflictCount++;
            logger.InstanceDataReconciliationConflict(
                instance.Id,
                head?.DataId ?? Guid.Empty,
                appendResult.ObservedHead?.DataId,
                attempt,
                contributions.Length,
                pipelineStep,
                transitionKey);
        }

        logger.InstanceDataReconciliationExhausted(
            instance.Id,
            MaxAttempts,
            contributions.Length,
            pipelineStep,
            transitionKey);
        RecordMetric("exhausted", rebased: true, MaxAttempts);

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
            if (!ReferenceEquals(after, before))
                appended.Add(after);
        }

        return appended;
    }

    private static bool IsCompleteBatchAlreadyApplied(
        InstanceDataHead? head,
        IReadOnlyList<InstanceDataContribution> orderedContributions)
    {
        if (head is null)
            return false;

        var last = orderedContributions[^1];
        return head.DataId == last.DataId &&
               string.Equals(head.Version, last.Version, StringComparison.Ordinal) &&
               head.HistorySequence == last.HistorySequence &&
               string.Equals(head.ETag, last.ETag, StringComparison.Ordinal) &&
               string.Equals(head.DataHash, last.DataHash, StringComparison.Ordinal) &&
               head.EnteredAt == last.EnteredAt;
    }

    private static InstanceDataHead? GetValidatedInitialHead(
        Instance instance,
        InstanceDataChangeSet changeSet)
    {
        if (changeSet.InstanceId != instance.Id)
        {
            throw new InvalidOperationException(
                $"Instance data change set '{changeSet.InstanceId}' does not belong to instance '{instance.Id}'.");
        }

        var latestData = instance.LatestData;
        if (changeSet.Baseline is null)
        {
            if (latestData is not null)
            {
                throw new InvalidOperationException(
                    "Instance data reconciliation expected the supplied instance to have no latest data.");
            }

            return null;
        }

        var baseline = changeSet.Baseline;
        if (latestData is null ||
            latestData.Id != baseline.DataId ||
            !string.Equals(latestData.ETag, baseline.ETag, StringComparison.Ordinal) ||
            !string.Equals(latestData.Version, baseline.Version, StringComparison.Ordinal) ||
            latestData.VersionNo != baseline.VersionNo)
        {
            throw new InvalidOperationException(
                "Instance data reconciliation baseline does not match the supplied instance latest data.");
        }

        return ToHead(latestData);
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
