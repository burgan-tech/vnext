using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Functions.Contracts;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Selection;

/// <summary>
/// Projects a transition's <c>schemas</c> entries onto selection candidates and hands them to the
/// shared <see cref="IRuleBasedSelectionResolver"/>, so a transition schema is picked by exactly the
/// same loop as a function contract slot and a state view.
/// </summary>
public sealed class TransitionSchemaResolver(
    IRuleBasedSelectionResolver selectionResolver,
    ILogger<TransitionSchemaResolver> logger) : ITransitionSchemaResolver
{
    /// <inheritdoc />
    public async Task<Result<Reference?>> ResolveAsync(
        Transition transition,
        LazyScriptContext scriptContext,
        CancellationToken cancellationToken = default)
    {
        var selection = transition.Schema;
        if (selection is null || selection.Schemas.Count == 0)
            return Result<Reference?>.Ok(null);

        var candidates = selection.Schemas
            .Select(entry => new SelectionCandidate(entry.Rule, entry.Schema))
            .ToList();

        var result = await selectionResolver.ResolveAsync(
            candidates,
            scriptContext,
            (reference, error) => logger.TransitionSchemaRuleEvaluationFailed(
                transition.Key,
                reference.Key,
                error),
            cancellationToken);

        return result.IsSuccess
            ? Result<Reference?>.Ok(result.Value?.Reference)
            : Result<Reference?>.Fail(result.Error);
    }
}
