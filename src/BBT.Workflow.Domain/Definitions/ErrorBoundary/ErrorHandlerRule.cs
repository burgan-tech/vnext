using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Represents a single error handling rule within an error boundary.
/// Rules are evaluated in priority order to find a matching handler for an error.
/// </summary>
public sealed record ErrorHandlerRule
{
    /// <summary>
    /// Default priority for rules without explicit priority.
    /// </summary>
    public const int DefaultPriority = 100;

    /// <summary>
    /// Priority for wildcard rules (should be evaluated last).
    /// </summary>
    public const int WildcardPriority = 999;

    /// <summary>
    /// The action to take when this rule matches an error.
    /// </summary>
    [JsonPropertyName("action")]
    public ErrorAction Action { get; init; } = ErrorAction.Abort;

    /// <summary>
    /// List of exception type names to match (e.g., "ValidationException", "HttpRequestException").
    /// If null or empty, matches all exception types.
    /// Use "*" for explicit wildcard matching.
    /// </summary>
    [JsonPropertyName("errorTypes")]
    public IReadOnlyList<string>? ErrorTypes { get; init; }

    /// <summary>
    /// List of error codes to match (e.g., HTTP status codes, domain error codes).
    /// If null or empty, matches all error codes.
    /// </summary>
    [JsonPropertyName("errorCodes")]
    public IReadOnlyList<int>? ErrorCodes { get; init; }

    /// <summary>
    /// Optional transition key to trigger when this rule matches.
    /// Used with Abort/Rollback actions to navigate to a specific state.
    /// </summary>
    [JsonPropertyName("transition")]
    public string? Transition { get; init; }

    /// <summary>
    /// Priority of this rule. Lower values are evaluated first.
    /// Default is 100. Wildcard rules should use 999.
    /// </summary>
    [JsonPropertyName("priority")]
    public int Priority { get; init; } = DefaultPriority;

    /// <summary>
    /// Retry policy configuration. Only used when Action is Retry.
    /// </summary>
    [JsonPropertyName("retryPolicy")]
    public RetryPolicy? RetryPolicy { get; init; }

    /// <summary>
    /// If true, only logs the error without affecting execution flow.
    /// Used with Ignore action for audit-only scenarios.
    /// </summary>
    [JsonPropertyName("logOnly")]
    public bool LogOnly { get; init; }

    /// <summary>
    /// Notification configuration. Used when Action is Notify.
    /// </summary>
    [JsonPropertyName("notificationConfig")]
    public NotificationConfig? NotificationConfig { get; init; }

    /// <summary>
    /// Optional list of allowed manual actions for Notify action.
    /// Used for human-in-the-loop scenarios.
    /// </summary>
    [JsonPropertyName("allowedActions")]
    public IReadOnlyList<string>? AllowedActions { get; init; }

    /// <summary>
    /// Checks if this rule is a wildcard rule (matches all errors).
    /// </summary>
    [JsonIgnore]
    public bool IsWildcard =>
        (ErrorTypes == null || ErrorTypes.Count == 0 || ErrorTypes.Contains("*")) &&
        (ErrorCodes == null || ErrorCodes.Count == 0);

    /// <summary>
    /// Calculates the effective priority considering wildcard status.
    /// </summary>
    [JsonIgnore]
    public int EffectivePriority => IsWildcard && Priority == DefaultPriority ? WildcardPriority : Priority;

    /// <summary>
    /// Calculates the specificity of this rule for tie-breaking.
    /// Higher values mean more specific rules.
    /// </summary>
    [JsonIgnore]
    public int Specificity
    {
        get
        {
            var score = 0;
            if (ErrorTypes != null && ErrorTypes.Count > 0 && !ErrorTypes.Contains("*"))
                score += ErrorTypes.Count;
            if (ErrorCodes != null && ErrorCodes.Count > 0)
                score += ErrorCodes.Count;
            return score;
        }
    }

    /// <summary>
    /// Checks if this rule matches the given exception type name.
    /// </summary>
    /// <param name="exceptionTypeName">The exception type name to check.</param>
    /// <returns>True if the rule matches the exception type.</returns>
    public bool MatchesExceptionType(string exceptionTypeName)
    {
        if (ErrorTypes == null || ErrorTypes.Count == 0 || ErrorTypes.Contains("*"))
            return true;

        return ErrorTypes.Any(t =>
            string.Equals(t, exceptionTypeName, StringComparison.OrdinalIgnoreCase) ||
            exceptionTypeName.EndsWith(t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks if this rule matches the given error code.
    /// </summary>
    /// <param name="errorCode">The error code to check (null if not applicable).</param>
    /// <returns>True if the rule matches the error code.</returns>
    public bool MatchesErrorCode(int? errorCode)
    {
        if (ErrorCodes == null || ErrorCodes.Count == 0)
            return true;

        if (errorCode == null)
            return false;

        return ErrorCodes.Contains(errorCode.Value);
    }
}

