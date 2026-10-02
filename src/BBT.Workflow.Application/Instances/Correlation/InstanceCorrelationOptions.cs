using System.ComponentModel.DataAnnotations;

namespace BBT.Workflow.Instances.Correlation;

/// <summary>
/// Bounds for the instance-correlation tree walk (<c>…/functions/instance-correlation</c>).
/// </summary>
/// <remarks>
/// Validated at startup rather than at first use. Every bound here is a positive-integer loop or
/// semaphore bound on a PUBLIC read path, and the failure modes are not theoretical in this
/// codebase: <see cref="FanoutParallelism"/> reaches <c>Parallel.ForEachAsync</c>, which treats a
/// negative as UNBOUNDED; <see cref="MaxConcurrentHops"/> sizes a semaphore that 0 would deadlock;
/// and <see cref="MaxDescentDepth"/> is the only thing standing between a cyclic correlation graph
/// and an infinite walk. A boot failure is the only acceptable way for a bad value to surface.
/// </remarks>
public sealed class InstanceCorrelationOptions
{
    /// <summary>Configuration section: <c>Workflow:InstanceCorrelation</c>.</summary>
    public const string SectionName = "Workflow:InstanceCorrelation";

    /// <summary>
    /// How many correlation levels one request may walk before it stops and reports the remaining
    /// nodes unresolved.
    /// </summary>
    /// <remarks>
    /// A bound, not a guess at real nesting — domains run six levels today, so twenty leaves ample
    /// headroom. It is DEPTH, not a node count: a tree three levels deep with five hundred nodes
    /// passes, a chain twenty-one deep does not.
    /// It is also the walk's ONLY protection against a cycle: correlations are written at spawn
    /// time and cannot currently form one, but a post-hoc correlation write API
    /// (vnext-client-sdk-core#58 AB-20) would change that, and this read path must not be the thing
    /// that discovers it. Exceeding the bound is reported on the node
    /// (<c>Resolved = false</c>, <c>depth-exceeded</c>), never silently rendered as "no children".
    /// </remarks>
    [Range(1, 100, ErrorMessage = "MaxDescentDepth must be between 1 and 100")]
    public int MaxDescentDepth { get; set; } = 20;

    /// <summary>
    /// How many sibling hops ONE request expands concurrently. A hop is one (domain, flow) group of
    /// a level, so this is the width of the tree walk, not its depth.
    /// </summary>
    /// <remarks>
    /// Each branch holds its own unit of work and therefore its own pooled connection across the
    /// loads and, cross-domain, across a network round trip. Nested hops inside a branch inherit
    /// that unit of work, so connections are bounded by WIDTH, never by width × depth.
    /// </remarks>
    [Range(1, 256, ErrorMessage = "FanoutParallelism must be between 1 and 256")]
    public int FanoutParallelism { get; set; } = 8;

    /// <summary>
    /// Process-wide ceiling on concurrent hops across ALL in-flight requests.
    /// </summary>
    /// <remarks>
    /// <see cref="FanoutParallelism"/> bounds one request; nothing bounds their product, and that
    /// product is what meets the connection pool. The human-task fan-out learned this the
    /// expensive way — concurrent distinct callers exhausted the server's connection slots outright
    /// (<c>53300 sorry, too many clients already</c>), first at 20 callers and, after its scan was
    /// collapsed onto one connection, still at 80. This walk has the same shape and no
    /// single-flight to fall back on, so it gets the same ceiling. Sized ABOVE what one request can
    /// want, so an uncontended request keeps its full width and this binds only when requests
    /// coincide.
    /// </remarks>
    [Range(1, 1_000, ErrorMessage = "MaxConcurrentHops must be between 1 and 1000")]
    public int MaxConcurrentHops { get; set; } = 32;
}
