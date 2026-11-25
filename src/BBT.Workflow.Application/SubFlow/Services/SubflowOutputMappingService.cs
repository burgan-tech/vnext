using BBT.Workflow.Scripting.Caller;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Authorization;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Declarative;
using BBT.Workflow.Scripting.Expressions;
using BBT.Workflow.SubFlow.Headers;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.SubFlow;

/// <inheritdoc cref="ISubflowOutputMappingService" />
/// <remarks>
/// <para>Carried headers: the optional policy re-filters the headers carried from the child's settlement for output mapping and the resume context; null disables header carry, production DI always supplies it.</para>
/// <para>
/// Declarative <c>output</c> block (Phase C1): a SubFlow state without a <c>mapping</c> script may carry an <c>output</c>
/// block. It runs here, on both the completion and the fault path, under the same <c>Script.Execute</c> span (kind
/// <c>subflowOutputMapping</c>). Its delta starts empty; <c>select</c> reads under
/// <c>result = { data: &lt;child data or null&gt;, status: "completed" | "faulted" }</c> (default <c>$.data</c>); a non-empty
/// delta is appended with the same single <see cref="IInstanceDataWriteService.AppendAsync"/> the script result uses.
/// A failure returns the error a failing script returns (<see cref="WorkflowErrors.SubFlowOutputMappingFailed"/>) and logs
/// <c>SubFlowDeclarativeOutputFailed</c> (10279); a caller-role provider failure is transient, as for the script. A stored
/// definition with both <c>mapping</c> and <c>output</c> runs the script. Registered scoped.
/// </para>
/// </remarks>
/// <param name="instanceRepository">Parent instance access for the script context and script mutations.</param>
/// <param name="instanceDataWriteService">The single write funnel the mapped delta is appended through.</param>
/// <param name="scriptEngine">Compiles the <c>mapping</c> script.</param>
/// <param name="scriptContextFactory">Builds the script / expression context over the parent instance.</param>
/// <param name="runtimeInfoProvider">Runtime information for the script context.</param>
/// <param name="logger">Logger for SubFlow output mapping telemetry.</param>
/// <param name="carriedHeaderPolicy">Re-filters the child settler's carried headers; null disables header carry.</param>
/// <param name="callerAccessorFactory">Resolves the child settler's roles (<c>Caller</c> / <c>user</c>); unavailable when null.</param>
/// <param name="expressionCompiler">Compiler for a declarative <c>output</c> block's expressions; the shared one when null.</param>
/// <param name="clock">Source of the block's <c>now</c>; the system clock when null.</param>
public sealed class SubflowOutputMappingService(
    IInstanceRepository instanceRepository,
    IInstanceDataWriteService instanceDataWriteService,
    IScriptEngine scriptEngine,
    IScriptContextFactory scriptContextFactory,
    IRuntimeInfoProvider runtimeInfoProvider,
    ILogger<SubflowOutputMappingService> logger,
    ICarriedHeaderPolicy? carriedHeaderPolicy = null,
    ICallerAccessorFactory? callerAccessorFactory = null,
    IExpressionCompiler? expressionCompiler = null,
    TimeProvider? clock = null)
    : ISubflowOutputMappingService
{
    /// <summary>The <c>Script.Execute</c> span's script kind, shared by the output mapping script and the output block.</summary>
    internal const string SubFlowOutputMappingScriptKind = "subflowOutputMapping";

    private const string OutputPhase = "output";
    private const string CompletedStatus = "completed";
    private const string FaultedStatus = "faulted";

    private readonly IExpressionCompiler _compiler = expressionCompiler ?? ExpressionCompiler.Shared;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<Result> ApplyAsync(
        Instance parentInstance,
        Definitions.Workflow parentWorkflow,
        string parentStateKey,
        JsonElement? childInstanceData,
        SubflowSettlement settlement,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string?>? carriedHeaders = null)
    {
        var preparation = await PrepareAsync(
            parentInstance.Id,
            parentWorkflow,
            parentStateKey,
            cancellationToken);
        if (!preparation.IsSuccess)
            return Result.Fail(preparation.Error);

        return await ApplyPreparedAsync(
            parentInstance,
            parentWorkflow,
            preparation.Value!,
            childInstanceData,
            settlement,
            carriedHeaders,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<SubflowOutputMappingPlan>> PrepareAsync(
        Guid parentInstanceId,
        Definitions.Workflow parentWorkflow,
        string parentStateKey,
        CancellationToken cancellationToken = default)
    {
        var parentStateResult = parentWorkflow.GetState(parentStateKey);
        if (!parentStateResult.IsSuccess)
            return Result<SubflowOutputMappingPlan>.Ok(new(null, null));

        var parentState = parentStateResult.Value!;
        var subFlowConfig = parentState.SubFlow;
        if (subFlowConfig?.Mapping is null || !subFlowConfig.Mapping.HasMappingCode)
        {
            // No script: the declarative output block (Phase C1), when the state has one. A stored definition with
            // both `mapping` and `output` runs the script (below). Like the OutputHandler, only an S SubFlow runs it.
            return subFlowConfig?.Output is { } outputBlock && subFlowConfig.Type.Equals(SubFlowType.SubFlow)
                ? PrepareOutputBlock(parentInstanceId, parentWorkflow, parentState, outputBlock)
                : Result<SubflowOutputMappingPlan>.Ok(new(parentState, null));
        }

        try
        {
            var mappingInstance = await scriptEngine.CompileToInstanceAsync<object>(
                subFlowConfig.Mapping,
                flowScripts: parentWorkflow.Scripts,
                cancellationToken: cancellationToken);

            return Result<SubflowOutputMappingPlan>.Ok(new(parentState, mappingInstance));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.SubFlowOutputMappingFailed(ex, parentInstanceId);
            return Result<SubflowOutputMappingPlan>.Fail(WorkflowErrors.SubFlowOutputMappingFailed(
                parentInstanceId,
                ScriptDiagnostics.Explain(ex),
                stackTrace: ex.ToString()));
        }
    }

    // Caller roles (context.Caller) in SubFlow output mappings — council 2026-11-12-script-caller-roles-background
    // (Chair override; the council itself proposed leaving this surface unavailable). The identity is the CHILD
    // SETTLER's: the headers of the request that completed the child, carried on the untrusted instance.sub.*
    // event and re-filtered by ICarriedHeaderPolicy — not the parent's starter. A child settled by a timer carries
    // no headers and the accessor is unavailable. Mitigations: roles are resolved before the terminal lock
    // (WarmCallerRolesAsync) and a provider failure is TRANSIENT (redelivered), never a permanent parent fault.

    /// <inheritdoc />
    public async Task WarmCallerRolesAsync(
        Guid parentInstanceId,
        SubflowOutputMappingPlan plan,
        IReadOnlyDictionary<string, string?>? carriedHeaders,
        CancellationToken cancellationToken = default)
    {
        // Cheap, conservative trigger: only a mapping whose source names Caller pays the provider call here. A
        // mapping that reaches the accessor only through a helper still works — it resolves inside the lock. A
        // declarative output block warms when its plan reads `user` (in a set expression or as the select variable).
        var needsCaller = plan.MappingInstance is not null
            ? plan.ParentState?.SubFlow?.Mapping is { } mapping
              && mapping.DecodedCode.Contains("Caller", StringComparison.Ordinal)
            : plan.OutputBlock?.Reads(ExpressionScope.UserVariable) == true;
        if (!needsCaller)
            return;

        var caller = CreateCaller(carriedHeaders, cancellationToken);
        if (!caller.IsAvailable)
            return;

        try
        {
            await caller.RolesAsync();
        }
        catch (CallerRolesUnavailableException)
        {
            throw new CallerRolesUnavailableTransientException(parentInstanceId);
        }
    }

    private ICallerAccessor CreateCaller(
        IReadOnlyDictionary<string, string?>? carriedHeaders,
        CancellationToken cancellationToken)
    {
        if (callerAccessorFactory is null || carriedHeaderPolicy is not { ExposeToOutputMapping: true })
            return NullCallerAccessor.Instance;

        var applied = carriedHeaderPolicy.Apply(carriedHeaders);
        return applied is { Count: > 0 }
            ? callerAccessorFactory.ForRequest(applied, cancellationToken)
            : NullCallerAccessor.Instance;
    }

    /// <inheritdoc />
    public async Task<Result> ApplyPreparedAsync(
        Instance parentInstance,
        Definitions.Workflow parentWorkflow,
        SubflowOutputMappingPlan plan,
        JsonElement? childInstanceData,
        SubflowSettlement settlement,
        IReadOnlyDictionary<string, string?>? carriedHeaders,
        CancellationToken cancellationToken = default)
    {
        if (plan.ParentState is null)
            return Result.Ok();

        if (plan.MappingInstance is null)
        {
            return plan.OutputBlock is { } outputBlock
                ? await ApplyOutputBlockAsync(parentInstance, parentWorkflow, plan.ParentState, outputBlock,
                    childInstanceData, settlement, carriedHeaders, cancellationToken)
                : Result.Ok();
        }

        var caller = CreateCaller(carriedHeaders, cancellationToken);

        try
        {
            logger.SubFlowOutputMappingStarted(parentInstance.Id);

            var builder = scriptContextFactory.NewBuilder(instanceRepository)
                .WithWorkflow(parentWorkflow)
                .WithInstance(parentInstance)
                .WithRuntime(runtimeInfoProvider)
                .WithBody(childInstanceData?.Deserialize<Dictionary<string, object>>() ?? new Dictionary<string, object>());

            // The child's request headers reach the OutputHandler only when the policy exposes them,
            // re-filtered on this side (the event is untrusted input).
            if (carriedHeaderPolicy is { ExposeToOutputMapping: true })
                builder = builder.WithHeaders(carriedHeaderPolicy.Apply(carriedHeaders));

            if (caller.IsAvailable)
                builder = builder.WithCaller(caller);

            var scriptContext = await builder.BuildAsync(cancellationToken);

            using var scriptActivity = ScriptActivityHelper.StartExecuteActivity(SubFlowOutputMappingScriptKind);

            ScriptResponse? outputMappingResult = null;
            if (plan.ParentState.SubFlow!.Type.Equals(SubFlowType.SubFlow)
                && plan.MappingInstance is ISubFlowMapping subFlowMapping)
            {
                outputMappingResult = await subFlowMapping.OutputHandler(scriptContext);
            }

            // Roles were read while the provider could not answer: nothing the mapping produced is persisted,
            // and the completion is redelivered instead of faulting the parent.
            if (caller.ResolutionFailed)
                throw new CallerRolesUnavailableTransientException(parentInstance.Id);

            var hasData = outputMappingResult?.Data != null;
            if (hasData)
            {
                // Persisted IMMEDIATELY — identity computed under the per-instance row lock.
                await instanceDataWriteService.AppendAsync(
                    parentInstance,
                    new JsonData(JsonSerializer.Serialize(outputMappingResult!.Data)),
                    plan.ParentState.VersionStrategy,
                    cancellationToken,
                    parentWorkflow);
            }

            if (scriptContext.Mutations.HasChanges)
            {
                scriptContext.Mutations.ApplyTo(parentInstance);
                await instanceRepository.UpdateAsync(parentInstance, true, cancellationToken);
            }

            return Result.Ok();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Our own cancellation (shutdown, caller gone) — not a mapping failure. Let it out so the
            // UnitOfWork rolls back; the delivery is redelivered when the runtime is healthy again.
            // A cancellation-shaped exception arriving while OUR token is still live (e.g. a
            // DaprClient call inside the mapping timing out) is a downstream fault, not this case;
            // it falls through to the permanent failed-Result path below.
            throw;
        }
        catch (CallerRolesUnavailableTransientException)
        {
            throw;
        }
        catch (Exception) when (caller.ResolutionFailed)
        {
            throw new CallerRolesUnavailableTransientException(parentInstance.Id);
        }
        catch (Exception ex)
        {
            logger.SubFlowOutputMappingFailed(ex, parentInstance.Id);
            return Result.Fail(WorkflowErrors.SubFlowOutputMappingFailed(
                parentInstance.Id,
                ScriptDiagnostics.Explain(ex),
                stackTrace: ex.ToString()));
        }
    }

    /// <summary>
    /// Builds the plan of the state's declarative <c>output</c> block (memoized per block instance). A block that does
    /// not build fails like a mapping script that does not compile (publish validation normally rejects it first).
    /// </summary>
    private Result<SubflowOutputMappingPlan> PrepareOutputBlock(
        Guid parentInstanceId, Definitions.Workflow parentWorkflow, State parentState, MappingBlock outputBlock)
    {
        var built = DeclarativePlanCache.GetOrBuild(_compiler, outputBlock, DeclarativeSiteKind.SubFlowOutput);
        if (built.IsSuccess)
            return Result<SubflowOutputMappingPlan>.Ok(new(parentState, null) { OutputBlock = built.Value });

        return Result<SubflowOutputMappingPlan>.Fail(
            OutputBlockFailed(parentWorkflow, parentState, parentInstanceId, built.Error));
    }

    /// <summary>
    /// Runs the declarative <c>output</c> block against authoritative locked state and appends its delta to the parent.
    /// Variables: <c>data</c> (parent data), <c>instance</c> (parent), <c>now</c>, <c>context</c>, <c>input</c> = null (the
    /// resume hop has no request payload), <c>headers</c> = the carried headers re-filtered by the policy only when it
    /// exposes them to output mapping (else empty), <c>user</c> = the child settler (as the script's <c>Caller</c>),
    /// <c>result</c> = <c>{ data, status }</c>. The same exception guard as the script path: our own cancellation and a
    /// caller-role provider failure propagate (the latter as <see cref="CallerRolesUnavailableTransientException"/>);
    /// anything else becomes the script's failed result.
    /// </summary>
    private async Task<Result> ApplyOutputBlockAsync(
        Instance parentInstance,
        Definitions.Workflow parentWorkflow,
        State parentState,
        DeclarativeMappingPlan outputBlock,
        JsonElement? childInstanceData,
        SubflowSettlement settlement,
        IReadOnlyDictionary<string, string?>? carriedHeaders,
        CancellationToken cancellationToken)
    {
        var caller = CreateCaller(carriedHeaders, cancellationToken);

        try
        {
            logger.SubFlowOutputMappingStarted(parentInstance.Id);

            var builder = scriptContextFactory.NewBuilder(instanceRepository)
                .WithWorkflow(parentWorkflow)
                .WithInstance(parentInstance)
                .WithRuntime(runtimeInfoProvider);

            // Re-filtered on this side (the event is untrusted input), and only when the policy exposes them.
            if (carriedHeaderPolicy is { ExposeToOutputMapping: true })
                builder = builder.WithHeaders(carriedHeaderPolicy.Apply(carriedHeaders));

            // `input` is null: the resume hop carries no request payload of its own.
            var scriptContext = await builder.BuildAsync(cancellationToken).WithHopInputAsync(null);

            using var scriptActivity = ScriptActivityHelper.StartExecuteActivity(SubFlowOutputMappingScriptKind);

            var result = ResultDocument(childInstanceData, settlement);
            var scope = ExpressionScope.Create(scriptContext, _clock, caller, ScopeKind.SubFlowOutput, result);
            var site = new DeclarativeSite(parentState.SubFlow!.Process.Key, OutputPhase)
            {
                Logger = logger,
                // The wrapping SubFlowOutputMappingFailed names the parent instance; the inner message names the state.
                ErrorSubject = $"state '{parentState.Key}'",
                ErrorCode = WorkflowErrorCodes.SubflowOutputMappingFailed
            };

            var delta = new JsonObject();
            var applied = await DeclarativeBlockEngine.ApplyAsync(
                outputBlock, scope, delta, result, site, cancellationToken);
            if (!applied.IsSuccess)
            {
                // Roles were read while the provider could not answer: nothing is persisted, and the completion is
                // redelivered instead of faulting the parent (the fault path turns it into a failed mapping result).
                if (applied.Error.Code == WorkflowErrorCodes.CallerRolesUnavailable || caller.ResolutionFailed)
                    throw new CallerRolesUnavailableTransientException(parentInstance.Id);

                var shaped = OutputBlockFailed(parentWorkflow, parentState, parentInstance.Id, applied.Error);
                scriptActivity.SetResultError(shaped.Code, shaped.Message);
                return Result.Fail(shaped);
            }

            if (delta.Count > 0)
            {
                // Persisted IMMEDIATELY — identity computed under the per-instance row lock (as the script result).
                await instanceDataWriteService.AppendAsync(
                    parentInstance,
                    new JsonData(delta.ToJsonString()),
                    parentState.VersionStrategy,
                    cancellationToken,
                    parentWorkflow);
            }

            return Result.Ok();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CallerRolesUnavailableTransientException)
        {
            throw;
        }
        catch (Exception) when (caller.ResolutionFailed)
        {
            throw new CallerRolesUnavailableTransientException(parentInstance.Id);
        }
        catch (Exception ex)
        {
            logger.SubFlowOutputMappingFailed(ex, parentInstance.Id);
            return Result.Fail(WorkflowErrors.SubFlowOutputMappingFailed(
                parentInstance.Id,
                ScriptDiagnostics.Explain(ex),
                stackTrace: ex.ToString()));
        }
    }

    /// <summary>
    /// The block's <c>result</c> document: <c>{ "data": &lt;child data as the parent receives it, or null&gt;,
    /// "status": "completed" | "faulted" }</c>. Detached (cloned), so it outlives no caller-owned document.
    /// </summary>
    private static JsonElement ResultDocument(JsonElement? childInstanceData, SubflowSettlement settlement)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("data");
            if (childInstanceData is { ValueKind: not JsonValueKind.Undefined } data)
                data.WriteTo(writer);
            else
                writer.WriteNullValue();
            writer.WriteString("status", settlement == SubflowSettlement.Faulted ? FaultedStatus : CompletedStatus);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// A failing block, logged (<c>SubFlowDeclarativeOutputFailed</c>) and shaped as a failing output mapping script
    /// (<see cref="WorkflowErrors.SubFlowOutputMappingFailed"/>).
    /// </summary>
    private Error OutputBlockFailed(
        Definitions.Workflow parentWorkflow, State parentState, Guid parentInstanceId, Error error)
    {
        var message = error.Message ?? error.Code;
        logger.SubFlowDeclarativeOutputFailed(
            parentWorkflow.Key, parentState.Key, parentState.SubFlow?.Process.Key ?? string.Empty, parentInstanceId, message);
        return WorkflowErrors.SubFlowOutputMappingFailed(parentInstanceId, message);
    }
}
