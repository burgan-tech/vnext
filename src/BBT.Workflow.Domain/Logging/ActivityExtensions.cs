using System.Diagnostics;

namespace BBT.Workflow.Logging;

/// <summary>
/// Extension methods for Activity to simplify OpenTelemetry operations.
/// </summary>
public static class ActivityExtensions
{
    /// <summary>
    /// Sets the display name for the activity.
    /// </summary>
    public static Activity? SetDisplayName(this Activity? activity, string displayName)
    {
        if (activity != null)
        {
            // Pipeline step spans may be suppressed by the Business profile. In that case
            // Activity.Current is the transition parent; never rename that parent to a step.
            if (TryGetPipelineStepOperationName(displayName, out var expectedOperationName) &&
                !string.Equals(activity.OperationName, expectedOperationName, StringComparison.Ordinal))
            {
                return activity;
            }

            activity.DisplayName = displayName;
        }
        return activity;
    }

    private static bool TryGetPipelineStepOperationName(string displayName, out string operationName)
    {
        operationName = string.Empty;
        if (string.IsNullOrEmpty(displayName) || displayName[0] != '[')
            return false;

        var separatorIndex = displayName.IndexOf("] ", StringComparison.Ordinal);
        if (separatorIndex < 0 || separatorIndex + 2 >= displayName.Length)
            return false;

        var stepName = displayName[(separatorIndex + 2)..];
        if (!stepName.EndsWith("Step", StringComparison.Ordinal))
            return false;

        operationName = $"{stepName}.ExecuteAsync";
        return true;
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
