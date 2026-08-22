using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Globalization;
using BBT.Aether.Application.Services;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Functions.Contracts;
using BBT.Workflow.Functions.Validation;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Coordinator;
using BBT.Workflow.Tasks.Evaluators;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Functions;

/// <summary>
/// Application service for function operations using Railway pattern.
/// </summary>
public sealed class FunctionAppService(
    IServiceProvider serviceProvider,
    IRuntimeInfoProvider runtimeInfoProvider,
    IInstanceRepository instanceRepository,
    IScriptContextFactory scriptContextFactory,
    IComponentCacheStore componentCacheStore,
    ICurrentSchema currentSchema,
    ITaskCoordinator taskCoordinator,
    IScriptEngine scriptEngine,
    IDynamicExpressoValueEvaluator keyEvaluator,
    IStateStoreCacheGateway cacheGateway,
    IRemoteInvokerService remoteInvoker,
    IFunctionAccessPolicy functionAccessPolicy,
    IFunctionRequestValidationService functionRequestValidationService,
    IWorkflowMetrics workflowMetrics)
    : ApplicationService(serviceProvider), IFunctionAppService
{
    /// <inheritdoc />
    public async Task<Result<FunctionResponseOutput>> GetFunctionByKeyAsync(
        string key,
        string domain,
        string? version = null,
        Dictionary<string, string?>? headers = null,
        Dictionary<string, string?>? queryParameters = null,
        JsonElement? body = null,
        string? httpMethod = null,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(domain);
        using (currentSchema.Change(RuntimeSysSchemaInfo.Functions))
        {
            return await componentCacheStore
                .GetFunctionAsync(domain, key, version, cancellationToken)
                .BindAsync(function =>
                    ExecuteFunctionAsync(function, null, null, headers, queryParameters, body, httpMethod, cancellationToken));
        }
    }

    /// <inheritdoc />
    public async Task<Result<FunctionResponseOutput>> GetFunctionByInstanceAsync(
        string key,
        string flow,
        string domain,
        string instanceKey,
        Dictionary<string, string?>? headers = null,
        Dictionary<string, string?>? queryParameters = null,
        JsonElement? body = null,
        string? httpMethod = null,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(domain);
        using (currentSchema.Change(flow))
        {
            var instance = await instanceRepository.FindByIdentifierAsync(instanceKey, cancellationToken);
            if (instance == null)
                return Result<FunctionResponseOutput>.Fail(WorkflowErrors.InstanceNotFound(instanceKey));

            var rootId = instance.GetRootInstanceId();
            if (rootId != instance.Id)
            {
                Activity.Current?.SetTag(TelemetryConstants.TagNames.RootInstanceId, rootId.ToString());
                Activity.Current?.SetBaggage(TelemetryConstants.TagNames.RootInstanceId, rootId.ToString());
            }

            return await componentCacheStore
                .GetFlowAsync(domain, flow, instance.FlowVersion, cancellationToken)
                .BindAsync(workflow =>
                    ResolveFunctionAndExecuteAsync(domain, key, instance, workflow, headers, queryParameters, body, httpMethod, cancellationToken));
        }
    }

    /// <inheritdoc />
    public async Task<Result<List<InstanceAndDataModel>>> GetFunctionsAsync(
        string domain,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(domain);
        using (currentSchema.Change(RuntimeSysSchemaInfo.Functions))
        {
            var result = await instanceRepository.GetActiveDataListAsync(cancellationToken);
            return Result<List<InstanceAndDataModel>>.Ok(result);
        }
    }

    /// <summary>
    /// Resolves the function reference from the workflow and delegates to <see cref="ExecuteFunctionAsync"/>.
    /// Guards against the function not being registered in the given workflow.
    /// </summary>
    private Task<Result<FunctionResponseOutput>> ResolveFunctionAndExecuteAsync(
        string domain,
        string key,
        Instance instance,
        Definitions.Workflow workflow,
        Dictionary<string, string?>? headers,
        Dictionary<string, string?>? queryParameters,
        JsonElement? body,
        string? httpMethod,
        CancellationToken cancellationToken)
    {
        var functionReference = workflow.FindFunction(key);
        return componentCacheStore
            .GetFunctionAsync(domain, key, functionReference?.Version, cancellationToken)
            .BindAsync(function =>
                ExecuteFunctionAsync(function, instance, workflow, headers, queryParameters, body, httpMethod, cancellationToken));
    }

    /// <summary>
    /// Builds the script context, executes all function tasks, and extracts the response.
    /// </summary>
    private async Task<Result<FunctionResponseOutput>> ExecuteFunctionAsync(
        Function function,
        Instance? instance,
        Definitions.Workflow? workflow,
        Dictionary<string, string?>? headers,
        Dictionary<string, string?>? queryParameters,
        JsonElement? body,
        string? httpMethod,
        CancellationToken cancellationToken)
    {
        var access = await functionAccessPolicy.AuthorizeAsync(
            function, instance, workflow, headers, queryParameters, cancellationToken);
        if (!access.IsSuccess)
            return Result<FunctionResponseOutput>.Fail(access.Error);

        // Contract enforcement. Both gates are opt-in: a function that declares no verbs accepts every
        // verb, and one that declares no input schema accepts any body - so definitions authored before
        // contract declaration behave exactly as before. Runs after authorization so an unauthorized
        // caller learns nothing about the function's shape.
        if (!function.SupportsVerb(httpMethod))
        {
            Logger.FunctionVerbRejected(function.Key, httpMethod!, string.Join(", ", function.Verbs));
            return Result<FunctionResponseOutput>.Fail(
                WorkflowErrors.FunctionVerbNotAllowed(function.Key, httpMethod!, function.Verbs));
        }

        object scriptBody = body.HasValue
            ? (object)body.Value
            : new JsonData("{}");

        // Carry the domain-supplied cache vary-by config into the script context so the generic
        // varyKey() key-expression helper can read it (the runtime imposes no header convention itself).
        var metadata = new Dictionary<string, object>();
        if (function.Cache is { } cacheConfig)
        {
            metadata[DynamicExpressoValueEvaluator.VaryByHeadersMetadataKey] = cacheConfig.VaryByHeaders;
            metadata[DynamicExpressoValueEvaluator.VaryByPrefixesMetadataKey] = cacheConfig.VaryByHeaderPrefixes;
        }

        // Built once and shared by input-schema rule evaluation, the cache key expression and the tasks.
        // Lazy because a function whose input schema declares no rules must not pay for a context it
        // would only need after validation anyway.
        var lazyScriptContext = new LazyScriptContext(ct => scriptContextFactory.NewBuilder(instanceRepository)
            .WithWorkflow(workflow)
            .WithInstance(instance)
            .WithRuntime(runtimeInfoProvider)
            .WithBody(scriptBody)
            .WithHeaders(headers)
            .WithQueryParameters(queryParameters)
            .WithMetadata(metadata)
            .BuildAsync(ct));

        var inputValidation = await functionRequestValidationService.ValidateRequestAsync(
            function, body, lazyScriptContext, headers, cancellationToken);
        if (!inputValidation.IsSuccess)
            return Result<FunctionResponseOutput>.Fail(inputValidation.Error);

        var scriptContextResult = await lazyScriptContext.GetAsync(cancellationToken);
        if (!scriptContextResult.IsSuccess)
            return Result<FunctionResponseOutput>.Fail(scriptContextResult.Error);

        var scriptContext = scriptContextResult.Value!;

        // Read-through cache: when the function opts in, serve the cached response on a hit (tasks skipped).
        string? cacheKey = null;
        if (function.Cache is { } cache && cache.HasKeySource)
        {
            if (cache.KeyExpression is not null &&
                cache.VaryByHeaders.Count == 0 && cache.VaryByHeaderPrefixes.Count == 0)
                Logger.LogWarning(
                    "Function {FunctionKey}: cache enabled without varyByHeaders/varyByHeaderPrefixes; a keyExpression using varyKey() falls back to ALL request headers (poor hit rate).",
                    function.Key);

            var keyResult = ResolveCacheKey(cache, scriptContext);
            if (!keyResult.IsSuccess)
            {
                // A key that cannot be computed cannot be cached — never fail the endpoint over it; run uncached.
                Logger.LogWarning(
                    "Function {FunctionKey}: cache key expression failed ({Error}); executing without caching.",
                    function.Key, keyResult.Error.Message ?? "unknown");
            }
            else
            {
                cacheKey = keyResult.Value;
            }

            if (!string.IsNullOrWhiteSpace(cacheKey))
            {
                var traceContext = remoteInvoker.CreateTraceContext(scriptContext);

                // Generation-namespace: fold the current generation stamp into the key so bumping the
                // stamp (on a dependency change) invalidates every cached variant at once.
                if (cache.HasGenerationSource)
                {
                    var generationResult = await ResolveGenerationAsync(cache, scriptContext, traceContext, cancellationToken);
                    if (!generationResult.IsSuccess)
                    {
                        if (!cache.BypassOnCacheError)
                            return Result<FunctionResponseOutput>.Fail(generationResult.Error);

                        Logger.LogWarning(
                            "Function {FunctionKey}: cache generation read failed; executing without caching (bypassOnCacheError=true).",
                            function.Key);
                        cacheKey = null;
                    }
                    else
                    {
                        cacheKey = $"{cacheKey}:g:{generationResult.Value}";
                    }
                }

                if (!string.IsNullOrWhiteSpace(cacheKey))
                {
                    var read = await cacheGateway.GetAsync(cacheKey, cache.StoreName, cache.Consistency, traceContext, cancellationToken);
                    if (!read.CacheOk)
                    {
                        if (!cache.BypassOnCacheError)
                            return Result<FunctionResponseOutput>.Fail(Error.Failure(
                                WorkflowErrorCodes.ExtensionExecutionFailed,
                                $"Function '{function.Key}' cache read failed."));

                        Logger.LogWarning(
                            "Function {FunctionKey}: cache read failed; executing the function (bypassOnCacheError=true).",
                            function.Key);
                    }
                    else if (read.Hit)
                    {
                        var cached = read.Value.Deserialize<FunctionResponseOutput>(JsonSerializerConstants.JsonOptions);
                        if (cached is not null)
                            return Result<FunctionResponseOutput>.Ok(cached);
                    }
                }
            }
        }

        Result executeResult;
        try
        {
            executeResult = await taskCoordinator.ExecuteAsync(
                function.GetExecuteTasks(),
                null,
                TaskTrigger.Extension,
                TaskExecutionOrigin.Function,
                scriptContext,
                cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Custom function {FunctionKey} execution threw an exception. Domain={Domain}, InstanceId={InstanceId}",
                function.Key,
                function.Domain,
                instance?.Id);

            return Result<FunctionResponseOutput>.Fail(Error.Failure(
                WorkflowErrorCodes.ExtensionExecutionFailed,
                $"Function '{function.Key}' execution failed: {ex.Message}"));
        }

        if (!executeResult.IsSuccess)
        {
            Logger.LogError(
                "Custom function {FunctionKey} execution failed. Domain={Domain}, InstanceId={InstanceId}, Error={ErrorMessage}",
                function.Key,
                function.Domain,
                instance?.Id,
                executeResult.Error.Message ?? "Unknown error");

            return Result<FunctionResponseOutput>.Fail(executeResult.Error);
        }

        var responseResult = await BuildResponseAsync(function, scriptContext, cancellationToken);
        if (!responseResult.IsSuccess)
        {
            Logger.LogError(
                "Custom function {FunctionKey} response mapping failed. Domain={Domain}, InstanceId={InstanceId}, Error={ErrorMessage}",
                function.Key,
                function.Domain,
                instance?.Id,
                responseResult.Error.Message ?? "Unknown error");

            return responseResult;
        }

        // Write-through: cache the computed response on a miss (best-effort unless bypass is disabled).
        if (function.Cache is { } writeCache && !string.IsNullOrWhiteSpace(cacheKey))
        {
            var traceContext = remoteInvoker.CreateTraceContext(scriptContext);
            var stored = await cacheGateway.SetAsync(
                cacheKey!, responseResult.Value, writeCache.TtlInSeconds, writeCache.StoreName,
                writeCache.Consistency, traceContext, cancellationToken);

            if (!stored)
            {
                if (!writeCache.BypassOnCacheError)
                    return Result<FunctionResponseOutput>.Fail(Error.Failure(
                        WorkflowErrorCodes.ExtensionExecutionFailed,
                        $"Function '{function.Key}' cache write failed."));

                Logger.LogWarning(
                    "Function {FunctionKey}: cache write failed; returning the computed result (bypassOnCacheError=true).",
                    function.Key);
            }
        }

        return responseResult;
    }

    /// <summary>
    /// Resolves the cache key from the function's cache config: evaluates the Dynamic Expresso
    /// <c>keyExpression</c> against the script context, or falls back to the static <c>key</c>.
    /// </summary>
    private Result<string?> ResolveCacheKey(FunctionCache cache, ScriptContext scriptContext)
    {
        if (cache.KeyExpression is { } expression && expression.HasMappingCode)
        {
            var result = keyEvaluator.Evaluate(expression, scriptContext);
            return result.IsSuccess
                ? Result<string?>.Ok(result.Value)
                : Result<string?>.Fail(result.Error);
        }

        return Result<string?>.Ok(cache.Key);
    }

    /// <summary>
    /// Reads the cache generation stamp for the given cache config: resolves the generation state key
    /// (Dynamic Expresso expression or static), reads it from the cache, and returns the stamp as a
    /// string. Returns <c>"0"</c> when no generation key resolves or the stamp entry is absent; fails
    /// only when the cache read itself errors.
    /// </summary>
    private async Task<Result<string>> ResolveGenerationAsync(
        FunctionCache cache,
        ScriptContext scriptContext,
        TaskTraceContext traceContext,
        CancellationToken cancellationToken)
    {
        string? generationKey;
        if (cache.GenerationKeyExpression is { HasMappingCode: true } expression)
        {
            var keyResult = keyEvaluator.Evaluate(expression, scriptContext);
            if (!keyResult.IsSuccess)
                return Result<string>.Fail(keyResult.Error);
            generationKey = keyResult.Value;
        }
        else
        {
            generationKey = cache.GenerationKey;
        }

        if (string.IsNullOrWhiteSpace(generationKey))
            return Result<string>.Ok("0");

        var read = await cacheGateway.GetAsync(generationKey, cache.StoreName, cache.Consistency, traceContext, cancellationToken);
        if (!read.CacheOk)
            return Result<string>.Fail(Error.Failure(
                WorkflowErrorCodes.ExtensionExecutionFailed,
                "Cache generation read failed."));

        return Result<string>.Ok(read.Hit ? ExtractGeneration(read.Value) : "0");
    }

    /// <summary>Extracts the generation stamp from its cached JSON value (number or string).</summary>
    private static string ExtractGeneration(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "0",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.Undefined or JsonValueKind.Null => "0",
        _ => value.GetRawText()
    };

    /// <summary>
    /// Builds the final response: uses the <c>output</c> script when defined, otherwise falls back to
    /// legacy single-task extraction from <see cref="ScriptContext.OutputResponse"/>.
    /// When <see cref="Function.RawResponse"/> is <c>true</c>, data is returned unwrapped.
    /// </summary>
    private async Task<Result<FunctionResponseOutput>> BuildResponseAsync(
        Function function,
        ScriptContext scriptContext,
        CancellationToken cancellationToken)
    {
        if (function.Output != null)
        {
            try
            {
                var handler = await scriptEngine.CompileToInstanceAsync<IOutputHandler>(
                    function.Output, flowScripts: scriptContext.Workflow?.Scripts, cancellationToken: cancellationToken);
                var executeStart = Stopwatch.GetTimestamp();
                var scriptResponse = await handler.OutputHandler(scriptContext);
                workflowMetrics.RecordScriptExecutionDuration(
                    "function", "csharp", "success",
                    Stopwatch.GetElapsedTime(executeStart).TotalSeconds);

                if (function.RawResponse)
                    return Result<FunctionResponseOutput>.Ok(CreateRawResponse(
                        function,
                        scriptContext,
                        scriptResponse.Data,
                        (object?)scriptResponse.Headers,
                        scriptResponse.StatusCode));

                return Result<FunctionResponseOutput>.Ok(new FunctionResponseOutput
                {
                    Data = new Dictionary<string, dynamic?> { [function.Key.ToVariableName()] = scriptResponse.Data }
                });
            }
            catch (Exception ex)
            {
                workflowMetrics.RecordScriptRuntimeError("function", "csharp", ex.GetType().Name);
                Logger.LogError(
                    ex,
                    "Custom function {FunctionKey} output ScriptMapping failed. Domain={Domain}, InstanceId={InstanceId}",
                    function.Key,
                    function.Domain,
                    scriptContext.Instance?.Id);

                return Result<FunctionResponseOutput>.Fail(Error.Failure(
                    WorkflowErrorCodes.ExtensionExecutionFailed,
                    $"Function '{function.Key}' output ScriptMapping failed: {ScriptDiagnostics.Explain(ex)}"));
            }
        }

        if (function.RawResponse)
            return Result<FunctionResponseOutput>.Ok(CreateRawResponse(
                function,
                scriptContext,
                ExtractRawFunctionResponse(function, scriptContext)));

        return Result<FunctionResponseOutput>.Ok(CreateWrappedResponse(function, scriptContext));
    }

    /// <summary>
    /// Converts an arbitrary object to a flat <c>Dictionary&lt;string, dynamic?&gt;</c> for raw responses.
    /// </summary>
    internal static Dictionary<string, dynamic?> ToRawDictionary(object? data)
    {
        if (data is Dictionary<string, dynamic?> dict)
            return dict;

        var json = JsonSerializer.Serialize(data);
        return JsonSerializer.Deserialize<Dictionary<string, dynamic?>>(json) ?? [];
    }

    /// <summary>
    /// Raw variant of legacy single-task extraction: returns the task value directly
    /// without wrapping it in the function-key dictionary.
    /// </summary>
    internal static Dictionary<string, dynamic?> ExtractRawFunctionResponse(
        Function function,
        ScriptContext scriptContext)
    {
        var variableKeyTask = GetSingleTaskVariableKey(function);
        if (variableKeyTask == null)
            return [];

        if (!scriptContext.OutputResponse.TryGetValue(variableKeyTask, out var value))
            return [];

        try
        {
            if (value is JsonElement jsonElement)
            {
                var target = jsonElement.TryGetProperty("data", out var dataProp)
                    ? dataProp
                    : jsonElement;

                if (target.ValueKind == JsonValueKind.Object)
                    return JsonSerializer.Deserialize<Dictionary<string, dynamic?>>(target.GetRawText()) ?? [];
            }

            if (value is Dictionary<string, dynamic?> d)
                return d;

            if (value is IDictionary<string, object?> objectDictionary)
                return objectDictionary.ToDictionary(
                    item => item.Key,
                    item => (dynamic?)item.Value);

            return ToRawDictionary(value);
        }
        catch { /* ignore */ }

        return [];
    }

    /// <summary>
    /// Legacy single-task output extraction from <see cref="ScriptContext.OutputResponse"/>.
    /// Unwraps the inner <c>data</c> property when the value is a JSON element wrapper.
    /// </summary>
    internal static Dictionary<string, dynamic?> ExtractFunctionResponse(
        Function function,
        ScriptContext scriptContext)
    {
        var response = new Dictionary<string, dynamic?>();
        var variableKeyFunction = function.Key.ToVariableName();
        var variableKeyTask = GetSingleTaskVariableKey(function);
        if (variableKeyTask == null)
            return response;

        if (scriptContext.OutputResponse.TryGetValue(variableKeyTask, out var value))
        {
            try
            {
                response[variableKeyFunction] = value is JsonElement jsonElement &&
                                                jsonElement.TryGetProperty("data", out var dataProperty)
                    ? dataProperty
                    : value;
            }
            catch
            {
                // If extraction fails, use the original value
                response[variableKeyFunction] = value!;
            }
        }

        return response;
    }

    internal static FunctionResponseOutput CreateRawResponse(
        Function function,
        ScriptContext scriptContext,
        object? data,
        object? outputHeaders = null,
        int? outputStatusCode = null)
    {
        var (singleStatusCode, singleHeaders) = ExtractSingleTaskHttpMetadata(function, scriptContext);

        // Output script may set its own headers/status (multi-task scenario); when it does,
        // prefer them. Otherwise fall back to single-task metadata (existing behavior preserved).
        return new FunctionResponseOutput
        {
            Data = data,
            StatusCode = outputStatusCode ?? singleStatusCode,
            Headers = NormalizeHeaders(outputHeaders) ?? singleHeaders
        };
    }

    internal static FunctionResponseOutput CreateWrappedResponse(
        Function function,
        ScriptContext scriptContext) => new()
    {
        Data = ExtractFunctionResponse(function, scriptContext)
    };

    private static (int? StatusCode, Dictionary<string, string>? Headers) ExtractSingleTaskHttpMetadata(
        Function function,
        ScriptContext scriptContext)
    {
        var variableKeyTask = GetSingleTaskVariableKey(function);
        if (variableKeyTask == null)
        {
            return (null, null);
        }

        if (!scriptContext.TaskResponse.TryGetValue(variableKeyTask, out var response) ||
            response is not object taskResponse)
        {
            return (null, null);
        }

        return (ExtractStatusCode(taskResponse), ExtractHeaders(taskResponse));
    }

    private static string? GetSingleTaskVariableKey(Function function)
    {
        var tasks = function.GetExecuteTasks();
        return tasks.Count == 1
            ? tasks[0].Task.Key.ToVariableName()
            : null;
    }

    private static int? ExtractStatusCode(object response)
    {
        if (response is StandardTaskResponse standardResponse)
            return standardResponse.StatusCode;

        if (response is JsonElement jsonElement)
        {
            if (jsonElement.TryGetProperty("statusCode", out var statusCodeProperty) &&
                statusCodeProperty.ValueKind == JsonValueKind.Number &&
                statusCodeProperty.TryGetInt32(out var statusCode))
            {
                return statusCode;
            }

            return null;
        }

        if (response is IDictionary<string, object?> dictionary &&
            TryGetDictionaryValue(dictionary, "statusCode", out var value))
        {
            return value switch
            {
                int statusCode => statusCode,
                long statusCode when statusCode is >= int.MinValue and <= int.MaxValue => (int)statusCode,
                string statusCode when int.TryParse(statusCode, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => null
            };
        }

        return null;
    }

    private static Dictionary<string, string>? ExtractHeaders(object response)
    {
        if (response is StandardTaskResponse standardResponse)
            return standardResponse.Headers;

        if (response is JsonElement jsonElement)
        {
            return jsonElement.TryGetProperty("headers", out var headersProperty) &&
                   headersProperty.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(headersProperty.GetRawText())
                : null;
        }

        if (response is IDictionary<string, object?> dictionary &&
            TryGetDictionaryValue(dictionary, "headers", out var value))
        {
            return NormalizeHeaders(value);
        }

        return null;
    }

    /// <summary>
    /// Normalizes a loosely-typed headers object (e.g. <see cref="ScriptResponse.Headers"/> which is
    /// <c>dynamic</c>) into a <see cref="Dictionary{TKey,TValue}"/> of string headers.
    /// Accepts <see cref="Dictionary{TKey,TValue}"/>, <see cref="IDictionary{TKey,TValue}"/> of objects,
    /// or any JSON-serializable object. Returns null when there are no usable headers.
    /// </summary>
    private static Dictionary<string, string>? NormalizeHeaders(object? value)
    {
        if (value is null)
            return null;

        try
        {
            var headers = value switch
            {
                Dictionary<string, string> typedHeaders => typedHeaders,
                IDictionary<string, object?> objectHeaders => objectHeaders
                    .Where(header => header.Value != null)
                    .ToDictionary(header => header.Key, header => Convert.ToString(header.Value, CultureInfo.InvariantCulture) ?? string.Empty),
                _ => JsonSerializer.Deserialize<Dictionary<string, string>>(JsonSerializer.Serialize(value))
            };

            return headers is { Count: > 0 } ? headers : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetDictionaryValue(
        IDictionary<string, object?> dictionary,
        string key,
        out object? value)
    {
        foreach (var item in dictionary)
        {
            if (string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = item.Value;
                return true;
            }
        }

        value = null;
        return false;
    }
}
