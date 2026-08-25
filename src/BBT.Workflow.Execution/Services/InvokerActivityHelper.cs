using System.Diagnostics;

namespace BBT.Workflow.Execution.Services;

/// <summary>
/// Starts the per-invocation span on the Execution side and records its outcome. The span makes
/// each task invocation identifiable in the trace (Task.Invoke.{type}/{key}) and is the only
/// place an invoker-produced failure — an HTTP 5xx result, a missing invoker, an exception —
/// turns into an error-status span; the ASP.NET server span alone stays green for those.
/// </summary>
internal static class InvokerActivityHelper
{
    /// <summary>
    /// Same source name <c>ExecutionController</c> uses, so the existing
    /// "BBT.Workflow.Execution*" entry in Telemetry:Tracing:AdditionalSources covers it —
    /// no host configuration change is needed.
    /// </summary>
    private static readonly ActivitySource ActivitySource = new("BBT.Workflow.Execution");

    // Kept local so the stateless Execution package does not acquire a Domain dependency.
    // These names mirror TelemetryConstants.TagNames in vNext Domain.
    private const string TaskKeyTag = "vnext.task.key";
    private const string TaskTypeTag = "vnext.task.type";
    private const string LayerTag = "vnext.layer";
    private const string SpanCategoryTag = "vnext.span.category";
    private const string ErrorTypeTag = "error.type";
    private const string ErrorCodeTag = "error.code";

    /// <summary>
    /// Opens the invocation span. Null when no listener sampled it — callers must tolerate null.
    /// </summary>
    public static Activity? StartInvokeActivity(TaskEnvelope envelope)
    {
        var activity = ActivitySource.StartActivity(
            $"Task.Invoke.{envelope.TaskType}/{envelope.TaskKey}",
            ActivityKind.Internal);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag(TaskKeyTag, envelope.TaskKey);
        activity.SetTag(TaskTypeTag, envelope.TaskType);
        activity.SetTag(LayerTag, "execution");
        activity.SetTag(SpanCategoryTag, "business");
        return activity;
    }

    /// <summary>
    /// Marks the span with the invocation's own verdict: business failures (IsSuccess=false,
    /// e.g. an HTTP 5xx surfaced by HttpTaskInvoker) become error-status spans with the status
    /// code attached; successes become OK.
    /// </summary>
    public static void RecordResult(Activity? activity, TaskInvocationResult result)
    {
        if (activity is null)
        {
            return;
        }

        if (result.IsSuccess)
        {
            activity.SetStatus(ActivityStatusCode.Ok);
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, result.ErrorMessage);
        if (result.StatusCode is { } statusCode)
        {
            activity.SetTag(ErrorCodeTag, statusCode);
        }
    }

    /// <summary>Marks the span for a task type no invoker claims.</summary>
    public static void RecordInvokerNotFound(Activity? activity, string error)
    {
        activity?.SetStatus(ActivityStatusCode.Error, error);
        activity?.SetTag(ErrorTypeTag, "InvokerNotFound");
    }

    /// <summary>Marks the span for an exception the invoker let escape.</summary>
    public static void RecordException(Activity? activity, Exception exception)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity.SetTag(ErrorTypeTag, exception.GetType().Name);
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            ["exception.type"] = exception.GetType().FullName,
            ["exception.message"] = exception.Message
        }));
    }
}
