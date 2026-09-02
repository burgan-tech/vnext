using System.Diagnostics;

namespace BBT.Workflow.Instances;

/// <summary>
/// Ambient descent depth for a built-in function walking down a subflow chain.
/// <para>
/// An <see cref="AsyncLocal{T}"/> rather than a parameter threaded through the read services: the
/// descent re-enters <see cref="IInstanceQueryAppService"/> through the gateway, which resolves a
/// fresh instance from a new DI scope. A constructor-injected or parameter-passed counter would have
/// to be plumbed through every gateway method signature and every DTO to survive that hop; the async
/// context survives it for free, because the local gateway creates a DI scope, not a new async flow.
/// </para>
/// <para>
/// <b>Cross-domain hops are bridged by a header.</b> The async context does not survive leaving the
/// process, so <c>CurrentUserForwardHeadersHelper</c> stamps <c>X-Subflow-Depth</c> from
/// <see cref="Current"/> on the way out and <c>ParentInstanceIdEnrichmentMiddleware</c> calls
/// <see cref="Seed"/> on the way in. Without that bridge a mixed chain reports 1, 1, 2 instead of
/// 1, 2, 3.
/// </para>
/// </summary>
public static class SubflowDescentContext
{
    private static readonly AsyncLocal<int> CurrentDepth = new();

    /// <summary>
    /// Depth of the descent currently in progress; 0 when the caller is at the top level and has not
    /// descended yet.
    /// </summary>
    public static int Current => CurrentDepth.Value;

    /// <summary>The depth a descent starting right now would occupy.</summary>
    public static int NextDepth => CurrentDepth.Value + 1;

    /// <summary>
    /// Seeds the depth for a level entered from outside this process, so a cross-domain hop continues
    /// the ladder instead of restarting it. Ignored when <paramref name="depth"/> is not positive —
    /// an absent or malformed header must degrade to today's behaviour, never to a negative depth.
    /// </summary>
    public static void Seed(int depth)
    {
        if (depth > 0)
            CurrentDepth.Value = depth;
    }

    /// <summary>
    /// Enters one descent level and returns a scope that restores the previous depth on dispose.
    /// Restoring rather than decrementing matters on the exception path: a descent that throws must
    /// not leave the counter raised for the sibling reads that follow it.
    /// </summary>
    internal static SubflowDescentScope Enter(Activity? activity)
    {
        var previous = CurrentDepth.Value;
        CurrentDepth.Value = previous + 1;
        return new SubflowDescentScope(activity, previous);
    }

    internal static void Restore(int depth) => CurrentDepth.Value = depth;
}

/// <summary>
/// Owns one descent level: the span and the ambient depth, disposed together.
/// <para>
/// A single scope for both so a call site cannot dispose one and forget the other — the failure that
/// would produce is a permanently raised depth counter, which is invisible until the numbers in a
/// trace stop making sense.
/// </para>
/// </summary>
internal readonly struct SubflowDescentScope(Activity? activity, int previousDepth) : IDisposable
{
    /// <summary>The descent span, or null when nothing is listening.</summary>
    internal Activity? Activity { get; } = activity;

    public void Dispose()
    {
        SubflowDescentContext.Restore(previousDepth);
        Activity?.Dispose();
    }
}
