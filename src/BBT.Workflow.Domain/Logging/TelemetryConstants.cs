namespace BBT.Workflow.Logging;

/// <summary>
/// Constants for OpenTelemetry span attributes used throughout the vNext workflow system.
/// Centralizes all telemetry-related string constants for maintainability.
/// </summary>
public static class TelemetryConstants
{
    /// <summary>
    /// Tag names for OpenTelemetry span attributes.
    /// </summary>
    public static class TagNames
    {
        public const string Domain = "vnext.domain";
        public const string Flow = "vnext.flow.key";
        public const string FlowVersion = "vnext.flow.version";
        public const string InstanceId = "vnext.instance.id";
        /// <summary>
        /// Vendor-neutral workflow instance identifier used to correlate telemetry
        /// emitted by dependencies that are not part of vNext.
        /// </summary>
        public const string WorkflowInstanceId = "workflow.instance.id";
        /// <summary>
        /// Business-operation correlation identifier. This is intentionally separate
        /// from the per-request X-Request-Id and the W3C trace identifier.
        /// </summary>
        public const string CorrelationId = "correlation.id";
        /// <summary>
        /// Primary subject exposed by the gateway authentication chain.
        /// It is propagated as correlation metadata only.
        /// </summary>
        public const string Sub = "sub";
        /// <summary>
        /// Actor subject exposed by the gateway authentication chain.
        /// </summary>
        public const string ActSub = "act.sub";
        public const string InstanceKey = "vnext.instance.key";
        public const string TransitionKey = "vnext.transition.key";
        public const string TriggerType = "vnext.trigger.type";
        public const string HandlerName = "vnext.handler.name";
        public const string TaskKey = "vnext.task.key";
        public const string TaskType = "vnext.task.type";

        /// <summary>Stable identity of one fan-out item within its batch.</summary>
        public const string FanOutItemKey = "vnext.fanout.item.key";

        /// <summary>Zero-based position of a fan-out item in its batch.</summary>
        public const string FanOutItemIndex = "vnext.fanout.item.index";

        /// <summary>
        /// Milliseconds a fan-out item spent queueing for its concurrency slots before execution
        /// began. Separates "the batch is slow because it is throttled" from "the batch is slow
        /// because one item is slow" without correlating two spans.
        /// </summary>
        public const string FanOutItemQueueWaitMs = "vnext.fanout.item.queue_wait_ms";
        public const string Layer = "vnext.layer";
        public const string SpanCategory = "vnext.span.category";
        public const string StateFrom = "vnext.state.from";
        public const string StateTo = "vnext.state.to";
        public const string JobName = "vnext.job.name";
        /// <summary>
        /// Parent instance ID for subflow/subprocess correlation in traces and logs.
        /// </summary>
        public const string ParentInstanceId = "vnext.parent.instance.id";
        /// <summary>
        /// Subflow instance ID for subflow/subprocess correlation in traces and logs.
        /// </summary>
        public const string SubflowInstanceId = "vnext.subflow.instance.id";
        /// <summary>
        /// Root (ancestor) instance ID — the top-level flow in a nested subflow chain (A→B→C→D always carries A's ID).
        /// </summary>
        public const string RootInstanceId = "vnext.root.instance.id";
        /// <summary>
        /// Originating request id (X-Request-Id value) — joins spans/logs across the async
        /// job, Execution and worker hops back to the client request that started them.
        /// <para>
        /// The value is the normalized form of the <c>X-Request-Id</c> header (lowercase, '-'
        /// replaced by '_') so log backends surface it under exactly the name the platform uses
        /// for the header. It carries no dot on purpose: backends that flatten dotted keys
        /// (OpenObserve, Elasticsearch) leave this one untouched, so the queried field is
        /// <c>x_request_id</c> everywhere.
        /// </para>
        /// <para>
        /// Because this key is identical to what Aether's header enricher would produce for
        /// <c>X-Request-Id</c>, that header must NEVER be listed in
        /// <c>Telemetry:Logging:Enrichers:Headers</c> — the enricher writes first and reports a
        /// value fabricated from <c>HttpContext.TraceIdentifier</c> on platform-originated
        /// requests (Dapr job callbacks, pub/sub deliveries), which would then suppress the
        /// correct value stamped from the correlation provider.
        /// </para>
        /// </summary>
        public const string RequestId = "x_request_id";
        public const string SubItemType = "vnext.subitem.type";
        public const string SubItemOutcome = "vnext.subitem.outcome";
        public const string TerminationOrigin = "vnext.termination.origin";
        public const string TerminationInitiator = "vnext.termination.initiator";
        public const string TerminationCascadeId = "vnext.termination.cascade_id";

        /// <summary>
        /// True when the span was parented to its trace lane anchor (see
        /// <c>WorkflowTraceLane</c>), false when it fell back to the ambient/predecessor parent.
        /// Lets a query separate flat-laned traces from legacy ones during a rolling deploy.
        /// </summary>
        public const string TraceLane = "vnext.trace.lane";

        /// <summary>The lane anchor's span id — groups a trace's lanes when it has more than one.</summary>
        public const string TraceLaneAnchor = "vnext.trace.lane.anchor";

        /// <summary>
        /// Set when an anchor was rejected for belonging to a different trace. The span keeps its
        /// ambient parent and links the anchor instead, so a stale or forged anchor cannot
        /// teleport it into a foreign trace.
        /// </summary>
        public const string TraceLaneMismatch = "vnext.trace.lane.mismatch";

        /// <summary>
        /// Span id of the immediate logical predecessor (hop N for hop N+1). Primary causality tag:
        /// the chain can be reconstructed by self-join even in a UI that hides ActivityLinks.
        /// </summary>
        public const string HopPredecessor = "vnext.hop.predecessor";

        /// <summary>
        /// Monotonic ordinal of a hop within its lane. Needed because <c>ChainDepth</c> resets to 0
        /// at subflow resume, long-poll resume, timeout and retry boundaries, so it cannot order a
        /// lane on its own.
        /// </summary>
        public const string LaneSeq = "vnext.lane.seq";

        /// <summary>Chain depth of the transition hop, promoted onto the lane span.</summary>
        public const string ChainDepth = "vnext.chain.depth";

        /// <summary>
        /// Set when the ambient Dapr scheduler-callback span was demoted to an ActivityLink because
        /// the span continues a different (originating) trace.
        /// </summary>
        public const string DaprCallback = "vnext.dapr.callback";
    }

    /// <summary>
    /// Well-known values used to group workflow spans into a compact business view
    /// or the full diagnostic view.
    /// </summary>
    public static class Layers
    {
        public const string Orchestration = "orchestration";
        public const string Execution = "execution";
    }

    public static class SpanCategories
    {
        public const string Business = "business";
        public const string Diagnostic = "diagnostic";
    }

    /// <summary>
    /// HTTP header names used for cross-domain correlation.
    /// </summary>
    public static class HeaderNames
    {
        /// <summary>
        /// Canonical workflow instance identifier propagated to trusted HTTP dependencies.
        /// </summary>
        public const string WorkflowInstanceId = "X-Workflow-Instance-Id";
        /// <summary>
        /// Canonical business correlation identifier propagated to trusted HTTP dependencies.
        /// </summary>
        public const string CorrelationId = "X-Correlation-Id";
        /// <summary>
        /// Primary subject emitted by the gateway after authentication.
        /// </summary>
        public const string Sub = "sub";
        /// <summary>
        /// Actor subject emitted by the gateway after authentication.
        /// </summary>
        public const string ActSub = "act_sub";
        /// <summary>
        /// Request header carrying the parent instance ID when invoking subflow/subprocess remotely.
        /// </summary>
        public const string ParentInstanceId = "X-Parent-Instance-Id";
        /// <summary>
        /// Request header carrying the root (ancestor) instance ID across the full subflow chain.
        /// Remains constant at A's ID regardless of nesting depth.
        /// </summary>
        public const string RootInstanceId = "X-Root-Instance-Id";
        /// <summary>
        /// Request header carrying the originating request id. Read/generated at the edge by
        /// the gateway and by Aether's correlation middleware; forwarded on every internal hop.
        /// </summary>
        public const string RequestId = "X-Request-Id";
    }

    /// <summary>
    /// Accepts a constrained actor claim emitted by the gateway authentication chain.
    /// The exact value is retained; unsafe or unexpectedly large values are omitted.
    /// </summary>
    public static bool TryNormalizeIdentityClaim(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrEmpty(value) || value.Length > 128)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-')
            {
                return false;
            }
        }

        normalized = value;
        return true;
    }
}
