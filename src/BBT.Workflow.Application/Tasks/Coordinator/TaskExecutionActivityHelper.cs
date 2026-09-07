using System.Diagnostics;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Tasks.Coordinator;

/// <summary>
/// Provides centralized tracing for task execution phases (input handler, invoke, output handler).
/// Creates child spans under the current activity so executor timing is visible in traces.
/// </summary>
public static class TaskExecutionActivityHelper
{
    /// <summary>
    /// ActivitySource for task execution phases (PrepareInput, Invoke, ProcessOutput).
    /// When using explicit OpenTelemetry source registration, add this source to the TracerProvider
    /// (e.g. <c>AddSource("BBT.Workflow.Tasks")</c>). If the host uses a wildcard such as
    /// <c>AddSource("BBT.Workflow.*")</c>, no extra registration is needed.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(TelemetryConstants.ActivitySources.Tasks);

    /// <summary>
    /// Operation name for the input preparation (PrepareInput) phase.
    /// </summary>
    public const string OperationPrepareInput = "Task.PrepareInput";

    /// <summary>
    /// Operation name for the raw invocation (Invoke) phase.
    /// </summary>
    public const string OperationInvoke = "Task.Invoke";

    /// <summary>
    /// Operation name for the output processing (ProcessOutput) phase.
    /// </summary>
    public const string OperationProcessOutput = "Task.ProcessOutput";

    /// <summary>
    /// Operation name for one fan-out item's slot acquisition plus inner-task execution.
    /// </summary>
    public const string OperationFanOutItem = "FanOut.Item";

    /// <summary>
    /// Operation name for a trigger-family task's local (in-process, same-domain) invocation.
    /// </summary>
    public const string OperationTriggerLocal = "Trigger.Local";

    /// <summary>
    /// Operation name for component-ref resolution + clone inside the task factory — the
    /// previously unspanned head of <c>Task.Execute.{key}</c>.
    /// </summary>
    public const string OperationResolve = "Task.Resolve";

    /// <summary>
    /// Operation name for the journal-row creation/probe persist.
    /// </summary>
    public const string OperationJournalCreate = "Task.Journal.Create";

    /// <summary>
    /// Operation name for the journal-row completion persist.
    /// </summary>
    public const string OperationJournalComplete = "Task.Journal.Complete";

    /// <summary>
    /// Starts the span for a trigger-family task's LOCAL (same-domain, in-process) invocation.
    /// <para>
    /// NOT gated on verbose tracing: the remote branch of these tasks produces a Dapr/HTTP client
    /// span, so without this span the local branch is the only invocation shape that leaves no
    /// trace at all — the request would be invisible under <c>Task.Execute.*</c> and its cost
    /// indistinguishable from engine overhead. One span per task invocation; business category.
    /// </para>
    /// This span is also the intended <c>WorkflowTraceLane</c> child-lane anchor for the work the
    /// invocation enqueues (see <c>TriggerTaskExecutorBase.RunLocalScopedAsync</c>).
    /// </summary>
    /// <param name="taskKey">The task key (becomes part of the display name).</param>
    /// <param name="taskType">The task type name (StartTrigger, DirectTrigger, …).</param>
    /// <param name="targetDomain">Target domain of the invocation.</param>
    /// <param name="targetFlow">Target workflow, when known.</param>
    /// <param name="targetInstance">Target instance identifier, when known.</param>
    public static Activity? StartLocalTriggerActivity(
        string taskKey,
        string taskType,
        string? targetDomain,
        string? targetFlow = null,
        string? targetInstance = null)
    {
        var activity = ActivitySource.StartActivity(
            $"{OperationTriggerLocal}.{taskKey}",
            ActivityKind.Internal);

        if (activity != null)
        {
            activity.SetTag(TelemetryConstants.TagNames.TaskKey, taskKey);
            activity.SetTag(TelemetryConstants.TagNames.TaskType, taskType);
            activity.SetTag(TelemetryConstants.TagNames.Layer, TelemetryConstants.Layers.Orchestration);
            activity.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
            if (!string.IsNullOrEmpty(targetDomain))
                activity.SetTag(TelemetryConstants.TagNames.TriggerTargetDomain, targetDomain);
            if (!string.IsNullOrEmpty(targetFlow))
                activity.SetTag(TelemetryConstants.TagNames.TriggerTargetFlow, targetFlow);
            if (!string.IsNullOrEmpty(targetInstance))
                activity.SetTag(TelemetryConstants.TagNames.TriggerTargetInstance, targetInstance);
        }

        return activity;
    }

    /// <summary>
    /// Starts a new activity as a child of the current activity for an executor phase
    /// (PrepareInput, Invoke, ProcessOutput). These phases are business-level and always on —
    /// not gated behind verbose tracing — so they are visible under the existing
    /// <c>Task.Execute.{key}</c> span (created by Aether's <c>[Trace]</c> aspect on
    /// <c>TaskExecutionEngine.ExecuteAsync</c>) in every trace, not just verbose ones.
    /// When taskKey/taskType are provided, enriches the span with standard tags for filtering.
    /// </summary>
    /// <param name="operationName">The name of the operation (e.g. Task.PrepareInput, Task.Invoke, Task.ProcessOutput).</param>
    /// <param name="taskKey">Optional task key for span tags.</param>
    /// <param name="taskType">Optional task type for span tags.</param>
    /// <returns>A new Activity linked to the current trace context, or null if no listener.</returns>
    public static Activity? StartActivity(
        string operationName,
        string? taskKey = null,
        string? taskType = null)
    {
        var activity = ActivitySource.StartActivity(
            operationName,
            ActivityKind.Internal);

        if (activity != null)
        {
            if (!string.IsNullOrEmpty(taskKey))
                activity.SetTag(TelemetryConstants.TagNames.TaskKey, taskKey);
            if (!string.IsNullOrEmpty(taskType))
                activity.SetTag(TelemetryConstants.TagNames.TaskType, taskType);
            activity.SetTag(TelemetryConstants.TagNames.Layer, TelemetryConstants.Layers.Orchestration);
            activity.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        }

        return activity;
    }

    /// <summary>
    /// Sets span Status=Error and records standard error tags (error.type, error.code).
    /// Call only on the final failure outcome — not on intermediate retry attempts.
    /// </summary>
    public static void SetError(Activity? activity, string? errorMessage, string? errorType = null, int? statusCode = null)
    {
        if (activity is null) return;
        activity.SetStatus(ActivityStatusCode.Error, errorMessage);
        if (!string.IsNullOrEmpty(errorType))
            activity.SetTag(TelemetryConstants.TagNames.ErrorType, errorType);
        if (statusCode.HasValue)
            activity.SetTag(TelemetryConstants.TagNames.ErrorCode, statusCode.Value.ToString());
    }

    /// <summary>
    /// Adds a <c>task.failed</c> ActivityEvent with error details.
    /// Use on every failure attempt (including retried ones) so the timeline shows each failure.
    /// </summary>
    public static void AddFailedEvent(Activity? activity, string? errorMessage, string? errorType = null, int? statusCode = null)
    {
        if (activity is null) return;
        var tags = new ActivityTagsCollection { { "error.message", errorMessage ?? string.Empty } };
        if (!string.IsNullOrEmpty(errorType)) tags.Add("error.type", errorType);
        if (statusCode.HasValue) tags.Add("error.code", statusCode.Value.ToString());
        activity.AddEvent(new ActivityEvent("task.failed", tags: tags));
    }

    /// <summary>
    /// Adds a <c>task.retry</c> ActivityEvent so each retry attempt is visible in the trace timeline.
    /// </summary>
    public static void AddRetryEvent(Activity? activity, int attempt, int maxRetries, string? errorMessage, TimeSpan delay)
    {
        activity?.AddEvent(new ActivityEvent("task.retry", tags: new ActivityTagsCollection
        {
            { "retry.attempt", attempt },
            { "retry.max", maxRetries },
            { "retry.delay_ms", (long)delay.TotalMilliseconds },
            { "error.message", errorMessage }
        }));
    }
}
