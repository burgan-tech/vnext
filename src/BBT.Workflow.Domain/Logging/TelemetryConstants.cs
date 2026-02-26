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
        public const string TransitionKey = "vnext.transition.key";
        public const string TriggerType = "vnext.trigger.type";
        public const string HandlerName = "vnext.handler.name";
        public const string TaskKey = "vnext.task.key";
        public const string TaskType = "vnext.task.type";
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
    }
}
