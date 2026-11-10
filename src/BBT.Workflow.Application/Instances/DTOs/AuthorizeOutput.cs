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

    /// <summary>
    /// Why the answer is not a grant evaluation, when it is not; absent otherwise. Today the only value is
    /// <see cref="Reasons.InstanceNotFound"/>: the addressed instance could not be loaded, so no grant was
    /// evaluated and <see cref="Allowed"/> is <c>false</c>.
    /// <para>
    /// A parent that descended to an active SubFlow reads it to tell "the leaf refused" from "the leaf
    /// does not exist yet" — the window between the parent committing its SubFlow correlation and the
    /// child's row being created (vnext#1181). It travels in the response body so the remote (cross-domain)
    /// descent carries it too; an older runtime omits it and keeps the previous behaviour.
    /// </para>
    /// </summary>
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }

    /// <summary>Values of <see cref="Reason"/>.</summary>
    public static class Reasons
    {
        /// <summary>The addressed instance could not be loaded; no grant was evaluated.</summary>
        public const string InstanceNotFound = "instance-not-found";
    }
}
