namespace BBT.Workflow.Scripting.Rules;

/// <summary>
/// Read-only workflow definition subset for rule expressions.
/// </summary>
public sealed class ExpressoWorkflowView
{
    /// <summary>Workflow key.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Domain key.</summary>
    public string Domain { get; init; } = string.Empty;

    /// <summary>Flow schema segment.</summary>
    public string Flow { get; init; } = string.Empty;

    /// <summary>Semantic version.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>State keys defined on the workflow.</summary>
    public IReadOnlyList<string> StateKeys { get; init; } = Array.Empty<string>();
}
