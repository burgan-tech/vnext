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
    /// List of error codes to match. Supports both domain error codes (e.g., "Task:400007")
    /// and HTTP status codes as strings (e.g., "500", "503").
    /// If null or empty, matches all error codes.
    /// Use "*" for explicit wildcard matching.
    /// </summary>
    [JsonPropertyName("errorCodes")]
    public IReadOnlyList<string>? ErrorCodes { get; init; }

    /// <summary>
    /// Optional transition key to trigger when this rule matches.
    /// Used with Rollback/Notify actions.
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
    /// Checks if this rule is a wildcard rule (matches all errors).
    /// </summary>
    [JsonIgnore]
    public bool IsWildcard =>
        (ErrorTypes == null || ErrorTypes.Count == 0 || ErrorTypes.Contains("*")) &&
        (ErrorCodes == null || ErrorCodes.Count == 0 || ErrorCodes.Contains("*"));

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
            if (ErrorTypes is { Count: > 0 } && !ErrorTypes.Contains("*"))
                score += ErrorTypes.Count;
            if (ErrorCodes is { Count: > 0 } && !ErrorCodes.Contains("*"))
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
    /// Checks if this rule matches the given error code (domain error code as string).
    /// Supports wildcard matching with "*".
    /// </summary>
    /// <param name="errorCode">The error code to check (e.g., "Task:400007", null if not applicable).</param>
    /// <returns>True if the rule matches the error code.</returns>
    public bool MatchesErrorCode(string? errorCode)
    {
        if (ErrorCodes == null || ErrorCodes.Count == 0 || ErrorCodes.Contains("*"))
            return true;

        if (string.IsNullOrEmpty(errorCode))
            return false;

        return ErrorCodes.Contains(errorCode, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if this rule matches the given HTTP status code.
    /// Converts the status code to string and checks against ErrorCodes.
    /// </summary>
    /// <param name="statusCode">The HTTP status code to check (null if not applicable).</param>
    /// <returns>True if the rule matches the status code.</returns>
    public bool MatchesStatusCode(int? statusCode)
    {
        if (statusCode == null)
            return false;

        return MatchesErrorCode(statusCode.Value.ToString());
    }

    /// <summary>
    /// Checks if this rule matches either the error code or status code.
    /// </summary>
    /// <param name="errorCode">The domain error code (e.g., "Task:400007").</param>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <returns>True if the rule matches either code.</returns>
    public bool MatchesAnyCode(string? errorCode, int? statusCode)
    {
        // If wildcard, match everything
        if (ErrorCodes == null || ErrorCodes.Count == 0 || ErrorCodes.Contains("*"))
            return true;

        // Check error code match
        if (!string.IsNullOrEmpty(errorCode) && 
            ErrorCodes.Contains(errorCode, StringComparer.OrdinalIgnoreCase))
            return true;

        // Check status code match
        if (statusCode.HasValue && 
            ErrorCodes.Contains(statusCode.Value.ToString(), StringComparer.OrdinalIgnoreCase))
            return true;

        return false;
    }
}

