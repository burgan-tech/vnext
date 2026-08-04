using System.Text.Json;

namespace BBT.Workflow.Functions.DTOs;

/// <summary>
/// The schema a function's <c>inputSchema</c> or <c>outputSchema</c> slot resolved to for this
/// request. Mirrors the state-level schema function payload so clients can reuse the same handling.
/// </summary>
public sealed class FunctionSchemaOutput
{
    /// <summary>The schema component key.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The schema type.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>The JSON Schema document.</summary>
    public JsonElement Schema { get; set; }
}
