using System.Diagnostics;

namespace BBT.Workflow.Logging;

/// <summary>
/// Extension methods for Activity to simplify OpenTelemetry operations.
/// </summary>
public static class ActivityExtensions
{
    /// <summary>
    /// Sets the display name for the activity. Names starting with '[' opt the span into the
    /// Business-profile export filter, so such names must only ever be given to spans whose
    /// CREATION is already gated on Verbose mode (see <c>PipelineStepActivityHelper</c>) — a
    /// created-but-filtered span orphans every child started inside it.
    /// </summary>
    public static Activity? SetDisplayName(this Activity? activity, string displayName)
    {
        if (activity != null)
        {
            activity.DisplayName = displayName;
        }
        return activity;
    }

    /// <summary>
    /// Records an exception and sets the activity status to Error.
    /// Wraps OpenTelemetry's RecordException and adds status.
    /// </summary>
    public static Activity? RecordExceptionWithStatus(this Activity? activity, Exception exception, string? description = null)
    {
        if (activity != null)
        {
            activity.AddException(exception);
            activity.SetStatus(ActivityStatusCode.Error, description ?? exception.Message);
        }
        return activity;
    }
}
