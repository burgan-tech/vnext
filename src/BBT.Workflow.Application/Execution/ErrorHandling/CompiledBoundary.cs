using BBT.Workflow.Definitions;

namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Pre-compiled error boundary with sorted rules for efficient matching.
/// Used for caching to avoid repeated sorting and matching overhead.
/// </summary>
public sealed class CompiledBoundary
{
    /// <summary>
    /// The original error boundary.
    /// </summary>
    public ErrorBoundary Boundary { get; }

    /// <summary>
    /// Rules sorted by priority and specificity for matching.
    /// </summary>
    public IReadOnlyList<ErrorHandlerRule> SortedRules { get; }

    /// <summary>
    /// The boundary level (Task, State, Global).
    /// </summary>
    public ErrorBoundaryLevel Level { get; }

    /// <summary>
    /// Context key for logging (e.g., task key, state key, or "global").
    /// </summary>
    public string ContextKey { get; }

    private CompiledBoundary(
        ErrorBoundary boundary,
        IReadOnlyList<ErrorHandlerRule> sortedRules,
        ErrorBoundaryLevel level,
        string contextKey)
    {
        Boundary = boundary;
        SortedRules = sortedRules;
        Level = level;
        ContextKey = contextKey;
    }

    /// <summary>
    /// Attempts to find a matching rule for the given error context.
    /// </summary>
    /// <param name="errorContext">The error context to match against.</param>
    /// <param name="excludeRetry">If true, excludes Retry action rules from matching.</param>
    /// <returns>The first matching rule, or null if no match found.</returns>
    public ErrorHandlerRule? FindMatch(ErrorContext errorContext, bool excludeRetry = false)
    {
        // If timeout and we have a timeout policy, handle it first
        if (errorContext.IsTimeout && Boundary.OnTimeout != null)
        {
            // Skip timeout policy if it's a retry action and excludeRetry is set
            if (!excludeRetry || Boundary.OnTimeout.Action != ErrorAction.Retry)
            {
                // Convert timeout policy to a synthetic rule
                return new ErrorHandlerRule
                {
                    Action = Boundary.OnTimeout.Action,
                    RetryPolicy = Boundary.OnTimeout.DefaultRetryPolicy,
                    Transition = Boundary.OnTimeout.Transition,
                    NotificationConfig = Boundary.OnTimeout.NotificationConfig,
                    Priority = 0 // Timeout rules have highest priority
                };
            }
        }

        // Match against error rules
        foreach (var rule in SortedRules)
        {
            // Skip Retry rules if excludeRetry is set
            if (excludeRetry && rule.Action == ErrorAction.Retry)
                continue;

            if (Matches(rule, errorContext))
            {
                return rule;
            }
        }

        return null;
    }

    /// <summary>
    /// Checks if a rule matches the given error context.
    /// Uses NormalizedError for consistent matching when available.
    /// </summary>
    private static bool Matches(ErrorHandlerRule rule, ErrorContext errorContext)
    {
        // Use NormalizedError for matching if available
        if (errorContext.NormalizedError != null)
        {
            return MatchesNormalized(rule, errorContext.NormalizedError);
        }
        
        // Fallback to legacy matching
        return rule.MatchesExceptionType(errorContext.ExceptionTypeName) &&
               rule.MatchesAnyCode(errorContext.ErrorCode, errorContext.StatusCode);
    }

    /// <summary>
    /// Checks if a rule matches a normalized error.
    /// Provides consistent matching across all error sources.
    /// </summary>
    private static bool MatchesNormalized(ErrorHandlerRule rule, NormalizedError normalizedError)
    {
        // Check exception type matching (null exception type matches wildcard or unspecified)
        if (!rule.MatchesExceptionType(normalizedError.ExceptionType ?? "Exception"))
            return false;

        // Check error codes - match against all matchable codes from normalized error
        // This includes: normalized code, status code string, original code
        var hasCodeMatch = false;
        var hasWildcard = rule.ErrorCodes?.Contains("*") ?? false;
        
        if (hasWildcard || rule.ErrorCodes == null || rule.ErrorCodes.Count == 0)
        {
            hasCodeMatch = true;
        }
        else
        {
            // Try matching against all possible codes
            foreach (var code in normalizedError.GetMatchableCodes())
            {
                if (rule.MatchesErrorCode(code))
                {
                    hasCodeMatch = true;
                    break;
                }
            }
            
            // Also try status code matching
            if (!hasCodeMatch && normalizedError.StatusCode.HasValue)
            {
                hasCodeMatch = rule.MatchesStatusCode(normalizedError.StatusCode);
            }
        }

        return hasCodeMatch;
    }

    /// <summary>
    /// Compiles an error boundary into a cached representation.
    /// </summary>
    /// <param name="boundary">The error boundary to compile.</param>
    /// <param name="level">The boundary level.</param>
    /// <param name="contextKey">Context key for logging.</param>
    /// <returns>The compiled boundary.</returns>
    public static CompiledBoundary Compile(
        ErrorBoundary boundary,
        ErrorBoundaryLevel level,
        string contextKey)
    {
        var sortedRules = boundary.OnError
            .OrderBy(r => r.EffectivePriority)
            .ThenByDescending(r => r.Specificity)
            .ThenBy(r => boundary.OnError.ToList().IndexOf(r)) // Stable sort by definition order
            .ToList();

        return new CompiledBoundary(boundary, sortedRules, level, contextKey);
    }
}

