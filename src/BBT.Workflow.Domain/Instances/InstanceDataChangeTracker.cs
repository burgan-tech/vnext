using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances;

internal sealed class InstanceDataChangeTracker(InstanceData? baseline)
{
    private readonly List<InstanceDataContribution> _contributions = [];
    private InstanceDataBaseline? _baseline = baseline is null ? null : ToBaseline(baseline);

    public void Record(Guid id, JsonData input, VersionStrategy strategy) =>
        _contributions.Add(new(id, new JsonData(input.Json), strategy, _contributions.Count));

    public InstanceDataChangeSet? GetChangeSet(Guid instanceId) =>
        _contributions.Count == 0 ? null : new(instanceId, _baseline, _contributions.ToArray());

    public void Acknowledge(InstanceData latest)
    {
        _contributions.Clear();
        _baseline = ToBaseline(latest);
    }

    private static InstanceDataBaseline ToBaseline(InstanceData data) =>
        new(data.Id, data.ETag, data.Version, data.VersionNo);
}
