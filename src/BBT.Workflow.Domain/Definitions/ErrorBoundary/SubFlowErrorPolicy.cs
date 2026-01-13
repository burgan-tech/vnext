using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Configures error propagation behavior for SubFlow (child workflow) errors.
/// Controls how errors in child workflows affect parent workflows.
/// </summary>
/// <remarks>
/// Note: SubFlow error handling implementation is deferred to a future release.
/// This class provides the definition structure only.
/// </remarks>
public sealed record SubFlowErrorPolicy
{
    /// <summary>
    /// Whether to propagate errors from child SubFlow to parent workflow.
    /// If false, child handles errors internally and parent receives only completion status.
    /// If true, parent's error boundary will be invoked for child errors.
    /// </summary>
    [JsonPropertyName("propagateToParent")]
    public bool PropagateToParent { get; init; } = true;

    /// <summary>
    /// Whether to include detailed error information from child in parent's error context.
    /// May be disabled for PII/security concerns or to reduce payload size.
    /// </summary>
    [JsonPropertyName("includeChildErrorDetails")]
    public bool IncludeChildErrorDetails { get; init; } = true;

    /// <summary>
    /// Optional transition key in parent workflow to trigger when child fails.
    /// Only used when PropagateToParent is true.
    /// </summary>
    [JsonPropertyName("parentTransition")]
    public string? ParentTransition { get; init; }

    /// <summary>
    /// Creates a default SubFlow error policy that propagates errors to parent.
    /// </summary>
    public static SubFlowErrorPolicy Default => new();

    /// <summary>
    /// Creates a SubFlow error policy that isolates child errors from parent.
    /// </summary>
    public static SubFlowErrorPolicy Isolated => new()
    {
        PropagateToParent = false,
        IncludeChildErrorDetails = false
    };
}

