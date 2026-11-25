using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Caller;
using BBT.Workflow.Scripting.Declarative;
using BBT.Workflow.Scripting.Expressions;
using BBT.Workflow.Shared;
using BBT.Aether.Results;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Transitions.Services;

/// <summary>
/// Service responsible for mapping transition payload data to instance data: a transition's <c>mapping</c> script, or —
/// without one — its declarative <c>output</c> block (<see cref="MappingBlock"/>, Phase C1).
/// </summary>
/// <remarks>
/// <para>
/// The block replaces the script result exactly: its delta starts EMPTY and keeps only what <c>select</c> / <c>into</c> /
/// <c>set</c> write (<c>"select": "$.input"</c> keeps the whole request); an empty delta returns null — no data version,
/// the record body unchanged, as a script returning null. Variables: <c>data</c>, <c>input</c> (the hop's raw request,
/// bound as <see cref="ScriptContext"/> hop input), <c>instance</c>, <c>now</c>, <c>user</c> (the hop caller from
/// <see cref="ICallerAccessorFactory.ForHop"/>; without a factory or actor there is no caller — fail-closed, never an
/// empty role set), <c>context</c>, <c>headers</c>. Plans come from <see cref="DeclarativePlanCache"/>.
/// </para>
/// <para>
/// A failing block (a plan that does not build, an expression that throws, a path that cannot be written) returns the
/// error a failing script returns (<see cref="ExecutionErrors.TransitionMappingFailed"/>: <c>ExecutionStepFailed</c>,
/// "Failed to execute transition mapping: …") and logs <c>TransitionDeclarativeOutputFailed</c> (10277) in place of the
/// script's exception. A caller-role provider failure keeps its own retryable <c>Authorization:110006</c>.
/// Registered scoped; stateless apart from its dependencies.
/// </para>
/// </remarks>
public sealed class TransitionDataMapper(
    IScriptEngine scriptEngine,
    IScriptContextFactory scriptContextFactory,
    IInstanceRepository instanceRepository,
    IRuntimeInfoProvider runtimeInfoProvider,
    IExpressionCompiler? expressionCompiler = null,
    TimeProvider? clock = null,
    ICallerAccessorFactory? callerAccessorFactory = null,
    ILogger<TransitionDataMapper>? logger = null) : ITransitionDataMapper
{
    /// <summary>The error <c>Detail</c> of a failing declarative block (a script's carries its exception type).</summary>
    internal const string DeclarativeFailureDetail = "DeclarativeOutputBlock";

    private const string OutputPhase = "output";

    /// <summary>The <c>Script.Execute</c> span's script kind, shared by the mapping script and the output block.</summary>
    internal const string TransitionMappingScriptKind = "transitionMapping";

    private readonly IExpressionCompiler _compiler = expressionCompiler ?? ExpressionCompiler.Shared;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <inheritdoc />
    /// <summary>
    /// Maps transition payload data using the optional mapping script or declarative output block.
    /// Script execution uses TryAsync - dynamic invocation is appropriate for Try per Railway pattern.
    /// </summary>
    public Task<Result<object?>> MapTransitionDataAsync(
        object? payload,
        Transition? transition,
        Definitions.Workflow workflow,
        Instance instance,
        IReadOnlyDictionary<string, string?>? headers = null,
        Action<Exception>? onScriptFailure = null,
        ExecutionActor? actor = null,
        CancellationToken cancellationToken = default)
    {
        // Guard: No mapping defined - return payload as-is
        if (transition is null || !transition.HasDataMapping)
            return Task.FromResult(Result<object?>.Ok(payload));

        // A stored transition with both (publish rejects it, older definitions may carry it) runs the script only.
        if (transition.Mapping is not null)
        {
            return ExecuteMappingScriptAsync(
                payload, transition, workflow, instance, headers, onScriptFailure, cancellationToken);
        }

        return ExecuteOutputBlockAsync(
            payload, transition, transition.Output!, workflow, instance, headers, actor, cancellationToken);
    }

    /// <summary>
    /// Runs the block under the same <c>Script.Execute</c> span (kind <c>transitionMapping</c>) a mapping script opens,
    /// so a slow or failing block sits in the trace tree exactly where the script would; a failure marks it the same way.
    /// </summary>
    private async Task<Result<object?>> ExecuteOutputBlockAsync(
        object? payload,
        Transition transition,
        MappingBlock block,
        Definitions.Workflow workflow,
        Instance instance,
        IReadOnlyDictionary<string, string?>? headers,
        ExecutionActor? actor,
        CancellationToken cancellationToken)
    {
        using var activity = ScriptActivityHelper.StartExecuteActivity(TransitionMappingScriptKind);

        var result = await RunOutputBlockAsync(
            payload, transition, block, workflow, instance, headers, actor, cancellationToken);
        if (!result.IsSuccess)
            activity.SetResultError(result.Error.Code, result.Error.Message);

        return result;
    }

    /// <summary>
    /// Runs the transition's declarative <c>output</c> block into an empty delta and returns it (null when empty).
    /// </summary>
    private async Task<Result<object?>> RunOutputBlockAsync(
        object? payload,
        Transition transition,
        MappingBlock block,
        Definitions.Workflow workflow,
        Instance instance,
        IReadOnlyDictionary<string, string?>? headers,
        ExecutionActor? actor,
        CancellationToken cancellationToken)
    {
        var planResult = DeclarativePlanCache.GetOrBuild(_compiler, block, DeclarativeSiteKind.TransitionOutput);
        if (!planResult.IsSuccess)
            return OutputBlockFailed(workflow, transition, instance, planResult.Error);

        var scriptContext = await scriptContextFactory.NewBuilder(instanceRepository)
            .WithRuntime(runtimeInfoProvider)
            .WithWorkflow(workflow)
            .WithInstance(instance)
            .WithTransition(transition)
            .WithBody(payload)
            .WithHeaders(headers?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value))
            .BuildAsync(cancellationToken)
            .WithHopInputAsync(payload);

        var caller = callerAccessorFactory is not null && actor is { } hopActor
            ? callerAccessorFactory.ForHop(hopActor, headers, cancellationToken)
            : NullCallerAccessor.Instance;
        var scope = ExpressionScope.Create(scriptContext, _clock, caller, ScopeKind.TransitionOutput);
        var site = new DeclarativeSite(transition.Key, OutputPhase)
        {
            Logger = logger,
            ErrorSubject = $"transition '{transition.Key}'",
            ErrorCode = WorkflowErrorCodes.ExecutionStepFailed
        };

        var delta = new JsonObject();
        var applied = await DeclarativeBlockEngine.ApplyAsync(
            planResult.Value!, scope, delta, result: null, site, cancellationToken);
        if (!applied.IsSuccess)
            return OutputBlockFailed(workflow, transition, instance, applied.Error);

        // An owned element (its own document): the same shape a transition payload has everywhere downstream.
        return Result<object?>.Ok(delta.Count == 0 ? null : JsonSerializer.SerializeToElement(delta));
    }

    /// <summary>
    /// A failing block, logged and shaped as a failing mapping script (<see cref="ExecutionErrors.TransitionMappingFailed"/>).
    /// Any other code (a caller-role provider failure, <c>Authorization:110006</c>) is kept: it is retryable.
    /// </summary>
    private Result<object?> OutputBlockFailed(
        Definitions.Workflow workflow, Transition transition, Instance instance, Error error)
    {
        var message = error.Message ?? error.Code;
        logger?.TransitionDeclarativeOutputFailed(workflow.Key, transition.Key, instance.Id, message);

        var shaped = error.Code is WorkflowErrorCodes.ExecutionStepFailed or WorkflowErrorCodes.TaskExecution
            ? ExecutionErrors.TransitionMappingFailed(message, DeclarativeFailureDetail)
            : error;
        return Result<object?>.Fail(shaped);
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
        IReadOnlyDictionary<string, string?>? headers,
        Action<Exception>? onScriptFailure,
        CancellationToken cancellationToken)
    {
        using var activity = ScriptActivityHelper.StartExecuteActivity(TransitionMappingScriptKind);

        var result = await ResultExtensions.TryAsync(
            async ct =>
            {
                var mappingInstance = await CompileMappingScriptAsync(transition, workflow.Scripts, ct);
                var scriptContext = await BuildScriptContextAsync(
                    payload, transition, workflow, instance, headers, ct);
                
                try
                {
                    var mapped = await mappingInstance.Handler(scriptContext);
                    return mapped;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw; // TryAsync continues to apply the existing error mapping (CreateMappingError)
                }
            },
            cancellationToken,
            ex =>
            {
                if (ex is not OperationCanceledException)
                    onScriptFailure?.Invoke(ex);
                return CreateMappingError(ex);
            });

        if (!result.IsSuccess)
            activity.SetResultError(result.Error.Code, result.Error.Message);

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
