namespace BBT.Workflow.Instances.History;

/// <summary>
/// Which recorded request headers transition history (<c>GET …/instances/{id}/transitions</c>) returns. Bound from
/// configuration section "Workflow:TransitionHistory:Headers". It shapes the RESPONSE only: the recorded header set and
/// what scripts read from <c>context.CurrentTransition.Header</c> are unchanged.
/// <para>
/// With both lists empty (the default) every recorded header is returned, as before this setting existed.
/// </para>
/// </summary>
public sealed class TransitionHistoryHeaderOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Workflow:TransitionHistory:Headers";

    /// <summary>The header lists applied when history is read.</summary>
    public TransitionHistoryHeaderLists Read { get; set; } = new();
}

/// <summary>
/// Allow and deny lists of header names, matched exactly and case-insensitively. A name in <see cref="Deny"/> is always
/// dropped; when <see cref="Allow"/> is non-empty only the names it lists are kept.
/// </summary>
public sealed class TransitionHistoryHeaderLists
{
    /// <summary>When non-empty, the only header names returned (minus <see cref="Deny"/>).</summary>
    public List<string> Allow { get; set; } = [];

    /// <summary>Header names never returned; wins over <see cref="Allow"/>.</summary>
    public List<string> Deny { get; set; } = [];
}
