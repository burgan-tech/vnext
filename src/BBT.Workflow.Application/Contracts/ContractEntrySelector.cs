using BBT.Aether.Results;
using BBT.Workflow.Functions.Contracts;
using BBT.Workflow.Tasks.Coordinator;

namespace BBT.Workflow.Contracts;

/// <inheritdoc />
public sealed class ContractEntrySelector(ITaskConditionService taskConditionService) : IContractEntrySelector
{
    /// <inheritdoc />
    public async Task<Result<ContractSelection?>> SelectAsync(
        IReadOnlyList<ContractCandidate> candidates,
        LazyScriptContext scriptContext,
        Action<ContractCandidate, string>? onRuleError,
        CancellationToken cancellationToken = default)
    {
        foreach (var candidate in candidates)
        {
            // A rule-less entry is the declared fallback - it always wins from here on, so nothing
            // after it can ever be reached (the component validator enforces that it is last).
            if (candidate.Rule == null)
                return Result<ContractSelection?>.Ok(
                    new ContractSelection(candidate.Reference, MatchedByRule: false, candidate.LoadData));

            var contextResult = await scriptContext.GetAsync(cancellationToken);
            if (!contextResult.IsSuccess)
                return Result<ContractSelection?>.Fail(contextResult.Error);

            var ruleResult = await taskConditionService.ExecuteConditionAsync(
                candidate.Rule,
                contextResult.Value!,
                cancellationToken);

            if (ruleResult is { IsSuccess: true, Value: true })
                return Result<ContractSelection?>.Ok(
                    new ContractSelection(candidate.Reference, MatchedByRule: true, candidate.LoadData));

            // A rule that threw tells us nothing about the entry - skip it and let a later entry or the
            // fallback answer, exactly as view selection does.
            if (!ruleResult.IsSuccess)
                onRuleError?.Invoke(candidate, ruleResult.Error.Message ?? "unknown");
        }

        return Result<ContractSelection?>.Ok(null);
    }
}
