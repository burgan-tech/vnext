using System.Text.Json.Serialization;

namespace BBT.Workflow.Instances;

/// <summary>
/// Result of the authorize system function. Indicates whether the given role is allowed for the requested transition and/or privilege.
/// </summary>
public sealed class AuthorizeOutput
{
    /// <summary>
    /// Whether the role is allowed. DENY always wins; if no DENY match, then any ALLOW match yields true, else false.
    /// </summary>
    [JsonPropertyName("allowed")]
    public bool Allowed { get; set; }
}
