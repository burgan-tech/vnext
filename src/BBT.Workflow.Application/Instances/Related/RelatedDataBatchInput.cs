namespace BBT.Workflow.Instances.Related;

/// <summary>
/// Body of the internal batched related-data read. All ids must belong to the routed domain and flow.
/// </summary>
public sealed class RelatedDataBatchInput
{
    /// <summary>
    /// Upper bound on ids accepted in one request. This is an abuse guard, not the feature's cap —
    /// RelatedAccessOptions.MaxResolutionsPerContext is the real limit and lives in the calling
    /// runtime, which this endpoint cannot trust. Deliberately far above any legitimate batch so
    /// raising the caller-side cap never trips it.
    /// </summary>
    public const int MaxInstanceIds = 100;

    /// <summary>Instance identifiers to read. Ids that do not resolve are omitted from the response.</summary>
    public IReadOnlyList<Guid> InstanceIds { get; init; } = [];
}
