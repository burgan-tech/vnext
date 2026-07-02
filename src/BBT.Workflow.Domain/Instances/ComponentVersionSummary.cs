namespace BBT.Workflow.Instances;

/// <summary>
/// Slim projection of a single published component version,
/// used by the monitoring version-list query.
/// Only the fields needed for display are selected; the data blob is not loaded.
/// </summary>
public sealed record ComponentVersionSummary(
    string   Version,
    bool     IsLatest,
    string?  FlowVersion,
    DateTime PublishedAt);
