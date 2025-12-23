using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Represents a SubFlow (child workflow) configuration within a state.
/// Contains process reference, mapping, error handling boundary, and error propagation policies.
/// </summary>
public sealed class SubFlow
{
    private SubFlow()
    {
    }

    [JsonConstructor]
    private SubFlow(
        SubFlowType type,
        Reference process,
        ScriptCode mapping,
        Dictionary<string, Reference>? viewOverrides,
        ErrorBoundary? errorBoundary,
        SubFlowErrorPolicy? errorPolicy)
    {
        Type = type;
        Process = process;
        Mapping = mapping;
        ViewOverrides = viewOverrides;
        ErrorBoundary = errorBoundary;
        ErrorPolicy = errorPolicy;
    }

    /// <summary>
    /// The type of SubFlow (SubFlow or SubProcess).
    /// </summary>
    public SubFlowType Type { get; private set; }

    /// <summary>
    /// Reference to the child workflow process.
    /// </summary>
    public Reference Process { get; private set; }

    /// <summary>
    /// Mapping script for data transformation between parent and child.
    /// </summary>
    public ScriptCode Mapping { get; private set; }

    /// <summary>
    /// Optional view overrides for the child workflow.
    /// </summary>
    public Dictionary<string, Reference>? ViewOverrides { get; private set; }

    /// <summary>
    /// Error boundary for this SubFlow.
    /// Defines how errors occurring during SubFlow execution should be handled.
    /// This is applied before error propagation to parent is considered.
    /// </summary>
    [JsonPropertyName("errorBoundary")]
    public ErrorBoundary? ErrorBoundary { get; private set; }

    /// <summary>
    /// Error propagation policy for this SubFlow.
    /// Controls how errors in the child workflow affect the parent workflow.
    /// Applied after local error boundary handling.
    /// </summary>
    [JsonPropertyName("errorPolicy")]
    public SubFlowErrorPolicy? ErrorPolicy { get; private set; }

    [NotMapped]
    [JsonIgnore]
    public bool HasViewOverrides => this.ViewOverrides != null;

    /// <summary>
    /// Indicates whether this SubFlow has an error boundary defined.
    /// </summary>
    [NotMapped]
    [JsonIgnore]
    public bool HasErrorBoundary => ErrorBoundary != null;

    /// <summary>
    /// Gets the effective error policy, using defaults if not configured.
    /// </summary>
    [NotMapped]
    [JsonIgnore]
    public SubFlowErrorPolicy EffectiveErrorPolicy => ErrorPolicy ?? SubFlowErrorPolicy.Default;

    /// <summary>
    /// Creates a new SubFlow instance.
    /// </summary>
    /// <param name="type">The SubFlow type.</param>
    /// <param name="reference">Reference to the child workflow.</param>
    /// <param name="mapping">Data mapping script.</param>
    /// <param name="viewOverrides">Optional view overrides.</param>
    /// <param name="errorBoundary">Optional error boundary for SubFlow execution.</param>
    /// <param name="errorPolicy">Optional error propagation policy.</param>
    public static SubFlow Create(
        string type,
        IReference reference,
        ScriptCode mapping,
        Dictionary<string, Reference>? viewOverrides,
        ErrorBoundary? errorBoundary = null,
        SubFlowErrorPolicy? errorPolicy = null)
    {
        return new SubFlow(
            SubFlowType.FromCode(type),
            reference.ToReference(),
            mapping,
            viewOverrides,
            errorBoundary,
            errorPolicy);
    }

    /// <summary>
    /// Sets the error boundary for this SubFlow.
    /// </summary>
    /// <param name="errorBoundary">The error boundary configuration.</param>
    public void SetErrorBoundary(ErrorBoundary errorBoundary)
    {
        ErrorBoundary = errorBoundary;
    }

    /// <summary>
    /// Sets the error propagation policy for this SubFlow.
    /// </summary>
    /// <param name="errorPolicy">The error policy configuration.</param>
    public void SetErrorPolicy(SubFlowErrorPolicy errorPolicy)
    {
        ErrorPolicy = errorPolicy;
    }
}
