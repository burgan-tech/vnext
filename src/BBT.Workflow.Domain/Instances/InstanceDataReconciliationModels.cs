using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances;

public sealed record InstanceDataBaseline(Guid DataId, string ETag, string Version, long VersionNo);

public sealed record InstanceDataContribution(
    Guid DataId,
    JsonData Input,
    VersionStrategy VersionStrategy,
    int Order);

public sealed record InstanceDataChangeSet(
    Guid InstanceId,
    InstanceDataBaseline? Baseline,
    IReadOnlyList<InstanceDataContribution> Contributions);

public sealed record InstanceDataHead(
    Guid DataId,
    string ETag,
    string Version,
    long VersionNo,
    int HistorySequence,
    string DataHash,
    JsonData Data,
    DateTime EnteredAt);

public sealed record PreparedInstanceData(
    Guid DataId,
    string Version,
    int HistorySequence,
    string ETag,
    string DataHash,
    JsonData Data,
    DateTime EnteredAt,
    bool IsLatest);

public enum ConditionalAppendStatus { Applied, NoChange, Conflict }

public sealed record ConditionalAppendResult(
    ConditionalAppendStatus Status,
    InstanceData? LatestData,
    IReadOnlyList<InstanceData> AppendedData,
    BBT.Aether.Results.Error? Error = null,
    InstanceDataHead? ObservedHead = null);

public interface IInstanceDataConcurrencyRepository
{
    Task<InstanceDataHead?> GetLatestDataHeadAsync(Guid instanceId, CancellationToken cancellationToken);

    Task<ConditionalAppendResult> TryAppendDataAsync(
        Guid instanceId,
        Guid? expectedLatestDataId,
        string? expectedLatestEtag,
        IReadOnlyList<PreparedInstanceData> data,
        CancellationToken cancellationToken);
}
