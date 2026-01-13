using BBT.Workflow.Definitions;

namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Pre-compiled error boundary with sorted rules for efficient matching.
/// Rules are sorted by: priority (ascending) -> specificity (descending) -> definition order.
/// </summary>
public sealed class CompiledBoundary
{
    /// <summary>
    /// The original error boundary definition.
    /// </summary>
    public required ErrorBoundary Boundary { get; init; }

    /// <summary>
    /// Rules sorted by matching priority.
    /// Lower priority values are matched first.
    /// </summary>
    public required IReadOnlyList<CompiledRule> SortedRules { get; init; }

    /// <summary>
    /// The level of this boundary in the hierarchy.
    /// </summary>
    public ErrorBoundaryLevel Level { get; init; }

    /// <summary>
    /// Finds the first matching rule for the given error context.
    /// </summary>
    /// <param name="exceptionTypeName">The exception type name to match.</param>
    /// <param name="errorCode">The error code to match.</param>
    /// <param name="statusCode">The HTTP status code to match.</param>
    /// <returns>The matching rule, or null if no rule matches.</returns>
    public CompiledRule? FindMatch(string exceptionTypeName, string? errorCode, int? statusCode)
    {
        return SortedRules.FirstOrDefault(rule => rule.Matches(exceptionTypeName, errorCode, statusCode));
    }

    /// <summary>
    /// Finds the first matching rule excluding specified actions.
    /// Used after retry exhaustion to find fallback actions.
    /// </summary>
    /// <param name="exceptionTypeName">The exception type name to match.</param>
    /// <param name="errorCode">The error code to match.</param>
    /// <param name="statusCode">The HTTP status code to match.</param>
    /// <param name="excludeActions">Set of actions to exclude from matching.</param>
    /// <returns>The matching rule, or null if no rule matches.</returns>
    public CompiledRule? FindMatchExcluding(
        string exceptionTypeName,
        string? errorCode,
        int? statusCode,
        HashSet<ErrorAction> excludeActions)
    {
        foreach (var compiledRule in SortedRules)
        {
            // Skip excluded actions
            if (excludeActions.Contains(compiledRule.Rule.Action))
                continue;

            if (compiledRule.Matches(exceptionTypeName, errorCode, statusCode))
            {
                return compiledRule;
            }
        }

        return null;
    }

    /// <summary>
    /// Compiles an ErrorBoundary into a CompiledBoundary with sorted rules.
    /// </summary>
    public static CompiledBoundary? Compile(ErrorBoundary? boundary, ErrorBoundaryLevel level)
    {
        if (boundary == null || boundary.OnError.Count == 0)
            return null;

        var sortedRules = boundary.OnError
            .Select((rule, index) => new CompiledRule
            {
                Rule = rule,
                DefinitionOrder = index,
                EffectivePriority = rule.EffectivePriority,
                Specificity = rule.Specificity
            })
            .OrderBy(r => r.EffectivePriority)
            .ThenByDescending(r => r.Specificity)
            .ThenBy(r => r.DefinitionOrder)
            .ToList();

        return new CompiledBoundary
        {
            Boundary = boundary,
            SortedRules = sortedRules,
            Level = level
        };
    }
}

/// <summary>
/// A compiled error handler rule with pre-computed matching properties.
/// </summary>
public sealed class CompiledRule
{
    /// <summary>
    /// The original error handler rule.
    /// </summary>
    public required ErrorHandlerRule Rule { get; init; }

    /// <summary>
    /// The definition order of the rule in the original boundary.
    /// Used as a tie-breaker when priority and specificity are equal.
    /// </summary>
    public int DefinitionOrder { get; init; }

    /// <summary>
    /// The effective priority (considering wildcard status).
    /// </summary>
    public int EffectivePriority { get; init; }

    /// <summary>
    /// The specificity score of the rule.
    /// </summary>
    public int Specificity { get; init; }

    /// <summary>
    /// Checks if this rule matches the given error properties.
    /// Uses OR logic when both errorTypes and errorCodes are defined:
    /// - If only errorTypes is defined → must match exception type
    /// - If only errorCodes is defined → must match error/status code
    /// - If both are defined → matches if EITHER condition is met (OR)
    /// - If neither is defined (wildcard) → matches everything
    /// </summary>
    public bool Matches(string? exceptionTypeName, string? errorCode, int? statusCode)
    {
        var hasTypeFilter = Rule.ErrorTypes is { Count: > 0 } && 
                            !Rule.ErrorTypes.Contains("*");
        
        var hasCodeFilter = Rule.ErrorCodes is { Count: > 0 } && 
                            !Rule.ErrorCodes.Contains("*");

        // No filters defined - matches everything (wildcard)
        if (!hasTypeFilter && !hasCodeFilter)
            return true;

        // Only type filter defined - must match exception type
        if (hasTypeFilter && !hasCodeFilter)
            return Rule.MatchesExceptionType(exceptionTypeName ?? string.Empty);

        // Only code filter defined - must match code/status
        if (!hasTypeFilter && hasCodeFilter)
            return Rule.MatchesAnyCode(errorCode, statusCode);

        // Both filters defined - match EITHER (OR logic)
        var matchesType = !string.IsNullOrEmpty(exceptionTypeName) && 
                         Rule.MatchesExceptionType(exceptionTypeName);
        var matchesCode = Rule.MatchesAnyCode(errorCode, statusCode);
        
        return matchesType || matchesCode;
    }
}

