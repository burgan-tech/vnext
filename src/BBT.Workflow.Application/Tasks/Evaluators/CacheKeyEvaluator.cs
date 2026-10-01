using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Rules;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Evaluators;

/// <summary>
/// Evaluates a CacheAside key <see cref="ScriptCode"/> of any kind. Routes by <see cref="ScriptCode.Location"/>
/// like <see cref="RoutingConditionEvaluator"/>: <c>dynamicExpresso</c> → <see cref="IDynamicExpressoValueEvaluator"/>
/// (a REF body is resolved from <c>sys-mappings</c> first, since the expresso evaluator reads inline code
/// only); anything else compiles to <see cref="ICacheKeyMapping"/>.
/// </summary>
public interface ICacheKeyEvaluator
{
    /// <summary>Evaluates the key script. An empty result is <c>Ok("")</c>: the caller keeps the previous key.</summary>
    Task<Result<string>> EvaluateAsync(ScriptCode script, ScriptContext context, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class CacheKeyEvaluator(
    IDynamicExpressoValueEvaluator expressoEvaluator,
    IScriptEngine scriptEngine,
    IComponentCacheStore componentCacheStore,
    ICurrentSchema currentSchema,
    ILogger<CacheKeyEvaluator> logger) : ICacheKeyEvaluator
{
    /// <inheritdoc />
    public async Task<Result<string>> EvaluateAsync(
        ScriptCode script, ScriptContext context, CancellationToken cancellationToken = default)
    {
        if (ConditionScriptLocations.IsDynamicExpresso(script.Location))
        {
            if (!script.IsReference)
            {
                return expressoEvaluator.Evaluate(script, context);
            }

            var body = await ResolveReferenceAsync(script.CodeReference!, cancellationToken);
            return body.IsSuccess
                ? expressoEvaluator.Evaluate(
                    ScriptCode.FromNative(body.Value!, location: ConditionScriptLocations.DynamicExpresso), context)
                : Result<string>.Fail(body.Error);
        }

        return await ResultExtensions.TryAsync<string>(async ct =>
        {
            var mapping = await scriptEngine.CompileToInstanceAsync<ICacheKeyMapping>(
                script, flowScripts: context.Workflow?.Scripts, cancellationToken: ct);
            return await mapping.Handler(context) ?? string.Empty;
        }, cancellationToken, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"Cache key script failed: {ScriptDiagnostics.Explain(ex)}"));
    }

    private async Task<Result<string>> ResolveReferenceAsync(Reference reference, CancellationToken cancellationToken)
    {
        using (currentSchema.Change(RuntimeSysSchemaInfo.Mappings))
        {
            var result = await componentCacheStore.GetMappingAsync(
                reference.Domain, reference.Key, reference.Version, cancellationToken);
            if (!result.IsSuccess || result.Value is null)
            {
                logger.ScriptHelperReferenceUnresolved(reference.Domain, reference.Flow, reference.Key, reference.Version);
                return Result<string>.Fail(Error.Validation(
                    WorkflowErrorCodes.TaskExecution,
                    $"Referenced cache-key mapping could not be resolved: {reference}"));
            }

            return Result<string>.Ok(result.Value.DecodedCode);
        }
    }
}
