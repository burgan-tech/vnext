using BBT.Aether.Results;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Represents the outcome of applying an error action.
/// </summary>
public sealed record ErrorActionResult
{
    /// <summary>
    /// The action that was applied.
    /// </summary>
    public ErrorAction Action { get; init; }

    /// <summary>
    /// Whether the pipeline should continue after this action.
    /// </summary>
    public bool ShouldContinue { get; init; }

    /// <summary>
    /// Whether a retry should be attempted.
    /// </summary>
    public bool ShouldRetry { get; init; }

    /// <summary>
    /// The delay before retry (if ShouldRetry is true).
    /// </summary>
    public TimeSpan RetryDelay { get; init; }

    /// <summary>
    /// Transition key to trigger (if any).
    /// </summary>
    public string? TransitionKey { get; init; }

    /// <summary>
    /// The error to propagate (if action failed or Abort was applied).
    /// </summary>
    public Error? Error { get; init; }

    /// <summary>
    /// Gets a value indicating whether this result has an error to propagate.
    /// </summary>
    public bool HasError => Error != null;

    /// <summary>
    /// Gets a value indicating whether a transition should be triggered.
    /// </summary>
    public bool HasTransition => !string.IsNullOrEmpty(TransitionKey);

    /// <summary>
    /// Creates a result indicating the pipeline should continue.
    /// </summary>
    public static ErrorActionResult Continue() => new()
    {
        Action = ErrorAction.Ignore,
        ShouldContinue = true
    };

    /// <summary>
    /// Creates a result indicating the pipeline should abort.
    /// </summary>
    public static ErrorActionResult Abort(Error error, string? transitionKey = null) => new()
    {
        Action = ErrorAction.Abort,
        ShouldContinue = false,
        Error = error,
        TransitionKey = transitionKey
    };

    /// <summary>
    /// Creates a result indicating a retry should be attempted.
    /// </summary>
    public static ErrorActionResult Retry(TimeSpan delay) => new()
    {
        Action = ErrorAction.Retry,
        ShouldContinue = false,
        ShouldRetry = true,
        RetryDelay = delay
    };

    /// <summary>
    /// Creates a result indicating max retries exceeded.
    /// </summary>
    public static ErrorActionResult RetryExhausted(Error error) => new()
    {
        Action = ErrorAction.Retry,
        ShouldContinue = false,
        ShouldRetry = false,
        Error = error
    };

    /// <summary>
    /// Creates a result indicating a transition should be triggered.
    /// </summary>
    public static ErrorActionResult Transition(string transitionKey) => new()
    {
        Action = ErrorAction.Rollback,
        ShouldContinue = false,
        TransitionKey = transitionKey
    };

    /// <summary>
    /// Creates a result indicating a notification was sent.
    /// </summary>
    public static ErrorActionResult Notified(string? transitionKey = null) => new()
    {
        Action = ErrorAction.Notify,
        ShouldContinue = transitionKey == null,
        TransitionKey = transitionKey
    };
}

