using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Container for error handling policies at different levels (Task, State, Global).
/// Provides deterministic error handling with priority-based rule matching.
/// </summary>
public sealed record ErrorBoundary
{
    /// <summary>
    /// Parameterless constructor for EF Core and deserialization.
    /// </summary>
    public ErrorBoundary()
    {
        OnError = [];
        OnTimeout = null;
    }

    [JsonConstructor]
    public ErrorBoundary(
        IReadOnlyList<ErrorHandlerRule>? onError,
        TimeoutPolicy? onTimeout)
    {
        OnError = onError ?? [];
        OnTimeout = onTimeout;
    }

    /// <summary>
    /// List of error handling rules. Evaluated in priority order.
    /// First matching rule is applied.
    /// </summary>
    [JsonPropertyName("onError")]
    public IReadOnlyList<ErrorHandlerRule> OnError { get; init; }

    /// <summary>
    /// Timeout-specific error handling policy.
    /// Applied when an operation times out.
    /// </summary>
    [JsonPropertyName("onTimeout")]
    public TimeoutPolicy? OnTimeout { get; init; }

    /// <summary>
    /// Gets a value indicating whether this boundary has any error rules defined.
    /// </summary>
    [JsonIgnore]
    public bool HasErrorRules => OnError.Count > 0;

    /// <summary>
    /// Gets a value indicating whether this boundary has a timeout policy defined.
    /// </summary>
    [JsonIgnore]
    public bool HasTimeoutPolicy => OnTimeout != null;

    /// <summary>
    /// Creates an empty error boundary with no rules.
    /// </summary>
    public static ErrorBoundary Empty => new([], null);

    /// <summary>
    /// Creates an error boundary with the specified error rules.
    /// </summary>
    /// <param name="rules">The error handling rules.</param>
    public static ErrorBoundary WithRules(params ErrorHandlerRule[] rules) =>
        new(rules, null);

    /// <summary>
    /// Creates an error boundary with a single wildcard rule that ignores all errors.
    /// </summary>
    public static ErrorBoundary IgnoreAll => new(
        [new ErrorHandlerRule { Action = ErrorAction.Ignore, Priority = ErrorHandlerRule.WildcardPriority }],
        null);

    /// <summary>
    /// Creates an error boundary with a single wildcard rule that aborts on all errors.
    /// </summary>
    public static ErrorBoundary AbortAll => new(
        [new ErrorHandlerRule { Action = ErrorAction.Abort, Priority = ErrorHandlerRule.WildcardPriority }],
        null);

    /// <summary>
    /// Creates a builder for constructing error boundaries.
    /// </summary>
    public static ErrorBoundaryBuilder Builder() => new();
}

/// <summary>
/// Fluent builder for constructing ErrorBoundary instances.
/// </summary>
public sealed class ErrorBoundaryBuilder
{
    private readonly List<ErrorHandlerRule> _rules = [];
    private TimeoutPolicy? _timeoutPolicy;

    /// <summary>
    /// Adds an error handling rule.
    /// </summary>
    public ErrorBoundaryBuilder AddRule(ErrorHandlerRule rule)
    {
        _rules.Add(rule);
        return this;
    }

    /// <summary>
    /// Adds an error handling rule for specific exception types.
    /// </summary>
    public ErrorBoundaryBuilder OnError(
        ErrorAction action,
        string? transition = null,
        int priority = ErrorHandlerRule.DefaultPriority,
        params string[] errorTypes)
    {
        _rules.Add(new ErrorHandlerRule
        {
            Action = action,
            ErrorTypes = errorTypes.Length > 0 ? errorTypes : null,
            Transition = transition,
            Priority = priority
        });
        return this;
    }

    /// <summary>
    /// Adds a retry rule for specific exception types.
    /// </summary>
    public ErrorBoundaryBuilder OnErrorRetry(
        RetryPolicy retryPolicy,
        int priority = ErrorHandlerRule.DefaultPriority,
        params string[] errorTypes)
    {
        _rules.Add(new ErrorHandlerRule
        {
            Action = ErrorAction.Retry,
            ErrorTypes = errorTypes.Length > 0 ? errorTypes : null,
            RetryPolicy = retryPolicy,
            Priority = priority
        });
        return this;
    }

    /// <summary>
    /// Sets the timeout policy.
    /// </summary>
    public ErrorBoundaryBuilder OnTimeout(TimeoutPolicy policy)
    {
        _timeoutPolicy = policy;
        return this;
    }

    /// <summary>
    /// Builds the error boundary.
    /// </summary>
    public ErrorBoundary Build() => new(
        _rules.Count > 0 ? _rules.ToArray() : null,
        _timeoutPolicy);
}

