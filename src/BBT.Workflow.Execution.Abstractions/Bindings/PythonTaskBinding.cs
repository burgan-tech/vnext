using System.Text.Json;

namespace BBT.Workflow.Execution.Bindings;

/// <summary>
/// Serializable contract sent from Orchestration to the Python task invoker.
/// </summary>
public sealed class PythonTaskBinding
{
    public required string Script { get; init; }
    public string Location { get; init; } = "inline";
    public string? ExecutionMode { get; init; }
    public JsonElement? Input { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
}
