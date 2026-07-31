using System;

namespace BBT.Workflow.Scripting.Related;

/// <summary>
/// Script-facing projection of a related instance. Combines the read snapshot with the correlation
/// facts the current instance owns.
/// </summary>
/// <remarks>
/// <see cref="IsCompleted"/> (target instance status) and <see cref="CorrelationCompleted"/>
/// (relationship closed) are separate on purpose: a subflow instance can be Completed while the
/// parent correlation is still open — the subflow completion window. Conflating them produces wrong
/// decisions.
/// </remarks>
public sealed class RelatedInstanceView
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

    /// <summary>
    /// Whether the correlation linking this instance to the current one is closed.
    /// Null for the parent direction, where no correlation is involved.
    /// </summary>
    public bool? CorrelationCompleted { get; init; }

    /// <summary>
    /// Correlation terminal outcome name (Completed / Faulted / Canceled).
    /// Null for the parent direction, and null while the correlation is open.
    /// </summary>
    public string? TerminalOutcome { get; init; }

    /// <summary>
    /// "S" (SubFlow) or "P" (SubProcess) for the down direction. Null for the parent.
    /// </summary>
    public string? SubFlowType { get; init; }

    /// <summary>Latest instance data, unfiltered by x-roles and without extensions.</summary>
    public dynamic? Data { get; init; }
}
