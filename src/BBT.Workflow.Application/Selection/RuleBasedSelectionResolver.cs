using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Functions.Contracts;
using BBT.Workflow.Tasks.Coordinator;

namespace BBT.Workflow.Selection;

/// <summary>
/// Evaluates a rule-based selection through <see cref="ITaskConditionService"/> - the same compilation
/// and execution path state and transition view rules take. This class never compiles a script itself.
/// </summary>
public sealed class RuleBasedSelectionResolver(
    ITaskConditionService taskConditionService) : IRuleBasedSelectionResolver
{
    /// <inheritdoc />
    public async Task<Result<SelectionMatch?>> ResolveAsync(
        IReadOnlyList<SelectionCandidate> candidates,
        LazyScriptContext scriptContext,
        Action<Reference, string>? onRuleEvaluationFailed = null,
        CancellationToken cancellationToken = default)
    {
        if (candidates.Count == 0)
            return Result<SelectionMatch?>.Ok(null);

        foreach (var candidate in candidates)
        {
            // A rule-less entry is the declared fallback - it always wins from here on, so nothing
            // after it can ever be reached (the component validators enforce that it is last).
            if (candidate.Rule == null)
                return Result<SelectionMatch?>.Ok(
                    new SelectionMatch(candidate.Reference, MatchedByRule: false, candidate.LoadData));

            var contextResult = await scriptContext.GetAsync(cancellationToken);
            if (!contextResult.IsSuccess)
                return Result<SelectionMatch?>.Fail(contextResult.Error);

            var ruleResult = await taskConditionService.ExecuteConditionAsync(
                candidate.Rule,
                contextResult.Value!,
                cancellationToken);

            if (ruleResult is { IsSuccess: true, Value: true })
                return Result<SelectionMatch?>.Ok(
                    new SelectionMatch(candidate.Reference, MatchedByRule: true, candidate.LoadData));

            // A rule that threw tells us nothing about the entry - skip it and let a later entry or the
            // fallback answer, exactly as view selection does.
            if (!ruleResult.IsSuccess)
            {
                onRuleEvaluationFailed?.Invoke(
                    candidate.Reference,
                    ruleResult.Error.Message ?? "unknown");
            }
        }

        // Every entry carried a rule and none matched: the definition declares no component for this
        // request. Not an error - the caller decides what an absent selection means.
        return Result<SelectionMatch?>.Ok(null);
    }
}
