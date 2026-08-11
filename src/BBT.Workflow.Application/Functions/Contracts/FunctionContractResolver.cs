using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Selection;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Functions.Contracts;

/// <summary>
/// Projects a function contract slot onto selection candidates and hands them to the shared
/// <see cref="IRuleBasedSelectionResolver"/>. The evaluation semantics - declaration order, first match
/// wins, rule-less entry short-circuits, a rule that cannot be evaluated is logged and skipped - live in
/// that resolver, which the transition <c>schemas</c> path shares.
/// </summary>
public sealed class FunctionContractResolver(
    IRuleBasedSelectionResolver selectionResolver,
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

        var result = await selectionResolver.ResolveAsync(
            candidates,
            scriptContext,
            (reference, error) => logger.FunctionContractRuleEvaluationFailed(
                function.Key,
                slot.ToString(),
                reference.Key,
                error),
            cancellationToken);

        if (!result.IsSuccess)
            return Result<FunctionContractResolution?>.Fail(result.Error);

        var match = result.Value;
        return Result<FunctionContractResolution?>.Ok(
            match is null
                ? null
                : new FunctionContractResolution(match.Reference, match.MatchedByRule, match.LoadData));
    }

    /// <summary>
    /// Projects the requested slot onto a uniform candidate list so views and schemas share one
    /// evaluation loop.
    /// </summary>
    private static List<SelectionCandidate> GetCandidates(Function function, FunctionContractSlot slot) =>
        slot switch
        {
            FunctionContractSlot.InputSchema => FromSchemas(function.InputSchema),
            FunctionContractSlot.OutputSchema => FromSchemas(function.OutputSchema),
            FunctionContractSlot.InputView => FromViews(function.InputView),
            FunctionContractSlot.OutputView => FromViews(function.OutputView),
            _ => []
        };

    private static List<SelectionCandidate> FromSchemas(SchemaSelection? selection) =>
        selection is null
            ? []
            : selection.Schemas.Select(e => new SelectionCandidate(e.Rule, e.Schema)).ToList();

    private static List<SelectionCandidate> FromViews(ViewDefinition? definition) =>
        definition is null
            ? []
            : definition.Views.Select(e => new SelectionCandidate(e.Rule, e.View, e.LoadData)).ToList();
}
