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
    /// Records an exception, sets the standard OTel error.type attribute, and sets the activity status to Error.
    /// </summary>
    public static Activity? SetError(this Activity? activity, Exception exception, string? description = null)
    {
        if (activity != null)
        {
            activity.AddException(exception);
            activity.SetStatus(ActivityStatusCode.Error, description ?? exception.Message);
            activity.SetTag(TelemetryConstants.TagNames.ErrorType, exception.GetType().FullName ?? exception.GetType().Name);
        }
        return activity;
    }

    /// <summary>
    /// Marks the activity as Error with standard OpenTelemetry error.type and error.code attributes.
    /// </summary>
    public static Activity? SetError(this Activity? activity, string errorMessage, string? errorType = null, string? errorCode = null)
    {
        if (activity != null)
        {
            activity.SetStatus(ActivityStatusCode.Error, errorMessage);
            if (!string.IsNullOrEmpty(errorType))
                activity.SetTag(TelemetryConstants.TagNames.ErrorType, errorType);
            if (!string.IsNullOrEmpty(errorCode))
                activity.SetTag(TelemetryConstants.TagNames.ErrorCode, errorCode);
        }
        return activity;
    }

    /// <summary>
    /// Records an exception and sets the activity status to Error.
    /// Wraps OpenTelemetry's RecordException, sets standard error.type and status.
    /// </summary>
    public static Activity? RecordExceptionWithStatus(this Activity? activity, Exception exception, string? description = null)
        => SetError(activity, exception, description);
}
