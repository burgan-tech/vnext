using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Configures retry behavior for error handling.
/// Supports fixed and exponential backoff strategies.
/// </summary>
public sealed record RetryPolicy
{
    /// <summary>
    /// The maximum number of retry attempts before giving up.
    /// </summary>
    [JsonPropertyName("maxRetries")]
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// The initial delay before the first retry attempt.
    /// For exponential backoff, this is the base delay.
    /// </summary>
    [JsonPropertyName("initialDelay")]
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The type of backoff strategy to use between retries.
    /// </summary>
    [JsonPropertyName("backoffType")]
    public BackoffType BackoffType { get; init; } = BackoffType.Exponential;

    /// <summary>
    /// The multiplier for exponential backoff.
    /// Each retry delay = previousDelay * BackoffMultiplier.
    /// Only used when BackoffType is Exponential.
    /// </summary>
    [JsonPropertyName("backoffMultiplier")]
    public double BackoffMultiplier { get; init; } = 2.0;

    /// <summary>
    /// The maximum delay between retry attempts.
    /// Prevents delays from growing unbounded in exponential backoff.
    /// </summary>
    [JsonPropertyName("maxDelay")]
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether to add random jitter to retry delays.
    /// Helps prevent thundering herd problems when multiple tasks retry simultaneously.
    /// </summary>
    [JsonPropertyName("useJitter")]
    public bool UseJitter { get; init; } = true;

    /// <summary>
    /// Calculates the delay for a specific retry attempt.
    /// </summary>
    /// <param name="attempt">The current retry attempt number (1-based).</param>
    /// <returns>The delay to wait before the retry.</returns>
    public TimeSpan CalculateDelay(int attempt)
    {
        if (attempt <= 0) return TimeSpan.Zero;

        var delay = BackoffType switch
        {
            BackoffType.Fixed => InitialDelay,
            BackoffType.Exponential => TimeSpan.FromMilliseconds(
                InitialDelay.TotalMilliseconds * Math.Pow(BackoffMultiplier, attempt - 1)),
            _ => InitialDelay
        };

        return delay > MaxDelay ? MaxDelay : delay;
    }

    /// <summary>
    /// Creates a default retry policy with exponential backoff.
    /// </summary>
    public static RetryPolicy Default => new();

    /// <summary>
    /// Creates a retry policy with no retries (immediate failure).
    /// </summary>
    public static RetryPolicy NoRetry => new() { MaxRetries = 0 };
}

