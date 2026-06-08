using System.Text;
using System.Text.Json;
using System.Globalization;
using BBT.Aether.Application.Services;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Workflow.Authorization;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;

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
    ICurrentUser currentUser,
    ITransitionAuthorizationManager transitionAuthorizationManager) : ApplicationService(serviceProvider), IFunctionAppService
{
    /// <inheritdoc />
    public async Task<Result<FunctionResponseOutput>> GetFunctionByKeyAsync(
        string key,
        string domain,
        string? version = null,
        Dictionary<string, string?>? headers = null,
        Dictionary<string, string?>? queryParameters = null,
        JsonElement? body = null,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(domain);
        using (currentSchema.Use(RuntimeSysSchemaInfo.Functions))
        {
            return await componentCacheStore
                .GetFunctionAsync(domain, key, version, cancellationToken)
                .BindAsync(function =>
                    ExecuteFunctionAsync(function, null, null, headers, queryParameters, body, cancellationToken));
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
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(domain);
        using (currentSchema.Use(flow))
        {
            var instance = await instanceRepository.FindByIdentifierAsync(instanceKey, cancellationToken);
            if (instance == null)
                return Result<FunctionResponseOutput>.Fail(WorkflowErrors.InstanceNotFound(instanceKey));

            return await componentCacheStore
                .GetFlowAsync(domain, flow, instance.FlowVersion, cancellationToken)
                .BindAsync(workflow =>
                    ResolveFunctionAndExecuteAsync(domain, key, instance, workflow, headers, queryParameters, body, cancellationToken));
        }
    }

    /// <inheritdoc />
    public async Task<Result<List<InstanceAndDataModel>>> GetFunctionsAsync(
        string domain,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(domain);
        using (currentSchema.Use(RuntimeSysSchemaInfo.Functions))
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
        CancellationToken cancellationToken)
    {
        var functionReference = workflow.FindFunction(key);
        return componentCacheStore
            .GetFunctionAsync(domain, key, functionReference?.Version, cancellationToken)
            .BindAsync(function =>
                ExecuteFunctionAsync(function, instance, workflow, headers, queryParameters, body, cancellationToken));
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
        CancellationToken cancellationToken)
    {
        // Scope enforcement. Domain is exempt; Instance/Flow require an instance;
        // Flow additionally requires the function to be declared in the instance's flow.
        if (!function.Scope.Equals(TaskScope.Domain))
        {
            if (instance == null)
                return Result<FunctionResponseOutput>.Fail(
                    WorkflowErrors.FunctionScopeNotSatisfied(function.Key, function.Scope.Description));

            if (function.Scope.Equals(TaskScope.Flow) &&
                !(workflow?.Functions.Any(f => f.Key == function.Key) ?? false))
            {
                return Result<FunctionResponseOutput>.Fail(
                    WorkflowErrors.FunctionScopeNotSatisfied(function.Key, function.Scope.Description));
            }
        }

        // Custom-function authorization: when the function defines Roles, the caller must resolve to an allow.
        // Built-in functions never reach this path (they use their own handlers/authorization).
        if (function.Roles.Count > 0)
        {
            var allowed = await transitionAuthorizationManager.IsAnyRoleAllowedForGrantsAsync(
                currentUser.Roles,
                function.Roles,
                instance,
                new AuthorizationRequestContext(headers, queryParameters),
                cancellationToken);
            if (!allowed)
                return Result<FunctionResponseOutput>.Fail(WorkflowErrors.FunctionAccessDenied(function.Key));
        }

        object scriptBody = body.HasValue
            ? (object)body.Value
            : new JsonData("{}");

        var scriptContext = await scriptContextFactory.NewBuilder(instanceRepository)
            .WithWorkflow(workflow)
            .WithInstance(instance)
            .WithRuntime(runtimeInfoProvider)
            .WithBody(scriptBody)
            .WithHeaders(headers)
            .WithQueryParameters(queryParameters)
            .BuildAsync(cancellationToken);

        var executeResult = await taskCoordinator.ExecuteAsync(
            function.GetExecuteTasks(),
            null,
            TaskTrigger.Extension,
            scriptContext,
            cancellationToken);

        if (!executeResult.IsSuccess)
            return Result<FunctionResponseOutput>.Fail(executeResult.Error);

        return await BuildResponseAsync(function, scriptContext, cancellationToken);
    }

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
            var handler = await scriptEngine.CompileToInstanceAsync<IOutputHandler>(
                function.Output.DecodedCode, cancellationToken: cancellationToken);
            var scriptResponse = await handler.OutputHandler(scriptContext);

            if (function.RawResponse)
                return Result<FunctionResponseOutput>.Ok(CreateRawResponse(
                    function,
                    scriptContext,
                    scriptResponse.Data));

            return Result<FunctionResponseOutput>.Ok(new FunctionResponseOutput
            {
                Data = new Dictionary<string, dynamic?> { [function.Key.ToVariableName()] = scriptResponse.Data }
            });
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
        object? data)
    {
        var (statusCode, headers) = ExtractSingleTaskHttpMetadata(function, scriptContext);

        return new FunctionResponseOutput
        {
            Data = data,
            StatusCode = statusCode,
            Headers = headers
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
            try
            {
                return value switch
                {
                    Dictionary<string, string> typedHeaders => typedHeaders,
                    IDictionary<string, object?> objectHeaders => objectHeaders
                        .Where(header => header.Value != null)
                        .ToDictionary(header => header.Key, header => Convert.ToString(header.Value, CultureInfo.InvariantCulture) ?? string.Empty),
                    _ => JsonSerializer.Deserialize<Dictionary<string, string>>(JsonSerializer.Serialize(value))
                };
            }
            catch
            {
                return null;
            }
        }

        return null;
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
