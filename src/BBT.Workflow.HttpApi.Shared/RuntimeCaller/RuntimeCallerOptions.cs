using BBT.Workflow.Execution.RuntimeCaller;

namespace BBT.Workflow.RuntimeCaller;

/// <summary>
/// The runtime-caller identity a host proves its own calls with (vnext#1265). Bound from
/// <c>Workflow:RuntimeCaller</c> on the Orchestration, Execution and Inbox hosts. Every key is
/// optional: with <see cref="KeyFile"/> unset the host neither signs nor verifies, and the gate
/// (<c>Workflow:SubEndpointGate</c>) cannot leave <c>Off</c>.
/// </summary>
public sealed class RuntimeCallerOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Workflow:RuntimeCaller";

    /// <summary>
    /// Path to the key-ring file (a read-only mounted Kubernetes Secret), see
    /// <see cref="RuntimeCallerKeyRing.Parse"/>. Configuration carries only the path: key material in
    /// configuration fails startup, because flow scripts can read configuration.
    /// </summary>
    public string? KeyFile { get; set; }

    /// <summary>
    /// When true, the host refuses to start with a blank <see cref="KeyFile"/>. Set it together with the
    /// mount so a host that lost its configuration cannot silently send unsigned calls. A set
    /// <see cref="KeyFile"/> must always load: a missing, unreadable or invalid file fails startup whatever this says.
    /// </summary>
    public bool Required { get; set; }

    /// <summary>Accepted distance, in seconds, between a token's timestamp and this host's clock (60–900).</summary>
    public int MaxClockSkewSeconds { get; set; } = RuntimeCallerToken.DefaultMaxClockSkewSeconds;

    /// <summary>
    /// Lets a host that runs flow scripts load a key ring with the script sandbox off. Without the
    /// sandbox a script can read the key file, so this logs Critical at every start; whether it may be
    /// used at all is a Chair decision (docs/runtime/sub-endpoint-gate.md § Key ring).
    /// </summary>
    public bool AllowWithoutScriptSandbox { get; set; }
}
