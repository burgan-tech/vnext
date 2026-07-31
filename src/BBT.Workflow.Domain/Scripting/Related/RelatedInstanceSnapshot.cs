namespace BBT.Workflow.Scripting.Related;

/// <summary>
/// Raw read result for a related instance, as produced by <see cref="IRelatedInstanceReader"/>.
/// Contains only facts owned by the target instance — correlation facts are added later by the
/// accessor, which owns the relationship record.
/// </summary>
public sealed class RelatedInstanceSnapshot
{
    /// <summary>The related instance identifier.</summary>
    public Guid InstanceId { get; init; }

    /// <summary>Business key of the related instance, when it has one.</summary>
    public string? Key { get; init; }

    /// <summary>Domain that owns the related instance.</summary>
    public string Domain { get; init; } = string.Empty;

    /// <summary>Workflow key of the related instance.</summary>
    public string Flow { get; init; } = string.Empty;

    /// <summary>Workflow version of the related instance.</summary>
    public string? FlowVersion { get; init; }

    /// <summary>Instance status code: A, B, C, F or P.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Current state key of the related instance.</summary>
    public string? CurrentState { get; init; }

    /// <summary>True when the related instance itself reached a completed terminal status.</summary>
    public bool IsCompleted { get; init; }

    /// <summary>Latest instance data, unfiltered by x-roles and without extensions.</summary>
    public dynamic? Data { get; init; }
}
