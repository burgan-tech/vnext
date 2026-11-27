using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using BBT.Aether;
using BBT.Aether.Results;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Caller;
using BBT.Workflow.Scripting.Declarative;
using BBT.Workflow.Scripting.Expressions;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Service for managing SubFlow and SubProcess workflows.
/// SubFlow: Runs on the main flow, preserves main flow state, acts as a wrapper.
/// SubProcess: Creates separate instances via remote calls (unchanged behavior).
/// Uses IInstanceCommandGateway to route between local and remote execution based on target domain.
/// </summary>
/// <param name="instanceCommandGateway">Gateway for instance commands, routes local/remote based on domain.</param>
/// <param name="configuration">Configuration provider for accessing application settings.</param>
/// <param name="scriptEngine">Script engine for compiling and executing mapping scripts.</param>
/// <param name="logger">Logger for SubFlow telemetry.</param>
/// <param name="expressionCompiler">Compiler for a declarative <c>input</c> block's expressions; the shared one when null.</param>
/// <param name="clock">Source of the block's <c>now</c>; the system clock when null.</param>
/// <param name="callerAccessorFactory">
/// Resolves the parent hop's caller for the block's <c>user</c>; without it <c>user</c> is unavailable (fail-closed).
/// </param>
/// <remarks>
/// A SubFlow's start request comes from its <c>mapping</c> script (InputHandler) or — without one — its declarative
/// <c>input</c> block (Phase C1), which builds the same <see cref="ScriptResponse"/>; a failing block fails the start with
/// <see cref="WorkflowErrors.SubFlowInputMappingFailed"/> before any child is created and logs
/// <c>SubFlowDeclarativeInputFailed</c> (10278). Registered scoped.
/// </remarks>
public sealed class SubflowStarter(
    IInstanceCommandGateway instanceCommandGateway,
    IConfiguration configuration,
    IScriptEngine scriptEngine,
    ILogger<SubflowStarter> logger,
    IExpressionCompiler? expressionCompiler = null,
    TimeProvider? clock = null,
    ICallerAccessorFactory? callerAccessorFactory = null) : ISubflowStarter
{
    /// <summary>The <c>Script.Execute</c> span's script kind, shared by the input mapping script and the input block.</summary>
    internal const string SubFlowInputMappingScriptKind = "subflowInputMapping";

    private const string InputPhase = "input";
    private const string AttributesMember = "attributes";
    private const string HeadersMember = "headers";
    private const string KeyMember = "key";
    private const string TagsMember = "tags";

    private readonly IExpressionCompiler _compiler = expressionCompiler ?? ExpressionCompiler.Shared;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<Result<string?>> StartAsync(
        Definitions.Workflow workflow,
        Instance parentInstance,
        State targetState,
        Transition transition,
        InstanceCorrelation correlation,
        ScriptContext context,
        ExecMode mode,
        ExecutionActor? actor = null,
        CancellationToken cancellationToken = default)
    {
        var subFlowConfig = targetState.SubFlow!;
        var subFlowReference = subFlowConfig.Process;
        using var activity =
            SubFlowActivityHelper.StartActivity($"SubFlow.Start/{subFlowReference.Domain}/{subFlowReference.Key}");
        var rootInstanceId = parentInstance.GetRootInstanceId();
        SubFlowActivityHelper.EnrichWithStart(
            activity,
            parentInstance.Id,
            subFlowReference.Domain,
            subFlowReference.Key,
            correlation.SubFlowInstanceId,
            rootInstanceId);
        activity?.SetTag("vnext.subflow.type", subFlowConfig.Type.Code == "S" ? "subflow" : "subprocess");
        activity?.SetTag("vnext.subflow.parent.state", targetState.Key);
        activity?.SetTag("vnext.subflow.parent.transition", transition.Key);

        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.Domain] = workflow.Domain,
            [TelemetryConstants.TagNames.Flow] = workflow.Key,
            [TelemetryConstants.TagNames.FlowVersion] = workflow.Version,
            [TelemetryConstants.TagNames.InstanceId] = parentInstance.Id,
            [TelemetryConstants.TagNames.InstanceKey] = parentInstance.Key ?? "N/A",
            [TelemetryConstants.TagNames.SubflowInstanceId] = correlation.SubFlowInstanceId,
            [TelemetryConstants.TagNames.RootInstanceId] = rootInstanceId,
        });

        // Handle input mapping if a script mapping is configured. "Script present" is the same test the output side
        // uses (SubflowOutputMappingService.PrepareAsync): a stored mapping with no code does not shadow a block.
        ScriptResponse? inputMappingResult = null;
        if (subFlowConfig.Mapping is { HasMappingCode: true })
        {
            var mappingResult = await HandleInputMappingAsync(subFlowConfig, workflow, context, cancellationToken);
            if (!mappingResult.IsSuccess)
            {
                SubFlowActivityHelper.SetError(activity, mappingResult.Error.Message ?? mappingResult.Error.Code);
                return Result<string?>.Fail(mappingResult.Error);
            }

            inputMappingResult = mappingResult.Value;
        }
        else if (subFlowConfig.Input is { } inputBlock && subFlowConfig.Type.Equals(SubFlowType.SubFlow))
        {
            // Declarative input block (Phase C1): builds the same ScriptResponse the InputHandler returns, so the
            // path below (MappingHeadersOf → ResolveInstanceCallCredential) runs unchanged. A stored definition with
            // both `mapping` and `input` runs the script (above). Like the output block, only an S SubFlow runs it.
            var blockResult = await ExecuteInputBlockAsync(
                subFlowConfig, inputBlock, workflow, parentInstance, targetState.Key, context, actor, cancellationToken);
            if (!blockResult.IsSuccess)
            {
                SubFlowActivityHelper.SetError(activity, blockResult.Error.Message ?? blockResult.Error.Code);
                return Result<string?>.Fail(blockResult.Error);
            }

            inputMappingResult = blockResult.Value;
        }

        return await StartSubFlowInternalAsync(
            workflow,
            parentInstance,
            subFlowConfig.Process,
            targetState.Key,
            transition.Key,
            correlation,
            subFlowConfig.Type.Code,
            inputMappingResult,
            subFlowConfig.HasTimeoutOverride ? subFlowConfig.Overrides!.Timeout : null,
            subFlowConfig.Overrides,
            context?.Headers is null ? null : context.GetTransportHeadersAsDictionary(),
            activity,
            cancellationToken);
    }

    /// <summary>
    /// Internal method that contains the common logic for starting SubFlow/SubProcess workflows.
    /// </summary>
    /// <returns>Result indicating success or failure; on success the child's resolved flow version (null when not reported).</returns>
    private async Task<Result<string?>> StartSubFlowInternalAsync(
        Definitions.Workflow workflow,
        Instance parentInstance,
        Reference subFlowReference,
        string stateKey,
        string transitionKey,
        InstanceCorrelation correlation,
        string subFlowTypeCode,
        ScriptResponse? inputMappingResult,
        WorkflowTimeout? timeoutOverride,
        SubFlowOverrides? overrides,
        IReadOnlyDictionary<string, string>? callerHeaders,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var rootInstanceId = parentInstance.GetRootInstanceId();

        // Prepare instance creation input
        var createInstanceInput = new CreateInstanceInput
        {
            Id = correlation.SubFlowInstanceId,
            Callback = configuration["DAPR_APP_ID"],
            Key = parentInstance.Key ?? string.Empty,
            Attributes = inputMappingResult?.Data != null
                    ? JsonSerializer.SerializeToElement(inputMappingResult.Data)
                    : null,
            Tags =
                [
                    $"parent.key:{parentInstance.Key}",
                    $"parent.domain:{workflow.Domain}",
                    $"parent.flow:{workflow.Key}",
                    $"root.instance:{rootInstanceId}"
                ],
            ExtraProperties = new ExtraPropertyDictionary
            {
                [DomainConsts.MetaDataKeys.Id] = parentInstance.Id,
                [DomainConsts.MetaDataKeys.Key] = parentInstance.Key ?? string.Empty,
                [DomainConsts.MetaDataKeys.Domain] = workflow.Domain,
                [DomainConsts.MetaDataKeys.Flow] = workflow.Key,
                [DomainConsts.MetaDataKeys.Version] = workflow.Version,
                [DomainConsts.MetaDataKeys.State] = stateKey,
                [DomainConsts.MetaDataKeys.Transition] = transitionKey,
                [DomainConsts.MetaDataKeys.FlowType] = subFlowTypeCode,
                [DomainConsts.MetaDataKeys.RootInstanceId] = rootInstanceId
            }
        };

        // A history-none parent requires a history-none SubFlow child (vnext#1006). Stamped only for
        // none, so children of flag-less parents carry exactly the same metadata as before; the child
        // enforces it at start (InstanceCommandAppService.StartAsync), which works across domains.
        if (workflow.SuppressesHistory)
            createInstanceInput.ExtraProperties[DomainConsts.MetaDataKeys.History] = HistoryMode.None.Code;

        // Apply timeout override from SubFlow config if present
        if (timeoutOverride != null)
        {
            // Shared options on the write side too: the reader
            // (InstanceMetadataExtensions.ResolveEffectiveTimeout) needs ScriptCodeJsonConverter
            // for WorkflowTimeout.Mapping, and a stamp written with the defaults would not survive
            // it. Stamps written by earlier runtimes stay readable — the shared options are
            // PropertyNameCaseInsensitive.
            createInstanceInput.ExtraProperties[DomainConsts.MetaDataKeys.TimeoutOverride] =
                JsonSerializer.Serialize(timeoutOverride, JsonSerializerConstants.JsonOptions);
        }

        // Stamp the parent's WHOLE per-transition / per-state override maps onto the child: role grants,
        // query grants, the long-poll override and the state/transition-scoped view swaps all travel
        // here and are resolved CHILD-side (SubFlowOverrideStamp). Only the legacy view-key map
        // (overrides.views / viewOverrides) is not stamped — it is still applied parent-side after descent.
        if (overrides?.Transitions is { Count: > 0 })
        {
            createInstanceInput.ExtraProperties[DomainConsts.MetaDataKeys.TransitionRoleOverrides] =
                JsonSerializer.Serialize(overrides.Transitions);
        }

        if (overrides?.States is { Count: > 0 })
        {
            createInstanceInput.ExtraProperties[DomainConsts.MetaDataKeys.StateRoleOverrides] =
                JsonSerializer.Serialize(overrides.States);
        }

        // Apply additional properties from input mapping if available
        if (inputMappingResult != null)
        {
            if (!inputMappingResult.Key.IsNullOrEmpty())
            {
                createInstanceInput.Key = inputMappingResult.Key;
            }

            if (!inputMappingResult.Tags.IsNullOrEmpty())
            {
                var existingTags = createInstanceInput.Tags?.ToList() ?? new List<string>();
                existingTags.AddRange(inputMappingResult.Tags);
                createInstanceInput.Tags = existingTags.ToArray();
            }
        }

        // HTTP headers are case-insensitive. Use OrdinalIgnoreCase so a parent/root instance-id
        // supplied by the input mapping (any casing) is REPLACED by the framework-stamped value
        // below instead of producing a duplicate header entry.
        // Caller credential: the mapping decides, blank included — HttpTaskInvocation.ResolveInstanceCallCredential. Blank entries stay "" on purpose; the exit drops them.
        var mappingHeaders = MappingHeadersOf(inputMappingResult, subFlowReference.Key);
        if (!mappingHeaders.IsSuccess)
        {
            // Fail closed before any child is started: unreadable headers must not read as "no headers".
            SubFlowActivityHelper.SetError(activity, mappingHeaders.Error.Message ?? mappingHeaders.Error.Code);
            return Result<string?>.Fail(mappingHeaders.Error);
        }

        var headers = HttpTaskInvocation.ResolveInstanceCallCredential(mappingHeaders.Value, callerHeaders);

        headers[TelemetryConstants.HeaderNames.ParentInstanceId] = parentInstance.Id.ToString();
        headers[TelemetryConstants.HeaderNames.RootInstanceId] = rootInstanceId.ToString();

        var subFlowStartInput = new StartInstanceInput(
                subFlowReference.Domain,
                subFlowReference.Key,
                subFlowReference.Version,
                sync: true
            )
        {
            Instance = createInstanceInput,
            Headers = headers,
            RouteValues = inputMappingResult?.RouteValues ?? new Dictionary<string, string?>(),
            StrictIdempotency = true, // Service-to-service call: return 409 if active instance exists
            // sync=true awaits the child's activation to a rest point; the response body is read
            // for IsSuccess only (below), so the child must not project attributes/extensions.
            SuppressResponseEnrichment = true,
            // Payload comes from the subflow input mapping, not a client.
            TrustedPayload = true
        };

        var startResult = await instanceCommandGateway.StartSubAsync(subFlowStartInput, cancellationToken);

        if (!startResult.IsSuccess)
        {
            var error = startResult.Error;

            SubFlowActivityHelper.SetError(activity, $"{error.Code}: {error.Message}");
            logger.SubFlowStartFailed(
                subFlowReference.Key,
                parentInstance.Id,
                error.Code,
                error.Message ?? string.Empty);

            return Result<string?>.Fail(error);
        }

        SubFlowActivityHelper.SetSuccess(activity);
        logger.SubFlowStarted(subFlowReference.Key, parentInstance.Id);

        return Result<string?>.Ok(startResult.Value?.FlowVersion);
    }

    /// <summary>The <c>Headers</c> shapes a SubFlow input mapping may return, named in the start failure.</summary>
    private const string AcceptedHeaderShapes =
        "a string dictionary, an object dictionary / ExpandoObject, or a JSON object; use a dictionary or an ExpandoObject";

    /// <summary>
    /// The headers a mapping result carries. Exactly three shapes are read: a string dictionary as is; an object
    /// dictionary / <c>ExpandoObject</c> by its values' text; a JSON object
    /// (<see cref="HttpTaskInvocation.ReadHeaderObject"/>). <c>null</c> headers are "no headers" (<c>Ok(null)</c>). Any
    /// other shape — an anonymous object or a script-defined class included — FAILS the SubFlow start: reading it as
    /// "no headers" would refill a credential the author meant to withhold (fail closed).
    /// </summary>
    private static Result<Dictionary<string, string?>?> MappingHeadersOf(ScriptResponse? mapping, string subFlowKey)
    {
        object? headers = mapping?.Headers;
        switch (headers)
        {
            case null:
                return Result<Dictionary<string, string?>?>.Ok(null);
            case IDictionary<string, string?> strings:
                return Result<Dictionary<string, string?>?>.Ok(new Dictionary<string, string?>(strings));
            case IDictionary<string, object?> objects:
                return Result<Dictionary<string, string?>?>.Ok(
                    objects.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString()));
            case JsonElement element when element.ValueKind == JsonValueKind.Object:
                return Result<Dictionary<string, string?>?>.Ok(HttpTaskInvocation.ReadHeaderObject(element));
            default:
                return Result<Dictionary<string, string?>?>.Fail(WorkflowErrors.SubFlowInputMappingFailed(
                    subFlowKey,
                    $"The input mapping returned Headers of type '{headers.GetType().Name}', which cannot be read as headers. " +
                    $"Accepted shapes: {AcceptedHeaderShapes}."));
        }
    }

    /// <summary>
    /// Handles input mapping for SubFlow/SubProcess by compiling and executing the mapping script.
    /// </summary>
    /// <param name="subFlowConfig">The SubFlow configuration containing mapping information.</param>
    /// <param name="workflow">The parent workflow definition, used to supply flow-level script settings.</param>
    /// <param name="context">The script context for mapping execution.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>Result containing the mapping response or error.</returns>
    private async Task<Result<ScriptResponse?>> HandleInputMappingAsync(
        Definitions.SubFlow subFlowConfig,
        Definitions.Workflow workflow,
        ScriptContext context,
        CancellationToken cancellationToken = default)
    {
        // Determine the appropriate mapping interface based on SubFlow type
        var mappingInterfaceType = subFlowConfig.Type.Code == "S"
            ? typeof(ISubFlowMapping)
            : typeof(ISubProcessMapping);

        return await ResultExtensions.TryAsync<ScriptResponse?>(async ct =>
        {
            using var scriptActivity = ScriptActivityHelper.StartExecuteActivity(SubFlowInputMappingScriptKind); // skipcq: CS-W1100 — the span ends on dispose

            // Cast to the appropriate mapping interface and execute InputHandler
            if (subFlowConfig.Type.Code == "S")
            {
                var subFlowMapping = await scriptEngine.CompileToInstanceAsync<ISubFlowMapping>(
                    subFlowConfig.Mapping,
                    flowScripts: workflow.Scripts,
                    cancellationToken: ct);
                using var invokeActivity = ScriptActivityHelper.StartInvokeActivity();
                return await subFlowMapping.InputHandler(context);
            }

            if (subFlowConfig.Type.Code == "P")
            {
                var subProcessMapping = await scriptEngine.CompileToInstanceAsync<ISubProcessMapping>(
                    subFlowConfig.Mapping,
                    flowScripts: workflow.Scripts,
                    cancellationToken: ct);
                using var invokeActivity = ScriptActivityHelper.StartInvokeActivity();
                return await subProcessMapping.InputHandler(context);
            }

            // If we reach here, casting failed
            throw new InvalidOperationException(
                $"Failed to cast mapping instance to {mappingInterfaceType.Name} for SubFlow type '{subFlowConfig.Type.Code}'");
        }, cancellationToken, ex => WorkflowErrors.SubFlowInputMappingFailed(
            subFlowConfig.Process.Key, ex.Message, ex.StackTrace));
    }

    /// <summary>
    /// Runs the SubFlow's declarative <c>input</c> block under the same <c>Script.Execute</c> span (kind
    /// <c>subflowInputMapping</c>) the input mapping script opens; a failure marks it as an error.
    /// </summary>
    private async Task<Result<ScriptResponse?>> ExecuteInputBlockAsync(
        Definitions.SubFlow subFlowConfig,
        MappingBlock block,
        Definitions.Workflow workflow,
        Instance parentInstance,
        string stateKey,
        ScriptContext context,
        ExecutionActor? actor,
        CancellationToken cancellationToken)
    {
        using var scriptActivity = ScriptActivityHelper.StartExecuteActivity(SubFlowInputMappingScriptKind);

        var result = await RunInputBlockAsync(subFlowConfig, block, stateKey, context, actor, cancellationToken);
        if (result.IsSuccess)
            return result;

        var shaped = InputBlockFailed(workflow, parentInstance, stateKey, subFlowConfig.Process.Key, result.Error);
        scriptActivity.SetResultError(shaped.Code, shaped.Message);
        return Result<ScriptResponse?>.Fail(shaped);
    }

    /// <summary>
    /// Applies the block to an empty start document <c>{ attributes, headers, key, tags }</c> and converts it to the
    /// <see cref="ScriptResponse"/> an InputHandler returns. Variables: <c>data</c> (parent data), <c>input</c> (the hop
    /// request that entered the SubFlow state, null on an automatic chain), <c>instance</c>, <c>now</c>, <c>user</c> (the
    /// parent hop's caller; none without a factory or actor), <c>context</c>, <c>headers</c>.
    /// </summary>
    private async Task<Result<ScriptResponse?>> RunInputBlockAsync(
        Definitions.SubFlow subFlowConfig,
        MappingBlock block,
        string stateKey,
        ScriptContext context,
        ExecutionActor? actor,
        CancellationToken cancellationToken)
    {
        var planResult = DeclarativePlanCache.GetOrBuild(_compiler, block, DeclarativeSiteKind.SubFlowInput);
        if (!planResult.IsSuccess)
            return Result<ScriptResponse?>.Fail(planResult.Error);

        var caller = callerAccessorFactory is not null && actor is { } hopActor
            ? callerAccessorFactory.ForHop(hopActor, HopHeadersOf(context), cancellationToken)
            : NullCallerAccessor.Instance;
        var scope = ExpressionScope.Create(context, _clock, caller, ScopeKind.SubFlowInput);
        var site = new DeclarativeSite(subFlowConfig.Process.Key, InputPhase)
        {
            Logger = logger,
            // The wrapping SubFlowInputMappingFailed already names the SubFlow; the inner message names the state.
            ErrorSubject = $"state '{stateKey}'",
            ErrorCode = WorkflowErrorCodes.SubflowStartFailed
        };

        var document = new JsonObject();
        var applied = await DeclarativeBlockEngine.ApplyAsync(
            planResult.Value!, scope, document, result: null, site, cancellationToken);
        if (!applied.IsSuccess)
            return Result<ScriptResponse?>.Fail(applied.Error);

        return ToScriptResponse(document, site);
    }

    /// <summary>The parent hop's request headers the caller-role provider reads (transport headers, not the script view).</summary>
    private static IReadOnlyDictionary<string, string?>? HopHeadersOf(ScriptContext context) =>
        context.Headers is null
            ? null
            : context.GetTransportHeadersAsDictionary().ToDictionary(kv => kv.Key, kv => (string?)kv.Value);

    /// <summary>
    /// The start document as the <see cref="ScriptResponse"/> the starter consumes: <c>attributes</c> → <c>Data</c>,
    /// <c>headers</c> → <c>Headers</c> (every value as a string: strings verbatim, others as their JSON text, null kept),
    /// <c>key</c> → <c>Key</c> (a string), <c>tags</c> → <c>Tags</c> (an array of strings). Any other shape fails.
    /// </summary>
    private static Result<ScriptResponse?> ToScriptResponse(JsonObject document, DeclarativeSite site)
    {
        var response = new ScriptResponse();

        // A JsonNode (a reference type), as the start path tests `Data != null` dynamically — a boxed JsonElement
        // cannot be compared to null by the runtime binder. Serialized into the start request as is.
        if (document[AttributesMember] is { } attributes)
            response.Data = attributes;

        var headersNode = document[HeadersMember];
        switch (headersNode)
        {
            case null:
                break;
            case JsonObject headers:
                response.Headers = HeadersAsStrings(headers);
                break;
            default:
                return Fail(site, $"$.headers is a JSON {headersNode.GetValueKind()}, not an object.");
        }

        var keyNode = document[KeyMember];
        switch (keyNode)
        {
            case null:
                break;
            case JsonValue value when value.TryGetValue<string>(out var key):
                response.Key = key;
                break;
            default:
                return Fail(site, $"$.key is a JSON {keyNode.GetValueKind()}, not a string.");
        }

        var tagsNode = document[TagsMember];
        switch (tagsNode)
        {
            case null:
                break;
            case JsonArray tags:
                var values = new string[tags.Count];
                for (var i = 0; i < tags.Count; i++)
                {
                    if (tags[i] is not JsonValue item || !item.TryGetValue<string>(out var tag))
                        return Fail(site, $"$.tags[{i}] is not a string; tags must be an array of strings.");
                    values[i] = tag;
                }

                response.Tags = values;
                break;
            default:
                return Fail(site, $"$.tags is a JSON {tagsNode.GetValueKind()}, not an array; tags must be an array of strings.");
        }

        return Result<ScriptResponse?>.Ok(response);

        static Result<ScriptResponse?> Fail(DeclarativeSite site, string detail) =>
            Result<ScriptResponse?>.Fail(site.Failure(detail));
    }

    /// <summary>A <c>headers</c> object with every value as a string (strings verbatim, others as their JSON text).</summary>
    private static Dictionary<string, string?> HeadersAsStrings(JsonObject headers)
    {
        var copy = new Dictionary<string, string?>(headers.Count);
        foreach (var (name, item) in headers)
        {
            copy[name] = item switch
            {
                null => null,
                JsonValue v when v.TryGetValue<string>(out var text) => text,
                _ => item.ToJsonString()
            };
        }

        return copy;
    }

    /// <summary>
    /// A failing block, logged and shaped as a failing input mapping script
    /// (<see cref="WorkflowErrors.SubFlowInputMappingFailed"/>). A caller-role provider failure
    /// (<c>Authorization:110006</c>) is kept: it is retryable.
    /// </summary>
    private Error InputBlockFailed(
        Definitions.Workflow workflow, Instance parentInstance, string stateKey, string subFlowKey, Error error)
    {
        var message = error.Message ?? error.Code;
        logger.SubFlowDeclarativeInputFailed(workflow.Key, stateKey, subFlowKey, parentInstance.Id, message);

        return error.Code == WorkflowErrorCodes.CallerRolesUnavailable
            ? error
            : WorkflowErrors.SubFlowInputMappingFailed(subFlowKey, message);
    }
}
