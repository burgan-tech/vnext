namespace BBT.Workflow.Scripting.Rules;

/// <summary>
/// Runtime environment subset exposed to Dynamic Expresso rules (read-only identifiers).
/// </summary>
public sealed class ExpressoRuntimeView
{
    /// <summary>Configured runtime domain.</summary>
    public string Domain { get; init; } = string.Empty;

    /// <summary>Runtime version string.</summary>
    public string Version { get; init; } = string.Empty;
}
