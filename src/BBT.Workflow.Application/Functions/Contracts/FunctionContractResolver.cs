using BBT.Aether.Results;
using BBT.Workflow.Contracts;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Functions.Contracts;

/// <summary>
/// Evaluates a function contract slot's rule-based entries and returns the winning reference.
/// Mirrors the state/transition view selection loop in <c>InstanceQueryAppService.ResolveViewAsync</c>:
/// declaration order, first match wins, a rule-less entry short-circuits, and a rule that cannot be
/// evaluated is logged and skipped rather than failing the request.
/// </summary>
public sealed class FunctionContractResolver(
    IContractEntrySelector selector,
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

        var selection = await selector.SelectAsync(
            candidates,
            scriptContext,
            (candidate, message) => logger.FunctionContractRuleEvaluationFailed(
                function.Key,
                slot.ToString(),
                candidate.Reference.Key,
                message),
            cancellationToken);

        if (!selection.IsSuccess)
            return Result<FunctionContractResolution?>.Fail(selection.Error);

        // No entry won: the function declares no contract for this request. Not an error - the caller
        // decides what an absent contract means.
        return Result<FunctionContractResolution?>.Ok(
            selection.Value is { } won
                ? new FunctionContractResolution(won.Reference, won.MatchedByRule, won.LoadData)
                : null);
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
}
