namespace BBT.Workflow.Gateway;

/// <summary>
/// Answer of a busy-propagation call: the status a client polling the level that was marked would
/// observe once the walk reached the bottom of its SubFlow chain.
/// </summary>
/// <remarks>
/// It exists so the synchronous top-down walk can carry the LEAF's resulting status back up the
/// call stack and stamp every ancestor's <c>EffectiveStatus</c> with it. The value matters exactly
/// where it is easy to assume: when the leaf could not be marked (already terminal, or gone between
/// the load and the flip), the ancestors must record what the leaf actually is rather than an
/// assumed Busy.
/// <para>
/// <c>EffectiveStatusCode</c> is null when the far side did not report one — an older runtime on the
/// other side of a cross-domain hop, or nothing to report. A null must be treated as "unknown, write
/// nothing", never as Busy.
/// </para>
/// </remarks>
public sealed record MarkBusyOutput
{
    /// <summary>Single-character instance status code (see <c>InstanceStatus.Code</c>), or null.</summary>
    public string? EffectiveStatusCode { get; init; }

    /// <summary>Nothing to report — used for a no-op propagation.</summary>
    public static MarkBusyOutput None { get; } = new();
}
