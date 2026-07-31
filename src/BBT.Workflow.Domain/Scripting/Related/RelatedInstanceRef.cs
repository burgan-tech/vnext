namespace BBT.Workflow.Scripting.Related;

/// <summary>
/// Address of a related workflow instance. Carries everything the reader needs to route the read
/// (domain for local-vs-remote dispatch, flow for schema resolution) without a lookup.
/// </summary>
/// <param name="InstanceId">The related instance identifier.</param>
/// <param name="Domain">The domain that owns the related instance.</param>
/// <param name="Flow">The workflow key of the related instance.</param>
/// <param name="FlowVersion">The workflow version, when known.</param>
public sealed record RelatedInstanceRef(
    Guid InstanceId,
    string Domain,
    string Flow,
    string? FlowVersion);
