using System.Text;

namespace BBT.Workflow.Telemetry;

/// <summary>
/// Builds the names of the two transition-hop spans, so a trace shows WHICH transition a hop ran
/// rather than just that some hop ran.
/// <para>
/// Both spans used to be named by their prefix alone, which made a chain of five hops five
/// identically-named spans: readable only by opening each one and reading its tags. The name now
/// carries <c>{prefix}/{domain}/{flow}/{transition}</c>, following the convention the
/// <c>SubFlow.*</c> spans already use (<c>SubFlow.Forward/{domain}/{flow}/{transitionKey}</c>).
/// </para>
/// <para>
/// All three segments are DEFINITION-level identifiers — a domain, a workflow key, a transition key
/// — so the name stays low-cardinality and safe as an APM transaction name. Never append anything
/// per-instance (instance id, correlation id, job name): apm-server groups transactions by name, and
/// an unbounded name turns one transaction into millions. Those belong in tags, where they already
/// are.
/// </para>
/// </summary>
public static class TransitionSpanName
{
    /// <summary>
    /// Prefix for a hop that ran as its own scheduler job (<c>AutoTransitionMode.Scheduled</c>, and
    /// every async accept's first hop).
    /// </summary>
    public const string JobPrefix = "TransitionJob.Execute";

    /// <summary>
    /// Prefix for a hop that ran in-process inside an already-executing job
    /// (<c>AutoTransitionMode.Inline</c>).
    /// </summary>
    public const string HopPrefix = "Transition.Hop";

    /// <summary>
    /// Builds <c>{prefix}/{domain}/{flow}/{transition}</c>.
    /// <para>
    /// Missing or blank segments are skipped rather than emitted as empty ones: a name like
    /// <c>TransitionJob.Execute//flow/go</c> reads as a bug in the tracing code and would split the
    /// transaction group in two. Truncating at the first gap keeps whatever prefix is meaningful.
    /// </para>
    /// </summary>
    /// <param name="prefix">One of <see cref="JobPrefix"/> / <see cref="HopPrefix"/>.</param>
    /// <param name="domain">Owning domain.</param>
    /// <param name="flow">Workflow key.</param>
    /// <param name="transitionKey">Transition key being executed.</param>
    public static string Build(string prefix, string? domain, string? flow, string? transitionKey)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return prefix;

        var builder = new StringBuilder(prefix).Append('/').Append(domain);

        if (string.IsNullOrWhiteSpace(flow))
            return builder.ToString();

        builder.Append('/').Append(flow);

        if (string.IsNullOrWhiteSpace(transitionKey))
            return builder.ToString();

        return builder.Append('/').Append(transitionKey).ToString();
    }
}
