using BBT.Workflow.Definitions;

namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Represents the result of error policy resolution.
/// Contains the matched rule and the level at which it was found.
/// </summary>
public sealed record ResolvedPolicy
{
    /// <summary>
    /// The boundary level where the matching rule was found.
    /// </summary>
    public ErrorBoundaryLevel Level { get; init; }

    /// <summary>
    /// The matching error handler rule.
    /// </summary>
    public required ErrorHandlerRule Rule { get; init; }

    /// <summary>
    /// The effective priority of the matched rule.
    /// </summary>
    public int EffectivePriority { get; init; }

    /// <summary>
    /// The error boundary containing the matched rule.
    /// </summary>
    public ErrorBoundary? Boundary { get; init; }

    /// <summary>
    /// Gets a value indicating whether this policy allows retry.
    /// </summary>
    public bool AllowsRetry => Rule.Action == ErrorAction.Retry && Rule.RetryPolicy != null;

    /// <summary>
    /// Gets a value indicating whether this policy triggers a transition.
    /// </summary>
    public bool HasTransition => !string.IsNullOrEmpty(Rule.Transition);

    /// <summary>
    /// Creates a ResolvedPolicy for the given rule at the specified level.
    /// </summary>
    public static ResolvedPolicy Create(
        ErrorHandlerRule rule,
        ErrorBoundaryLevel level,
        ErrorBoundary? boundary = null)
    {
        return new ResolvedPolicy
        {
            Rule = rule,
            Level = level,
            EffectivePriority = rule.EffectivePriority,
            Boundary = boundary
        };
    }
}

