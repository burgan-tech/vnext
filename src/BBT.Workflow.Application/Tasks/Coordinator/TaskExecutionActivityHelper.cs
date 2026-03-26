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
    public static readonly ActivitySource ActivitySource = new("BBT.Workflow.Tasks");

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
    /// Starts a new activity as a child of the current activity for an executor phase.
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
        var parentContext = Activity.Current?.Context ?? default;

        var activity = ActivitySource.StartActivity(
            operationName,
            ActivityKind.Internal,
            parentContext);

        if (activity != null)
        {
            if (!string.IsNullOrEmpty(taskKey))
                activity.SetTag(TelemetryConstants.TagNames.TaskKey, taskKey);
            if (!string.IsNullOrEmpty(taskType))
                activity.SetTag(TelemetryConstants.TagNames.TaskType, taskType);
        }

        return activity;
    }
}
