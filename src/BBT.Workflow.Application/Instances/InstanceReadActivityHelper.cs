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
    public const string SourceName = TelemetryConstants.ActivitySources.InstancesRead;

    /// <summary>
    /// ActivitySource for instance read operations. Hosts that register sources explicitly must list
    /// <see cref="SourceName"/> in <c>Otel:Tracing:AdditionalSources</c>; a source missing from one
    /// host's list goes dark in that host only, which is the failure mode this convention prevents.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(SourceName);

    /// <summary>Starts a bounded list-read phase; callers never include raw filters or attribute values.</summary>
    public static Activity? StartListPhase(string phase)
    {
        var activity = ActivitySource.StartActivity($"Instances.List.{phase}", ActivityKind.Internal);
        activity?.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        return activity;
    }

    /// <summary>The read was answered 304 from the fingerprint projection alone.</summary>
    public const string FastPathNotModified = "notModified";

    /// <summary>The read was answered from the cached response body.</summary>
    public const string FastPathCacheHit = "cacheHit";

    /// <summary>The read fell through and built the response.</summary>
    public const string FastPathBuild = "build";

    /// <summary>The response cache is disabled, so this read always builds.</summary>
    public const string FastPathDisabled = "disabled";

    /// <summary>
    /// Records which read ran and how it was answered — on the TRANSACTION, at zero span documents.
    /// <para>
    /// This is the hot path's whole design. The state function is the highest-QPS route in the
    /// runtime and its 304 branch returns before any cache read, aggregate load or response build,
    /// so today it costs one transaction document plus a single <c>Db.SELECT</c>. Opening an
    /// envelope there would be a permanent +50&#160;% on documents for that route, to record that
    /// nothing happened — and the binding objection is not cost but fidelity: no sampler is
    /// configured in any host, so with untuned OpenTelemetry defaults a burst drops spans
    /// indiscriminately, including the pipeline spans this tree exists to protect.
    /// </para>
    /// <para>
    /// Written on EVERY branch, including the ones that go on to open an envelope. Tagging only the
    /// fast path would put the two outcomes on different document types and force every query to
    /// OR across a transaction tag and a span tag to answer one question.
    /// </para>
    /// </summary>
    /// <param name="transaction">
    /// The ambient activity captured at entry — the ASP.NET server span, since no vNext span is open
    /// yet. Passed in rather than read here so it cannot accidentally pick up an envelope opened later.
    /// </param>
    public static void SetReadOutcome(Activity? transaction, string kind, string outcome)
    {
        if (transaction is null) return;

        transaction.SetTag(TelemetryConstants.TagNames.FunctionKey, kind);
        transaction.SetTag(TelemetryConstants.TagNames.ReadFastPath, outcome);
    }

    /// <summary>Operation name for the human-task candidate scan of one workflow schema.</summary>
    public const string OperationHumanTaskScan = "HumanTask.Scan";

    /// <summary>Operation name for one workflow schema's whole leaf descent.</summary>
    public const string OperationHumanTaskDescend = "HumanTask.Descend";

    /// <summary>Operation name for leaf-side authorization at one level of a descent.</summary>
    public const string OperationHumanTaskAuthorize = "HumanTask.Authorize";

    /// <summary>
    /// Starts the span covering one workflow schema's candidate scan — the query that answers
    /// "which roots of this flow are waiting", and nothing else.
    /// <para>
    /// The fan-out opens one of these per schema, in parallel, so the database work of a branch is
    /// attributable to that branch: without it every schema's <c>Db.*</c> child hangs directly off
    /// the read envelope and a slow schema is indistinguishable from a slow endpoint. The selection
    /// and the descent are separately timed on purpose — they answer different questions and have
    /// completely different cost shapes.
    /// </para>
    /// </summary>
    public static Activity? StartHumanTaskScan(string flow)
    {
        var activity = ActivitySource.StartActivity($"{OperationHumanTaskScan}/{flow}", ActivityKind.Internal);
        if (activity is null) return null;

        activity.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        activity.SetTag(TelemetryConstants.TagNames.Flow, flow);
        return activity;
    }

    /// <summary>
    /// Starts the span covering one workflow schema's entire leaf descent: every level, local and
    /// remote, for every candidate of that flow.
    /// <para>
    /// The per-hop <c>Subflow.Descend</c> spans nest under it, so the ladder stays readable while
    /// this one carries the branch's total. A domain boundary is one hop for a whole branch, which
    /// makes the difference between this span and the sum of its children the local work.
    /// </para>
    /// </summary>
    public static Activity? StartHumanTaskDescend(string flow, int roots)
    {
        var activity = ActivitySource.StartActivity($"{OperationHumanTaskDescend}/{flow}", ActivityKind.Internal);
        if (activity is null) return null;

        activity.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        activity.SetTag(TelemetryConstants.TagNames.Flow, flow);
        activity.SetTag(TelemetryConstants.TagNames.HumanTaskRoots, roots);
        return activity;
    }

    /// <summary>
    /// Starts the span covering leaf-side authorization at ONE level of a descent — evaluator
    /// construction and grant evaluation for every leaf found at that level.
    /// <para>
    /// One span for the level, not one per leaf: a branch can carry hundreds of candidates, and the
    /// cardinality would swamp the trace for the same information a count already gives. Same rule
    /// <c>View.Resolve</c> follows for its rule walk. Building an evaluator can serialize the
    /// instance's full latest data, so this is where that cost becomes visible.
    /// </para>
    /// </summary>
    public static Activity? StartHumanTaskAuthorize(string flow)
    {
        var activity = ActivitySource.StartActivity($"{OperationHumanTaskAuthorize}/{flow}", ActivityKind.Internal);
        if (activity is null) return null;

        activity.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        activity.SetTag(TelemetryConstants.TagNames.Flow, flow);
        return activity;
    }

    /// <summary>Operation name for view-rule resolution.</summary>
    public const string OperationViewResolve = "View.Resolve";

    /// <summary>
    /// Starts the span covering view-rule evaluation — the ordered walk through a state's or
    /// transition's <c>views</c> array until a rule matches.
    /// <para>
    /// Each rule is a compiled C# script, and on the warm path it is completely invisible today:
    /// <c>Script.Compile</c> appears only on a cold compile, so a request that evaluated four rules
    /// and one that evaluated none look identical. One span for the whole walk, with the rule count
    /// and the winner as tags — not one span per rule, which would turn a slow view definition into
    /// a wide trace instead of a readable number.
    /// </para>
    /// </summary>
    public static Activity? StartViewResolve()
    {
        var activity = ActivitySource.StartActivity(OperationViewResolve, ActivityKind.Internal);
        activity?.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        return activity;
    }

    /// <summary>Records how many rules ran and which view won.</summary>
    public static void SetViewResolution(Activity? activity, int rulesEvaluated, string? selectedView)
    {
        if (activity is null) return;

        activity.SetTag(TelemetryConstants.TagNames.ViewRulesEvaluated, rulesEvaluated);
        if (selectedView is { Length: > 0 })
            activity.SetTag(TelemetryConstants.TagNames.ViewSelected, selectedView);
    }

    /// <summary>Operation name for the wait on the active-subflow build gate.</summary>
    public const string OperationBuildGate = "Instance.Read.BuildGate";

    /// <summary>This request went on to build the response itself.</summary>
    public const string BuildGateBuild = "build";

    /// <summary>The wait paid off: the holder populated the cache and this request served that.</summary>
    public const string BuildGateCoalesced = "coalesced";

    /// <summary>
    /// Starts the span covering the wait on the per-key build gate that serialises concurrent state
    /// builds for an instance with an active subflow.
    /// <para>
    /// An active-subflow state read cannot be validated from the parent row, so it bypasses the
    /// long-poll fast path and performs a live descent. The gate exists so that N simultaneous polls
    /// on the same parent produce ONE descent; without a span, the time a request spends queued
    /// behind another request's descent is indistinguishable from its own work, and the coalescing
    /// the gate performs — the entire reason it exists — leaves no trace at all.
    /// </para>
    /// <para>
    /// Opened on the gate path only, which is already the build branch: the 304 and cache-hit
    /// branches return before reaching it, so this adds no documents to the hot path.
    /// </para>
    /// </summary>
    public static Activity? StartBuildGate()
    {
        var activity = ActivitySource.StartActivity(OperationBuildGate, ActivityKind.Internal);
        activity?.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        return activity;
    }

    /// <summary>
    /// Records whether the request actually waited and what the wait bought.
    /// </summary>
    /// <param name="activity">The gate span, or null when nothing is listening.</param>
    /// <param name="contended">True when the gate was held on arrival.</param>
    /// <param name="outcome"><see cref="BuildGateBuild"/> or <see cref="BuildGateCoalesced"/>.</param>
    public static void SetBuildGateOutcome(Activity? activity, bool contended, string outcome)
    {
        if (activity is null) return;

        activity.SetTag(TelemetryConstants.TagNames.BuildGateContended, contended);
        activity.SetTag(TelemetryConstants.TagNames.BuildGateOutcome, outcome);
    }

    /// <summary>One level of descent into an active subflow.</summary>
    public const string OperationDescend = "Subflow.Descend";

    /// <summary>Envelope for one built-in instance read.</summary>
    public const string OperationRead = "Instance.Read";

    /// <summary>
    /// Starts the envelope span for one built-in read, named <c>Instance.Read/{kind}</c>.
    /// <para>
    /// This is the one genuine does-not-exist gap on the read path. Built-in and custom functions
    /// share a single route template (<c>{domain}/workflows/{workflow}/instances/{instance}/functions/{function}</c>),
    /// so Elastic's <c>transaction.name</c> is identical for a state poll, a view read and a
    /// custom-function call. Per-function latency is therefore unobtainable from the transaction —
    /// and the leaf layer does not help: <c>Db.*</c> and <c>Cache.*</c> are always on and are by far
    /// the top emitters, so the read path is densely instrumented at the bottom and unstructured at
    /// the top. A bounded-cardinality envelope is the only thing that can carry the distinction.
    /// </para>
    /// <para>
    /// Opened in the query service rather than the controller on purpose: a descent re-enters this
    /// service once per level, so an envelope at the controller would count one read where the
    /// request actually performed three.
    /// </para>
    /// </summary>
    /// <param name="kind">One of <see cref="InstanceReadKinds"/> — a closed set, so it can sit in the span name.</param>
    /// <param name="domain">Owning domain, for aggregation. Bounded by the domain catalogue.</param>
    /// <param name="flow">Owning workflow key. Bounded by the component catalogue.</param>
    public static Activity? StartRead(string kind, string? domain = null, string? flow = null)
    {
        // Implicit parent, for the same baggage reason as StartDescend below.
        var activity = ActivitySource.StartActivity($"{OperationRead}/{kind}", ActivityKind.Internal);
        if (activity is null) return null;

        activity.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        activity.SetTag(TelemetryConstants.TagNames.Layer, TelemetryConstants.Layers.Orchestration);
        if (domain is { Length: > 0 }) activity.SetTag(TelemetryConstants.TagNames.Domain, domain);
        if (flow is { Length: > 0 }) activity.SetTag(TelemetryConstants.TagNames.Flow, flow);
        return activity;
    }

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
