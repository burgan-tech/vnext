namespace BBT.Workflow.Definitions;

/// <summary>
/// Extension types
/// </summary>
public enum ExtensionType
{
    /// <summary>
    /// Extension that will work while recording samples are rotating in all streams.
    /// </summary>
    Global = 1,

    /// <summary>
    /// Extension that will work on all streams and when requesting recording samples.
    /// </summary>
    GlobalAndRequested = 2,

    /// <summary>
    /// Extension that will only work on the streams for which it is defined.
    /// </summary>
    DefinedFlows = 3,
    
    /// <summary>
    /// An extension that will only work on the streams it is defined for and when requested.
    /// </summary>
    DefinedFlowAndRequested = 4
}

/// <summary>
/// Extension scopes. Authored and persisted as the NUMERIC value (see the extension component
/// schema's <c>scope</c> enum), so these numbers are a wire contract — never renumber an existing
/// member.
/// </summary>
/// <remarks>
/// A fourth member, <c>GetHistoryTransition</c>, was declared here with the value 2 — the same value
/// as <see cref="GetAllInstances"/>. It was never referenced, never accepted by the component schema
/// (which allows 1, 2 and 3 only) and never handled by the scope switch, so no authored extension
/// could ever request it and no stored 2 ever meant it. It was removed rather than renumbered:
/// keeping it would have kept a duplicate value documenting a capability the runtime cannot express,
/// because the transitions-history read path does not run extensions at all. Reinstating the
/// capability is a feature (new value 4 + schema const + switch case + wiring that read path), not
/// an enum edit.
/// </remarks>
public enum ExtensionScope
{
    /// <summary>
    /// The entension works on {domain}/workflows/{workflow}/instances/{instance} endpoint
    /// </summary>
    GetInstance = 1,

    /// <summary>
    /// The entension works on  {domain}/workflows/{workflow}/instances endpoint
    /// </summary>
    GetAllInstances = 2,

    /// <summary>
    /// The entension works on  all get endpoints
    /// </summary>
    Everywhere = 3
}
