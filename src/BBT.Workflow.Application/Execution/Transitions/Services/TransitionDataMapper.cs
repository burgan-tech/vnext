using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Aether.Results;

namespace BBT.Workflow.Execution.Transitions.Services;

/// <summary>
/// Service responsible for mapping transition payload data to instance data.
/// Handles optional transition mapping scripts to transform payload before adding to instance.
/// </summary>
public sealed class TransitionDataMapper(
    IScriptEngine scriptEngine,
    IScriptContextFactory scriptContextFactory,
    IInstanceRepository instanceRepository) : ITransitionDataMapper
{
    /// <inheritdoc />
    /// <summary>
    /// Maps transition payload data using optional mapping script.
    /// Script execution uses TryAsync - dynamic invocation is appropriate for Try per Railway pattern.
    /// </summary>
    public Task<Result<object?>> MapTransitionDataAsync(
        object? payload,
        Transition? transition,
        Definitions.Workflow workflow,
        Instance instance,
        IRuntimeInfoProvider runtimeInfoProvider,
        IReadOnlyDictionary<string, string?>? headers = null,
        CancellationToken cancellationToken = default)
    {
        // Guard: No mapping defined - return payload as-is
        if (transition?.Mapping is null)
            return Task.FromResult(Result<object?>.Ok(payload));

        // Execute mapping script safely using TryAsync
        return ExecuteMappingScriptAsync(
            payload, transition, workflow, instance,
            runtimeInfoProvider, headers, cancellationToken);
    }

    /// <summary>
    /// Executes the mapping script safely using TryAsync.
    /// Script compilation and execution are dynamic invocation - TryAsync is appropriate.
    /// </summary>
    private async Task<Result<object?>> ExecuteMappingScriptAsync(
        object? payload,
        Transition transition,
        Definitions.Workflow workflow,
        Instance instance,
        IRuntimeInfoProvider runtimeInfoProvider,
        IReadOnlyDictionary<string, string?>? headers,
        CancellationToken cancellationToken)
    {
        var result = await ResultExtensions.TryAsync(
            async ct =>
            {
                var mappingInstance = await CompileMappingScriptAsync(transition, workflow.Scripts, ct);
                var scriptContext = await BuildScriptContextAsync(
                    payload, transition, workflow, instance, runtimeInfoProvider, headers, ct);

                return await mappingInstance.Handler(scriptContext);
            },
            cancellationToken,
            CreateMappingError);

        // Map dynamic result to object? for proper nullability
        return result.IsSuccess
            ? Result<object?>.Ok(result.Value)
            : Result<object?>.Fail(result.Error);
    }

    /// <summary>
    /// Compiles the mapping script to ITransitionMapping interface.
    /// </summary>
    private Task<ITransitionMapping> CompileMappingScriptAsync(
        Transition transition,
        ScriptSettings? flowScripts,
        CancellationToken cancellationToken)
    {
        return scriptEngine.CompileToInstanceAsync<ITransitionMapping>(
            transition.Mapping!,
            flowScripts: flowScripts,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Builds the script context for mapping execution.
    /// </summary>
    private Task<ScriptContext> BuildScriptContextAsync(
        object? payload,
        Transition transition,
        Definitions.Workflow workflow,
        Instance instance,
        IRuntimeInfoProvider runtimeInfoProvider,
        IReadOnlyDictionary<string, string?>? headers,
        CancellationToken cancellationToken)
    {
        return scriptContextFactory.NewBuilder(instanceRepository)
            .WithRuntime(runtimeInfoProvider)
            .WithWorkflow(workflow)
            .WithInstance(instance)
            .WithTransition(transition)
            .WithBody(payload)
            .WithHeaders(headers?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value))
            .BuildAsync(cancellationToken);
    }

    /// <summary>
    /// Creates an error from a mapping script exception.
    /// Used as error mapper for TryAsync.
    /// </summary>
    private static Error CreateMappingError(Exception ex)
        => ExecutionErrors.TransitionMappingFailed(ex.Message, ex.GetType().Name);
}
