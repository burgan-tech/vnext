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
using BBT.Workflow.Tasks;
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
    ITaskCoordinator taskCoordinator) : ApplicationService(serviceProvider), IFunctionAppService
{
    /// <inheritdoc />
    public async Task<Result<Dictionary<string, dynamic?>>> GetFunctionByFunctionKeyAsync(
        string key,
        string flow,
        string domain,
        Dictionary<string, string?>? headers = null,
        Dictionary<string, string?>? queryParameters = null,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(domain);
        currentSchema.Use(flow);
        Instance? instance = await instanceRepository.FindByIdentifierAsync(key, cancellationToken);
        return await BuildFunctionResponseAsync(instance, key, flow, domain, headers, queryParameters, cancellationToken);
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
        var instance = await instanceRepository.FindByIdentifierAsync(instanceKey, cancellationToken);
        return await BuildFunctionResponseAsync(instance, key, flow, domain, headers, queryParameters,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<List<InstanceAndDataModel>>> GetDomainFunctionsAsync(
        string domain,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(domain);
        var result = await instanceRepository.GetActiveDataListAsync(cancellationToken);
        return Result<List<InstanceAndDataModel>>.Ok(result);
    }

    /// <summary>
    /// Builds the function response using Railway pattern.
    /// Chain: Load Function → Load Workflow → Execute Tasks → Extract Response
    /// </summary>
    private Task<Result<Dictionary<string, dynamic?>>> BuildFunctionResponseAsync(
        Instance? instance,
        string functionKey,
        string flow,
        string domain,
        Dictionary<string, string?>? headers = null,
        Dictionary<string, string?>? queryParameters = null,
        CancellationToken cancellationToken = default)
    {
        return componentCacheStore
            .GetFunctionAsync(domain, functionKey, string.Empty, cancellationToken)
            .BindAsync(function =>
                componentCacheStore.GetFlowAsync(domain, flow, null, cancellationToken)
                    .MapAsync(workflow => (function, workflow)))
            .BindAsync(data => ExecuteFunctionAsync(data.function, data.workflow, instance, headers, queryParameters,
                cancellationToken));
    }

    /// <summary>
    /// Executes the function tasks and extracts the response.
    /// Uses Railway pattern to propagate task execution errors.
    /// </summary>
    private async Task<Result<Dictionary<string, dynamic?>>> ExecuteFunctionAsync(
        Function function,
        Definitions.Workflow workflow,
        Instance? instance,
        Dictionary<string, string?>? headers = null,
        Dictionary<string, string?>? queryParameters = null,
        CancellationToken cancellationToken = default)
    {
        if (workflow.Key != RuntimeSysSchemaInfo.Functions &&function.Scope!=TaskScope.Domain&&
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

        // Execute tasks and propagate errors
        var executeResult = await taskCoordinator.ExecuteAsync(
            function.GetExecuteTasks(),
            null,
            TaskTrigger.Extension,
            scriptContext,
            cancellationToken);

        if (!executeResult.IsSuccess)
        {
            return Result<Dictionary<string, dynamic?>>.Fail(executeResult.Error);
        }

        var response = ExtractFunctionResponse(function, scriptContext);
        return Result<Dictionary<string, dynamic?>>.Ok(response);
    }

    /// <summary>
    /// Extracts the function response from script context.
    /// </summary>
    private static Dictionary<string, dynamic?> ExtractFunctionResponse(
        Function function,
        ScriptContext scriptContext)
    {
        var response = new Dictionary<string, dynamic?>();
        var variableKeyFunction = function.Key.ToVariableName();
        var variableKeyTask = function.Task.Task.Key.ToVariableName();

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
