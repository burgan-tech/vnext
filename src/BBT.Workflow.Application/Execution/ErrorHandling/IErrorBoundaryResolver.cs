using BBT.Workflow.Definitions;

namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Resolves error boundary policies using hierarchical lookup: Task -> State -> Global.
/// Single responsibility: find matching boundary rule for a given error.
/// </summary>
public interface IErrorBoundaryResolver
{
    /// <summary>
    /// Resolves the appropriate boundary action for an error using hierarchical lookup.
    /// Resolution order: Task boundary first, then State, then Global.
    /// </summary>
    /// <param name="error">The normalized error to match against boundary rules.</param>
    /// <param name="boundaryChain">The pre-compiled boundary chain containing Task, State, and Global boundaries.</param>
    /// <returns>The resolution result containing the matched rule and level, or unhandled if no match.</returns>
    BoundaryResolutionResult Resolve(
        NormalizedError error,
        CompiledBoundaryChain boundaryChain);

    /// <summary>
    /// Resolves boundary using raw ErrorBoundary definitions.
    /// Compiles boundaries on-the-fly and performs hierarchical lookup.
    /// </summary>
    /// <param name="error">The normalized error to match against boundary rules.</param>
    /// <param name="taskBoundary">Optional task-level error boundary.</param>
    /// <param name="stateBoundary">Optional state-level error boundary.</param>
    /// <param name="globalBoundary">Optional global (workflow) error boundary.</param>
    /// <returns>The resolution result containing the matched rule and level, or unhandled if no match.</returns>
    BoundaryResolutionResult Resolve(
        NormalizedError error,
        ErrorBoundary? taskBoundary,
        ErrorBoundary? stateBoundary,
        ErrorBoundary? globalBoundary);

    /// <summary>
    /// Resolves boundary excluding specified actions.
    /// Used after retry exhaustion to find fallback actions.
    /// </summary>
    /// <param name="error">The normalized error to match against boundary rules.</param>
    /// <param name="boundaryChain">The pre-compiled boundary chain.</param>
    /// <param name="excludeActions">Actions to exclude from matching.</param>
    /// <returns>The resolution result, or unhandled if no matching non-excluded rule found.</returns>
    BoundaryResolutionResult ResolveExcluding(
        NormalizedError error,
        CompiledBoundaryChain boundaryChain,
        params ErrorAction[] excludeActions);
}

/// <summary>
/// Result of error boundary resolution.
/// Contains the matched rule, resolution level, and whether handling was found.
/// </summary>
public sealed record BoundaryResolutionResult
{
    /// <summary>
    /// Gets a value indicating whether a matching boundary rule was found.
    /// </summary>
    public bool IsHandled { get; init; }

    /// <summary>
    /// Gets the matched compiled rule, if any.
    /// </summary>
    public CompiledRule? MatchedRule { get; init; }

    /// <summary>
    /// Gets the level at which the boundary was resolved.
    /// </summary>
    public ErrorBoundaryLevel? ResolvedAtLevel { get; init; }

    /// <summary>
    /// Gets the error action to execute.
    /// </summary>
    public ErrorAction Action => MatchedRule?.Rule.Action ?? ErrorAction.Abort;

    /// <summary>
    /// Gets the retry policy if action is Retry.
    /// </summary>
    public RetryPolicy? RetryPolicy => MatchedRule?.Rule.RetryPolicy;

    /// <summary>
    /// Gets the transition key if action requires a transition.
    /// </summary>
    public string? TransitionKey => MatchedRule?.Rule.Transition;

    /// <summary>
    /// Creates an unhandled result (no matching boundary found).
    /// </summary>
    public static BoundaryResolutionResult Unhandled() => new()
    {
        IsHandled = false,
        MatchedRule = null,
        ResolvedAtLevel = null
    };

    /// <summary>
    /// Creates a handled result with the matched rule and level.
    /// </summary>
    /// <param name="rule">The matched compiled rule.</param>
    /// <param name="level">The level where the rule was found.</param>
    public static BoundaryResolutionResult Handled(CompiledRule rule, ErrorBoundaryLevel level) => new()
    {
        IsHandled = true,
        MatchedRule = rule,
        ResolvedAtLevel = level
    };
}

