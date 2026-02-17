using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Configures timeout-specific error handling policy.
/// Used when a task or transition times out.
/// </summary>
public sealed record TimeoutPolicy
{
    /// <summary>
    /// The action to take when a timeout occurs.
    /// </summary>
    [JsonPropertyName("action")]
    public ErrorAction Action { get; init; } = ErrorAction.Abort;

    /// <summary>
    /// Default retry policy for timeout errors.
    /// Only used when Action is Retry.
    /// </summary>
    [JsonPropertyName("defaultRetryPolicy")]
    public RetryPolicy? DefaultRetryPolicy { get; init; }

    /// <summary>
    /// Optional transition key to trigger on timeout.
    /// Used with Rollback/Notify actions.
    /// </summary>
    [JsonPropertyName("transition")]
    public string? Transition { get; init; }

    /// <summary>
    /// Creates a default timeout policy that aborts execution.
    /// </summary>
    public static TimeoutPolicy Default => new();

    /// <summary>
    /// Creates a timeout policy that retries with the specified retry policy.
    /// </summary>
    /// <param name="retryPolicy">The retry policy to use.</param>
    public static TimeoutPolicy WithRetry(RetryPolicy retryPolicy) => new()
    {
        Action = ErrorAction.Retry,
        DefaultRetryPolicy = retryPolicy
    };
}

