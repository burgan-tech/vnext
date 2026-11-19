using BBT.Workflow.Execution.RuntimeCaller;

namespace BBT.Workflow.Authorization.SubEndpoints;

/// <summary>What the sub-endpoint gate does with a call it would refuse.</summary>
public enum SubEndpointGateMode
{
    /// <summary>No gate: the endpoint behaves exactly as without this feature.</summary>
    Off,

    /// <summary>Decide and log <c>would refuse</c>, but admit. Used to prove every runtime caller signs.</summary>
    Observe,

    /// <summary>Refuse with 403 <c>Authorization:110001</c> before the request body is bound or anything is written.</summary>
    Enforce
}

/// <summary>
/// The domain-level gate on the runtime-internal <c>sub/instances/start</c> and
/// <c>instances/{instance}/complete</c> endpoints (vnext#1265), bound from
/// <c>Workflow:SubEndpointGate</c> on the Orchestration host.
/// <para>
/// A call carrying a valid runtime-caller token is admitted before any grant is read. Any other
/// caller is decided by <see cref="Grants"/> with the shared role-grant rule. "Runtime only" is
/// <c>[{ "role": "$Everyone", "grant": "deny" }]</c>.
/// </para>
/// </summary>
public sealed class SubEndpointGateOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Workflow:SubEndpointGate";

    /// <summary>Mode for both endpoints. Default <see cref="SubEndpointGateMode.Off"/>.</summary>
    public SubEndpointGateMode Mode { get; set; } = SubEndpointGateMode.Off;

    /// <summary>
    /// Mode for <c>complete</c> only, overriding <see cref="Mode"/>. A refused cross-domain completion
    /// has no durable backup and strands the parent, so an operator can enforce <c>sub-start</c> while
    /// keeping <c>complete</c> in <see cref="SubEndpointGateMode.Observe"/>.
    /// </summary>
    public SubEndpointGateMode? CompleteMode { get; set; }

    /// <summary>
    /// Who may call without a runtime-caller token. Single roles or <c>allOf</c> / <c>anyOf</c> of static
    /// roles, and <c>$Everyone</c>; identity and instance-bound leaves are rejected at startup. Required
    /// (non-empty) whenever a mode is not <see cref="SubEndpointGateMode.Off"/>.
    /// </summary>
    public List<SubEndpointGrantOptions> Grants { get; set; } = [];

    /// <summary>The effective mode for <paramref name="operation"/>.</summary>
    public SubEndpointGateMode ModeFor(string operation)
        => operation == RuntimeCallOperations.Complete ? CompleteMode ?? Mode : Mode;

    /// <summary>Whether any operation is gated.</summary>
    public bool IsEnabled => Mode != SubEndpointGateMode.Off || CompleteMode is not (null or SubEndpointGateMode.Off);
}

/// <summary>One grant of <see cref="SubEndpointGateOptions.Grants"/>; same shape as a definition's role grant.</summary>
public sealed class SubEndpointGrantOptions
{
    /// <summary>A single role. Exactly one of <see cref="Role"/>, <see cref="AllOf"/>, <see cref="AnyOf"/>.</summary>
    public string? Role { get; set; }

    /// <summary><c>allow</c> or <c>deny</c>.</summary>
    public string? Grant { get; set; }

    /// <summary>All of these roles.</summary>
    public List<SubEndpointGrantConditionOptions>? AllOf { get; set; }

    /// <summary>Any of these roles.</summary>
    public List<SubEndpointGrantConditionOptions>? AnyOf { get; set; }
}

/// <summary>One role of an <c>allOf</c> / <c>anyOf</c> combinator.</summary>
public sealed class SubEndpointGrantConditionOptions
{
    /// <summary>The role.</summary>
    public string? Role { get; set; }
}
