using BBT.Workflow.Definitions;

namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Pre-compiled chain of error boundaries for hierarchical resolution.
/// Compiles Task, State, and Global boundaries once for efficient matching.
/// </summary>
/// <remarks>
/// Resolution order:
/// 1. Task boundary (highest priority)
/// 2. State boundary (parent scope)
/// 3. Global/Workflow boundary (fallback)
/// 
/// Each level is compiled once and cached for the duration of the execution context.
/// </remarks>
public sealed class CompiledBoundaryChain
{
    /// <summary>
    /// Gets the compiled task-level boundary (highest priority).
    /// </summary>
    public CompiledBoundary? TaskBoundary { get; init; }

    /// <summary>
    /// Gets the compiled state-level boundary.
    /// </summary>
    public CompiledBoundary? StateBoundary { get; init; }

    /// <summary>
    /// Gets the compiled global/workflow-level boundary (fallback).
    /// </summary>
    public CompiledBoundary? GlobalBoundary { get; init; }

    /// <summary>
    /// Gets a value indicating whether any boundary is defined in the chain.
    /// </summary>
    public bool HasAnyBoundary =>
        TaskBoundary != null || StateBoundary != null || GlobalBoundary != null;

    /// <summary>
    /// Compiles error boundaries from Task, State, and Global levels into a chain.
    /// </summary>
    /// <param name="taskBoundary">Optional task-level error boundary.</param>
    /// <param name="stateBoundary">Optional state-level error boundary.</param>
    /// <param name="globalBoundary">Optional global/workflow error boundary.</param>
    /// <returns>A compiled boundary chain for efficient hierarchical resolution.</returns>
    public static CompiledBoundaryChain Compile(
        ErrorBoundary? taskBoundary,
        ErrorBoundary? stateBoundary,
        ErrorBoundary? globalBoundary)
    {
        return new CompiledBoundaryChain
        {
            TaskBoundary = CompiledBoundary.Compile(taskBoundary, ErrorBoundaryLevel.Task),
            StateBoundary = CompiledBoundary.Compile(stateBoundary, ErrorBoundaryLevel.State),
            GlobalBoundary = CompiledBoundary.Compile(globalBoundary, ErrorBoundaryLevel.Global)
        };
    }

    /// <summary>
    /// Creates an empty chain with no boundaries.
    /// </summary>
    public static CompiledBoundaryChain Empty => new();

    /// <summary>
    /// Finds the first matching rule across all boundary levels.
    /// Returns the match with its level, or null if no match found.
    /// </summary>
    /// <param name="exceptionTypeName">The exception type name to match.</param>
    /// <param name="errorCode">The error code to match.</param>
    /// <param name="statusCode">The HTTP status code to match.</param>
    /// <returns>Tuple of matching rule and level, or null if no match.</returns>
    public (CompiledRule Rule, ErrorBoundaryLevel Level)? FindMatch(
        string? exceptionTypeName,
        string? errorCode,
        int? statusCode)
    {
        return FindMatchInternal(boundary => boundary.FindMatch(
            exceptionTypeName ?? string.Empty,
            errorCode,
            statusCode));
    }

    /// <summary>
    /// Finds the first matching rule for a normalized error.
    /// </summary>
    /// <param name="error">The normalized error to match.</param>
    /// <returns>Tuple of matching rule and level, or null if no match.</returns>
    public (CompiledRule Rule, ErrorBoundaryLevel Level)? FindMatch(NormalizedError error)
    {
        return FindMatch(error.ExceptionType, error.Code, error.StatusCode);
    }

    /// <summary>
    /// Finds the first matching rule excluding specified actions.
    /// Used after retry exhaustion to find fallback actions like Abort, Log, etc.
    /// </summary>
    /// <param name="error">The normalized error to match.</param>
    /// <param name="excludeActions">Actions to exclude from matching.</param>
    /// <returns>Tuple of matching rule and level, or null if no match found.</returns>
    public (CompiledRule Rule, ErrorBoundaryLevel Level)? FindMatchExcluding(
        NormalizedError error,
        params ErrorAction[] excludeActions)
    {
        var excludeSet = new HashSet<ErrorAction>(excludeActions);

        return FindMatchInternal(boundary => boundary.FindMatchExcluding(
            error.ExceptionType ?? string.Empty,
            error.OriginalCode,
            error.StatusCode,
            excludeSet));
    }

    /// <summary>
    /// Helper to find match across hierarchy (Task -> State -> Global).
    /// </summary>
    private (CompiledRule Rule, ErrorBoundaryLevel Level)? FindMatchInternal(
        Func<CompiledBoundary, CompiledRule?> findFunc)
    {
        // 1. Try Task-level boundary
        if (TaskBoundary != null)
        {
            var match = findFunc(TaskBoundary);
            if (match != null)
                return (match, ErrorBoundaryLevel.Task);
        }

        // 2. Try State-level boundary
        if (StateBoundary != null)
        {
            var match = findFunc(StateBoundary);
            if (match != null)
                return (match, ErrorBoundaryLevel.State);
        }

        // 3. Try Global-level boundary
        if (GlobalBoundary != null)
        {
            var match = findFunc(GlobalBoundary);
            if (match != null)
                return (match, ErrorBoundaryLevel.Global);
        }

        return null;
    }
}

