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
        public const string InstanceKey = "vnext.instance.key";
        public const string TransitionKey = "vnext.transition.key";
        public const string TriggerType = "vnext.trigger.type";
        public const string HandlerName = "vnext.handler.name";
        public const string TaskKey = "vnext.task.key";
        public const string TaskType = "vnext.task.type";
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
        /// </summary>
        public const string RequestId = "vnext.request.id";
        public const string SubItemType = "vnext.subitem.type";
        public const string SubItemOutcome = "vnext.subitem.outcome";
        public const string TerminationOrigin = "vnext.termination.origin";
        public const string TerminationInitiator = "vnext.termination.initiator";
        public const string TerminationCascadeId = "vnext.termination.cascade_id";
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
}
