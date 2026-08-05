using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Functions.Contracts;

/// <summary>
/// Evaluates a function contract slot's rule-based entries and returns the winning reference.
/// Mirrors the state/transition view selection loop in <c>InstanceQueryAppService.ResolveViewAsync</c>:
/// declaration order, first match wins, a rule-less entry short-circuits, and a rule that cannot be
/// evaluated is logged and skipped rather than failing the request.
/// </summary>
public sealed class FunctionContractResolver(
    ITaskConditionService taskConditionService,
    ILogger<FunctionContractResolver> logger) : IFunctionContractResolver
{
    /// <inheritdoc />
    public async Task<Result<FunctionContractResolution?>> ResolveAsync(
        Function function,
        FunctionContractSlot slot,
        LazyScriptContext scriptContext,
        CancellationToken cancellationToken = default)
    {
        var candidates = GetCandidates(function, slot);
        if (candidates.Count == 0)
            return Result<FunctionContractResolution?>.Ok(null);

        foreach (var candidate in candidates)
        {
            // A rule-less entry is the declared fallback - it always wins from here on, so nothing
            // after it can ever be reached (the component validator enforces that it is last).
            if (candidate.Rule == null)
                return Result<FunctionContractResolution?>.Ok(
                    new FunctionContractResolution(candidate.Reference, MatchedByRule: false, candidate.LoadData));

            var contextResult = await scriptContext.GetAsync(cancellationToken);
            if (!contextResult.IsSuccess)
                return Result<FunctionContractResolution?>.Fail(contextResult.Error);

            var ruleResult = await taskConditionService.ExecuteConditionAsync(
                candidate.Rule,
                contextResult.Value!,
                cancellationToken);

            if (ruleResult is { IsSuccess: true, Value: true })
                return Result<FunctionContractResolution?>.Ok(
                    new FunctionContractResolution(candidate.Reference, MatchedByRule: true, candidate.LoadData));

            // A rule that threw tells us nothing about the entry - skip it and let a later entry or the
            // fallback answer, exactly as view selection does.
            if (!ruleResult.IsSuccess)
            {
                logger.FunctionContractRuleEvaluationFailed(
                    function.Key,
                    slot.ToString(),
                    candidate.Reference.Key,
                    ruleResult.Error.Message ?? "unknown");
            }
        }

        // Every entry carried a rule and none matched: the function declares no contract for this
        // request. Not an error - the caller decides what an absent contract means.
        return Result<FunctionContractResolution?>.Ok(null);
    }

    /// <summary>
    /// Projects the requested slot onto a uniform candidate list so views and schemas share one
    /// evaluation loop.
    /// </summary>
    private static List<ContractCandidate> GetCandidates(Function function, FunctionContractSlot slot) =>
        slot switch
        {
            FunctionContractSlot.InputSchema => FromSchemas(function.InputSchema),
            FunctionContractSlot.OutputSchema => FromSchemas(function.OutputSchema),
            FunctionContractSlot.InputView => FromViews(function.InputView),
            FunctionContractSlot.OutputView => FromViews(function.OutputView),
            _ => []
        };

    private static List<ContractCandidate> FromSchemas(SchemaSelection? selection) =>
        selection is null
            ? []
            : selection.Schemas.Select(e => new ContractCandidate(e.Rule, e.Schema, null)).ToList();

    private static List<ContractCandidate> FromViews(ViewDefinition? definition) =>
        definition is null
            ? []
            : definition.Views.Select(e => new ContractCandidate(e.Rule, e.View, e.LoadData)).ToList();

    private sealed record ContractCandidate(ScriptCode? Rule, Reference Reference, bool? LoadData);
}
