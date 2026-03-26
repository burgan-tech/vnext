using System.Text;
using System.Text.Json;
using BBT.Aether.Application.Services;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
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
    IScriptEngine scriptEngine) : ApplicationService(serviceProvider), IFunctionAppService
{
    /// <inheritdoc />
    public async Task<Result<Dictionary<string, dynamic?>>> GetFunctionByKeyAsync(
        string key,
        string domain,
        string? version = null,
        Dictionary<string, string?>? headers = null,
        Dictionary<string, string?>? queryParameters = null,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(domain);
        using (currentSchema.Use(RuntimeSysSchemaInfo.Functions))
        {
            return await componentCacheStore
                .GetFunctionAsync(domain, key, version, cancellationToken)
                .BindAsync(function =>
                    ExecuteFunctionAsync(function, null, null, headers, queryParameters, cancellationToken));
        }
    }

    /// <inheritdoc />
    public async Task<Result<Dictionary<string, dynamic?>>> GetFunctionByInstanceAsync(
        string key,
        string flow,
        string domain,
        string instanceKey,
        Dictionary<string, string?>? headers = null,
        Dictionary<string, string?>? queryParameters = null,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(domain);
        using (currentSchema.Use(flow))
        {
            var instance = await instanceRepository.FindByIdentifierAsync(instanceKey, cancellationToken);
            if (instance == null)
                return Result<Dictionary<string, dynamic?>>.Fail(WorkflowErrors.InstanceNotFound(instanceKey));

            return await componentCacheStore
                .GetFlowAsync(domain, flow, instance.FlowVersion, cancellationToken)
                .BindAsync(workflow =>
                    ResolveFunctionAndExecuteAsync(domain, key, instance, workflow, headers, queryParameters, cancellationToken));
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
    private Task<Result<Dictionary<string, dynamic?>>> ResolveFunctionAndExecuteAsync(
        string domain,
        string key,
        Instance instance,
        Definitions.Workflow workflow,
        Dictionary<string, string?>? headers,
        Dictionary<string, string?>? queryParameters,
        CancellationToken cancellationToken)
    {
        var functionReference = workflow.FindFunction(key);
        return componentCacheStore
            .GetFunctionAsync(domain, key, functionReference?.Version, cancellationToken)
            .BindAsync(function =>
                ExecuteFunctionAsync(function, instance, workflow, headers, queryParameters, cancellationToken));
    }

    /// <summary>
    /// Builds the script context, executes all function tasks, and extracts the response.
    /// </summary>
    private async Task<Result<Dictionary<string, dynamic?>>> ExecuteFunctionAsync(
        Function function,
        Instance? instance,
        Definitions.Workflow? workflow,
        Dictionary<string, string?>? headers,
        Dictionary<string, string?>? queryParameters,
        CancellationToken cancellationToken)
    {
        if (instance != null &&
            workflow!.Key != RuntimeSysSchemaInfo.Functions &&
            !function.Scope.Equals(TaskScope.Domain) &&
            !workflow.Functions.Any(f => f.Key == function.Key))
        {
            return Result<Dictionary<string, dynamic?>>.Fail(
                WorkflowErrors.FunctionNotInWorkflow(function.Key, workflow.Key));
        }

        var scriptContext = await scriptContextFactory.NewBuilder(instanceRepository)
            .WithWorkflow(workflow)
            .WithInstance(instance)
            .WithRuntime(runtimeInfoProvider)
            .WithBody(instance?.LatestData?.Data ?? new JsonData("{}"))
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
            return Result<Dictionary<string, dynamic?>>.Fail(executeResult.Error);

        return await BuildResponseAsync(function, scriptContext, cancellationToken);
    }

    /// <summary>
    /// Builds the final response: uses the <c>output</c> script when defined, otherwise falls back to
    /// legacy single-task extraction from <see cref="ScriptContext.OutputResponse"/>.
    /// </summary>
    private async Task<Result<Dictionary<string, dynamic?>>> BuildResponseAsync(
        Function function,
        ScriptContext scriptContext,
        CancellationToken cancellationToken)
    {
        if (function.Output != null)
        {
            var handler = await scriptEngine.CompileToInstanceAsync<IOutputHandler>(
                function.Output.DecodedCode, cancellationToken: cancellationToken);
            var scriptResponse = await handler.OutputHandler(scriptContext);
            return Result<Dictionary<string, dynamic?>>.Ok(
                new Dictionary<string, dynamic?> { [function.Key.ToVariableName()] = scriptResponse.Data });
        }

        return Result<Dictionary<string, dynamic?>>.Ok(ExtractFunctionResponse(function, scriptContext));
    }

    /// <summary>
    /// Legacy single-task output extraction from <see cref="ScriptContext.OutputResponse"/>.
    /// Unwraps the inner <c>data</c> property when the value is a JSON element wrapper.
    /// </summary>
    private static Dictionary<string, dynamic?> ExtractFunctionResponse(
        Function function,
        ScriptContext scriptContext)
    {
        var response = new Dictionary<string, dynamic?>();
        var variableKeyFunction = function.Key.ToVariableName();
        var variableKeyTask = function.Task!.Task.Key.ToVariableName();

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
}
