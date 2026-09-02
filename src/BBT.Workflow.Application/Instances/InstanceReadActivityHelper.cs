using System.Diagnostics;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Instances;

/// <summary>
/// Business-level spans for the instance READ path — specifically, the descent a built-in function
/// makes into an active subflow.
/// <para>
/// The write path got its span tree on 2026-08-25; the read path never had one. A built-in function
/// that walks down a subflow chain re-enters <see cref="IInstanceQueryAppService"/> once per level,
/// and — for a same-domain chain — does so without opening a single Activity: the local gateway
/// creates a DI scope, not a span. The whole descent therefore collapsed into the caller's server
/// span, taking every level's <c>Cache.*</c> and <c>Db.*</c> children with it.
/// </para>
/// <para>
/// The asymmetry that made this worth fixing: a CROSS-domain descent was already visible, because
/// Aether's HttpClient instrumentation draws the boundary. So the cheap in-process hop was the
/// invisible one and the expensive network hop was the traced one — exactly backwards.
/// </para>
/// </summary>
public static class InstanceReadActivityHelper
{
    /// <summary>
    /// The source name as a compile-time constant.
    /// <para>
    /// Anything identifying this source — a test's <c>ShouldListenTo</c> predicate above all — must
    /// use THIS, never <c>ActivitySource.Name</c>. Reading the field runs this class's static
    /// initializer, and <c>ActivitySource.AddActivityListener</c> invokes every registered predicate
    /// while it constructs sources: a predicate that touches the field re-enters the initializer that
    /// is still running, reads a null field, and poisons the type for the rest of the process. The
    /// symptom is tests that pass individually and fail together. A const is inlined and triggers
    /// nothing.
    /// </para>
    /// <para>
    /// Deliberately NOT <c>BBT.Workflow.Pipeline</c>: that name means the write path, and folding read
    /// spans into it would silently break every "show me pipeline spans" query and every duration
    /// aggregation built on one.
    /// </para>
    /// </summary>
    public const string SourceName = "BBT.Workflow.Instances.Read";

    /// <summary>
    /// ActivitySource for instance read operations. Hosts that register sources explicitly must list
    /// <see cref="SourceName"/> in <c>Otel:Tracing:AdditionalSources</c>; a source missing from one
    /// host's list goes dark in that host only, which is the failure mode this convention prevents.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(SourceName);

    /// <summary>One level of descent into an active subflow.</summary>
    public const string OperationDescend = "Subflow.Descend";

    /// <summary>
    /// Starts the descent span for one level.
    /// <para>
    /// Named <c>Subflow.Descend/{targetFlow}</c> — the subject goes in the name, per the convention
    /// <c>Cache.Get/{key}</c> and <c>Lock.Acquire/{key}</c> follow, so the ladder is readable without
    /// opening any span. The flow key is bounded cardinality; the instance id (which is not) stays a
    /// tag.
    /// </para>
    /// </summary>
    /// <param name="targetFlow">The child workflow being descended into.</param>
    /// <param name="depth">1-based descent level.</param>
    /// <param name="transport">
    /// <see cref="TelemetryConstants.DescentTransports.Local"/> or
    /// <see cref="TelemetryConstants.DescentTransports.Remote"/>. Read from the same domain-match
    /// predicate the gateway routes on, never re-derived — a tag that disagrees with the actual route
    /// is worse than no tag.
    /// </param>
    /// <param name="function">Which built-in function is descending (<c>state</c>, <c>view</c>, …).</param>
    public static Activity? StartDescend(string targetFlow, int depth, string transport, string function)
    {
        // IMPLICIT parent, deliberately. The explicit-ActivityContext overload sets ParentSpanId but
        // leaves Activity.Parent null, and baggage is inherited through the Activity CHAIN, not
        // through the context — so an explicitly-parented span silently severs baggage for everything
        // nested under it. That would drop the root-instance baggage the outbound cross-domain read
        // reads back out in CurrentUserForwardHeadersHelper, one level below this very span.
        var activity = ActivitySource.StartActivity(
            $"{OperationDescend}/{targetFlow}",
            ActivityKind.Internal);

        if (activity is null) return null;

        activity.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        activity.SetTag(TelemetryConstants.TagNames.SubflowDepth, depth);
        activity.SetTag(TelemetryConstants.TagNames.DescentTransport, transport);
        activity.SetTag(TelemetryConstants.TagNames.DescentFunction, function);
        return activity;
    }

    /// <summary>
    /// Opens a descent level: resolves the transport, starts the span, stamps the target and raises
    /// the ambient depth — the whole composition in one place.
    /// <para>
    /// Transport is read from the SAME predicate <c>RoutedInstanceQueryGateway</c> routes on
    /// (<c>IsDomainMatch</c>), never re-derived by the caller. Two services descend into subflows and
    /// a third could join them; a copy of this decision in each is how a tag ends up describing a
    /// route it no longer matches.
    /// </para>
    /// </summary>
    internal static SubflowDescentScope StartDescendScope(
        IRuntimeInfoProvider runtimeInfoProvider,
        string targetDomain,
        string targetFlow,
        string targetInstanceId,
        string? parentInstanceId,
        string function)
    {
        var transport = runtimeInfoProvider.IsDomainMatch(targetDomain)
            ? TelemetryConstants.DescentTransports.Local
            : TelemetryConstants.DescentTransports.Remote;

        var activity = StartDescend(targetFlow, SubflowDescentContext.NextDepth, transport, function);
        SetTarget(activity, targetDomain, targetFlow, targetInstanceId, parentInstanceId);

        // Depth is raised for the whole scope, not just the span: a nested read happens INSIDE the
        // gateway call the caller is about to make, and it reads the ambient depth to number itself.
        return SubflowDescentContext.Enter(activity);
    }

    /// <summary>
    /// Stamps the identity of the level this span covers.
    /// <para>
    /// <paramref name="instanceId"/> is the CHILD's — the instance whose work this span contains.
    /// <paramref name="parentInstanceId"/> is the caller's, so a reader can walk the ladder in either
    /// direction without joining across spans.
    /// </para>
    /// </summary>
    public static void SetTarget(
        Activity? activity,
        string? domain,
        string? flow,
        string? instanceId,
        string? parentInstanceId)
    {
        if (activity is null) return;

        activity.SetTag(TelemetryConstants.TagNames.Domain, domain);
        activity.SetTag(TelemetryConstants.TagNames.Flow, flow);
        activity.SetTag(TelemetryConstants.TagNames.InstanceId, instanceId);
        activity.SetTag(TelemetryConstants.TagNames.ParentInstanceId, parentInstanceId);
    }

    /// <summary>
    /// Marks a descent that did not produce a usable answer. Not an exception path — several descents
    /// legitimately degrade (a subflow view that resolves to null, a state read that falls back to the
    /// parent's transitions). Recording it keeps a silent fallback from looking like a successful
    /// descent that simply returned nothing.
    /// </summary>
    public static void SetUnresolved(Activity? activity, string reason)
    {
        activity?.SetTag(TelemetryConstants.TagNames.DescentOutcome, reason);
    }
}
