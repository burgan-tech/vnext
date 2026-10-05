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
    /// Marks the activity Error from a Result-pattern failure: status, <c>error.code</c> and, when
    /// given, <c>error.type</c>. The single entry point for "this span's operation failed with a
    /// business/infrastructure <c>Error</c>" so every span spells the attributes the same way.
    /// </summary>
    public static Activity? SetResultError(this Activity? activity, string? errorCode, string? errorMessage, string? errorType = null)
    {
        if (activity != null)
        {
            activity.SetStatus(ActivityStatusCode.Error, errorMessage);
            if (!string.IsNullOrEmpty(errorCode))
                activity.SetTag(TelemetryConstants.TagNames.ErrorCode, errorCode);
            if (!string.IsNullOrEmpty(errorType))
                activity.SetTag(TelemetryConstants.TagNames.ErrorType, errorType);
        }
        return activity;
    }

    /// <summary>
    /// Sets <c>Ok</c> unless the span already carries <c>Error</c>. <see cref="Activity.SetStatus"/>
    /// replaces the previous status, so an unconditional <c>Ok</c> at the end of a handler erases
    /// an error a nested call recorded on the same span (see <see cref="MarkFaultedOnLocalChain"/>).
    /// </summary>
    public static Activity? SetOkUnlessError(this Activity? activity)
    {
        if (activity is { Status: not ActivityStatusCode.Error })
            activity.SetStatus(ActivityStatusCode.Ok);
        return activity;
    }

    /// <summary>
    /// Marks the activity and every in-process ancestor Error with
    /// <see cref="TelemetryConstants.TagNames.InstanceFaulted"/>. Used where a pipeline failure
    /// faults the instance but the operation deliberately still returns success (200 +
    /// <c>Status=F</c>). The walk follows <see cref="Activity.Parent"/>, which is null past a
    /// remote or explicitly supplied parent context, so it stops at the local root — the job's
    /// flat-lane span, or the HTTP server span on the sync path — and never reaches another trace.
    /// </summary>
    public static void MarkFaultedOnLocalChain(this Activity? activity, string? errorCode, string? errorMessage)
    {
        for (var current = activity; current != null; current = current.Parent)
        {
            current.SetResultError(errorCode, errorMessage);
            current.SetTag(TelemetryConstants.TagNames.InstanceFaulted, true);
        }
    }

    /// <summary>
    /// Records an exception and sets the activity status to Error.
    /// Wraps OpenTelemetry's RecordException, sets standard error.type and status.
    /// </summary>
    public static Activity? RecordExceptionWithStatus(this Activity? activity, Exception exception, string? description = null)
        => SetError(activity, exception, description);
}
