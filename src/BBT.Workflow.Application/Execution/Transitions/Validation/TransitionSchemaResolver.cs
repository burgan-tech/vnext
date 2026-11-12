using BBT.Aether.Results;
using BBT.Workflow.Authorization;
using BBT.Workflow.Contracts;
using BBT.Workflow.Definitions;
using BBT.Workflow.Functions.Contracts;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Validation;

/// <summary>
/// Evaluates a transition's rule-based schema entries (declaration order, first match wins, a rule-less
/// entry is the fallback) through the shared <see cref="IContractEntrySelector"/>. The rule context is
/// workflow, instance, runtime, transition key, the instance's latest data as body and the request
/// headers - never query parameters and never the request payload, so a caller cannot pick its own schema.
/// A transition with a single <c>schema</c> never builds a script context.
/// </summary>
public sealed class TransitionSchemaResolver(
    IContractEntrySelector selector,
    IScriptContextFactory scriptContextFactory,
    IInstanceRepository instanceRepository,
    IRuntimeInfoProvider runtimeInfoProvider,
    ICallerAccessorFactory callerAccessorFactory,
    ILogger<TransitionSchemaResolver> logger) : ITransitionSchemaResolver
{
    /// <inheritdoc />
    public async Task<Result<Reference?>> ResolveAsync(
        Definitions.Workflow workflow,
        Instance instance,
        Transition transition,
        IReadOnlyDictionary<string, string?>? headers,
        CancellationToken cancellationToken = default)
    {
        if (transition.SchemaSelection is not { Schemas.Count: > 0 } selection)
            return Result<Reference?>.Ok(null);

        var candidates = selection.Schemas
            .Select(e => new ContractCandidate(e.Rule, e.Schema, null))
            .ToList();

        var headerCopy = headers?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        // Lazy: a rule-less (single schema) selection short-circuits before anything is built.
        var lazyContext = new LazyScriptContext(ct => scriptContextFactory.NewBuilder(instanceRepository)
            .WithWorkflow(workflow)
            .WithInstance(instance)
            .WithRuntime(runtimeInfoProvider)
            .WithTransition(transition.Key)
            .WithBody(instance.LatestData?.Data ?? new JsonData("{}"))
            .WithHeaders(headerCopy)
            // Rules may select by the caller's resolved roles; resolved only if a rule reads them.
            .WithCaller(callerAccessorFactory.ForRequest(headerCopy, ct))
            .BuildAsync(ct));

        var selected = await selector.SelectAsync(
            candidates,
            lazyContext,
            (candidate, message) => logger.TransitionSchemaRuleEvaluationFailed(
                transition.Key, candidate.Reference.Key, message),
            cancellationToken);

        if (!selected.IsSuccess)
            return Result<Reference?>.Fail(selected.Error);

        if (selected.Value is { } won)
            return Result<Reference?>.Ok(won.Reference);

        logger.TransitionSchemaNotMatched(transition.Key);
        return Result<Reference?>.Fail(WorkflowErrors.TransitionSchemaNotMatched(transition.Key));
    }
}
