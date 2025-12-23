using System.Text.Json.Serialization;

namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Represents the retry state for a pipeline step.
/// Persisted in Instance.ExtraProperties to survive process restarts.
/// </summary>
public sealed record StepRetryState
{
    /// <summary>
    /// Key used to store retry state in Instance.ExtraProperties.
    /// </summary>
    public const string ExtraPropertiesKey = "StepRetryState";

    /// <summary>
    /// The order of the step to resume from.
    /// </summary>
    [JsonPropertyName("stepOrder")]
    public int StepOrder { get; init; }

    /// <summary>
    /// The name of the step (for logging purposes).
    /// </summary>
    [JsonPropertyName("stepName")]
    public string StepName { get; init; } = string.Empty;

    /// <summary>
    /// The current retry attempt number (0-based).
    /// </summary>
    [JsonPropertyName("currentAttempt")]
    public int CurrentAttempt { get; init; }

    /// <summary>
    /// The maximum number of retries allowed.
    /// </summary>
    [JsonPropertyName("maxRetries")]
    public int MaxRetries { get; init; }

    /// <summary>
    /// The error code from the last failure.
    /// </summary>
    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; init; }

    /// <summary>
    /// The error message from the last failure.
    /// </summary>
    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// The timestamp of the last retry attempt.
    /// </summary>
    [JsonPropertyName("lastAttemptAt")]
    public DateTimeOffset LastAttemptAt { get; init; }

    /// <summary>
    /// The transition key that was being executed.
    /// </summary>
    [JsonPropertyName("transitionKey")]
    public string? TransitionKey { get; init; }

    /// <summary>
    /// The workflow key.
    /// </summary>
    [JsonPropertyName("workflowKey")]
    public string WorkflowKey { get; init; } = string.Empty;

    /// <summary>
    /// The workflow version.
    /// </summary>
    [JsonPropertyName("workflowVersion")]
    public string WorkflowVersion { get; init; } = string.Empty;

    /// <summary>
    /// Indicates whether the maximum retry count has been exceeded.
    /// </summary>
    [JsonIgnore]
    public bool IsMaxRetriesExceeded => CurrentAttempt >= MaxRetries;

    /// <summary>
    /// Creates a new retry state with incremented attempt count.
    /// </summary>
    public StepRetryState IncrementAttempt() => this with
    {
        CurrentAttempt = CurrentAttempt + 1,
        LastAttemptAt = DateTimeOffset.UtcNow
    };

    /// <summary>
    /// Creates a new retry state for a step.
    /// </summary>
    public static StepRetryState Create(
        int stepOrder,
        string stepName,
        int maxRetries,
        string? errorCode,
        string? errorMessage,
        string? transitionKey,
        string workflowKey,
        string workflowVersion)
    {
        return new StepRetryState
        {
            StepOrder = stepOrder,
            StepName = stepName,
            CurrentAttempt = 0,
            MaxRetries = maxRetries,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            LastAttemptAt = DateTimeOffset.UtcNow,
            TransitionKey = transitionKey,
            WorkflowKey = workflowKey,
            WorkflowVersion = workflowVersion
        };
    }

    /// <summary>
    /// Creates a StepRetryState from a dictionary (deserialized from ExtraProperties).
    /// </summary>
    public static StepRetryState? FromDictionary(IDictionary<string, object?> dict)
    {
        if (dict == null || !dict.ContainsKey("stepOrder"))
            return null;

        return new StepRetryState
        {
            StepOrder = Convert.ToInt32(GetValue(dict, "stepOrder", 0)),
            StepName = GetValue(dict, "stepName", null)?.ToString() ?? string.Empty,
            CurrentAttempt = Convert.ToInt32(GetValue(dict, "currentAttempt", 0)),
            MaxRetries = Convert.ToInt32(GetValue(dict, "maxRetries", 0)),
            ErrorCode = GetValue(dict, "errorCode", null)?.ToString(),
            ErrorMessage = GetValue(dict, "errorMessage", null)?.ToString(),
            LastAttemptAt = dict.TryGetValue("lastAttemptAt", out var lastAttempt) && lastAttempt != null
                ? DateTimeOffset.Parse(lastAttempt.ToString()!)
                : DateTimeOffset.UtcNow,
            TransitionKey = GetValue(dict, "transitionKey", null)?.ToString(),
            WorkflowKey = GetValue(dict, "workflowKey", null)?.ToString() ?? string.Empty,
            WorkflowVersion = GetValue(dict, "workflowVersion", null)?.ToString() ?? string.Empty
        };
    }

    private static object? GetValue(IDictionary<string, object?> dict, string key, object? defaultValue)
    {
        return dict.TryGetValue(key, out var value) ? value : defaultValue;
    }

    /// <summary>
    /// Converts this retry state to a dictionary for storage in ExtraProperties.
    /// </summary>
    public Dictionary<string, object?> ToDictionary()
    {
        return new Dictionary<string, object?>
        {
            ["stepOrder"] = StepOrder,
            ["stepName"] = StepName,
            ["currentAttempt"] = CurrentAttempt,
            ["maxRetries"] = MaxRetries,
            ["errorCode"] = ErrorCode,
            ["errorMessage"] = ErrorMessage,
            ["lastAttemptAt"] = LastAttemptAt.ToString("O"),
            ["transitionKey"] = TransitionKey,
            ["workflowKey"] = WorkflowKey,
            ["workflowVersion"] = WorkflowVersion
        };
    }
}

