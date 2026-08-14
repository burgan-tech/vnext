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
