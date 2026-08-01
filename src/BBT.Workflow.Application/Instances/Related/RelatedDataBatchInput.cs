namespace BBT.Workflow.Instances.Related;

/// <summary>
/// Body of the internal batched related-data read. All ids must belong to the routed domain and flow.
/// </summary>
public sealed class RelatedDataBatchInput
{
    /// <summary>Instance identifiers to read. Ids that do not resolve are omitted from the response.</summary>
    public IReadOnlyList<Guid> InstanceIds { get; init; } = [];
}
