using System.Text.Json;
using BBT.Aether;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Discovery;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Executor for GetInstances tasks that retrieve a list of instance data from a workflow.
/// Domain-aware: uses local IInstanceQueryAppService for same domain,
/// RemoteInvokerService for cross-domain execution.
/// </summary>
public sealed class GetInstancesTaskExecutor : TriggerTaskExecutorBase<GetInstancesTask>
{
    private readonly IInstanceQueryGateway _instanceQueryGateway;
    private readonly IDomainDiscoveryResolver _endpointResolver;

    /// <summary>
    /// Initializes a new instance of GetInstancesTaskExecutor.
    /// </summary>
    public GetInstancesTaskExecutor(
        IScriptEngine scriptEngine,
        IRuntimeInfoProvider runtimeInfoProvider,
        IRemoteInvokerService remoteInvoker,
        IInstanceQueryGateway instanceQueryGateway,
        IDomainDiscoveryResolver endpointResolver,
        ILogger<GetInstancesTaskExecutor> logger)
        : base(scriptEngine, runtimeInfoProvider, remoteInvoker, logger)
    {
        _instanceQueryGateway = instanceQueryGateway;
        _endpointResolver = endpointResolver;
    }

    /// <inheritdoc />
    public override TaskType TaskType => TaskType.GetInstances;

    /// <inheritdoc />
    protected override string GetTargetDomain(GetInstancesTask task) => task.TriggerDomain;

    /// <inheritdoc />
    protected override async Task<Result<TaskInvocationResult>> InvokeAsync(
        GetInstancesTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var isSameDomain = IsSameDomain(task);

        Logger.LogDebug(
            "GetInstances task {TaskKey} targeting domain {TargetDomain}, workflow {Workflow}, same domain: {IsSameDomain}",
            task.Key, task.TriggerDomain, task.TriggerFlow, isSameDomain);

        if (isSameDomain)
        {
            return await ExecuteLocalAsync(task, context, cancellationToken);
        }

        return await ExecuteRemoteAsync(task, context, cancellationToken);
    }

    private async Task<Result<TaskInvocationResult>> ExecuteLocalAsync(
        GetInstancesTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        Logger.LogDebug("Using local IInstanceQueryGateway for GetInstances task {TaskKey}", task.Key);

        try
        {
            // Step 1: Get list of instances
            var listInput = new GetInstanceListInput
            {
                Domain = task.TriggerDomain,
                Workflow = task.TriggerFlow,
                Page = task.Page,
                PageSize = task.PageSize,
                Sort = task.Sort,
                Filter = task.Filter
            };

            var instanceListResult = await _instanceQueryGateway.GetInstanceListAsync(listInput, cancellationToken);
            if (!instanceListResult.IsSuccess)
            {
                Logger.TaskLocalExecutionFailed(
                    task.Key,
                    TaskType.ToString(),
                    task.TriggerFlow,
                    instanceListResult.Error.Message ?? "GetInstanceList failed");

                return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Failure(
                    error: instanceListResult.Error.Message ?? "GetInstanceList failed",
                    statusCode: 500,
                    taskType: TaskType.ToString()));
            }

            // Step 2: Get data for each instance
            // Note: ToPagedList uses Items.Count as totalItems estimate since InstanceListWithGroupsResponse doesn't preserve total count
            var pagedList = instanceListResult.Value!.ToPagedList(task.PageSize, task.Page, instanceListResult.Value.Items.Count);
            var instanceDataResults = await ProcessDataFunctionListAsync(
                task.TriggerDomain,
                task.TriggerFlow,
                pagedList,
                cancellationToken);

            // Build HATEOAS-like response structure for consistency with remote execution
            var basePath = $"/api/v1/{task.TriggerDomain}/workflows/{task.TriggerFlow}/functions/data";
            var responseData = new
            {
                links = new
                {
                    self = $"{basePath}?page={pagedList.CurrentPage}&pageSize={pagedList.PageSize}",
                    first = $"{basePath}?page=1&pageSize={pagedList.PageSize}",
                    next = pagedList.HasNext 
                        ? $"{basePath}?page={pagedList.CurrentPage + 1}&pageSize={pagedList.PageSize}" 
                        : "",
                    prev = pagedList.CurrentPage > 1 
                        ? $"{basePath}?page={pagedList.CurrentPage - 1}&pageSize={pagedList.PageSize}" 
                        : ""
                },
                items = instanceDataResults
            };
            
            return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Success(
                data: responseData,
                statusCode: 200,
                taskType: TaskType.ToString(),
                metadata: new Dictionary<string, object>
                {
                    ["Page"] = pagedList.CurrentPage,
                    ["PageSize"] = pagedList.PageSize,
                    ["HasNext"] = pagedList.HasNext,
                    ["ItemCount"] = instanceDataResults.Count
                }));
        }
        catch (Exception ex)
        {
            Logger.TaskLocalExecutionFailed(
                task.Key,
                TaskType.ToString(),
                task.TriggerFlow,
                ex.Message);

            return Result<TaskInvocationResult>.Fail(
                Error.Failure(
                    WorkflowErrorCodes.TaskExecution,
                    $"GetInstances execution failed: {ex.Message}",
                    detail: ex.GetType().Name));
        }
    }

    private async Task<List<GetInstanceDataOutput>> ProcessDataFunctionListAsync(
        string domain,
        string workflow,
        HateoasPagedList<GetInstanceOutput> instanceListResult,
        CancellationToken cancellationToken)
    {
        // Process sequentially to avoid flooding downstream dependencies
        var results = new List<GetInstanceDataOutput>();
        
        foreach (var instance in instanceListResult.Items)
        {
            var input = new GetInstanceDataInput
            {
                Domain = domain,
                Workflow = workflow,
                Instance = instance.Key!
            };
            
            var result = await _instanceQueryGateway.GetInstanceDataAsync(input, cancellationToken);
            if (result.Result is { IsSuccess: true, Value: not null })
            {
                results.Add(result.Result.Value);
            }
        }
        
        return results;
    }

    private async Task<Result<TaskInvocationResult>> ExecuteRemoteAsync(
        GetInstancesTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        Logger.LogDebug("Using RemoteInvokerService for GetInstances task {TaskKey}", task.Key);

        var bindingResult = await BuildGetInstancesBindingAsync(task, cancellationToken);
        
        if (!bindingResult.IsSuccess)
        {
            Logger.TaskRemoteExecutionFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance.Id,
                bindingResult.Error.Message ?? "Failed to resolve endpoint");
            return Result<TaskInvocationResult>.Fail(bindingResult.Error);
        }

        var binding = bindingResult.Value!;
        var envelope = new TaskEnvelope
        {
            TaskType = TaskTypes.GetInstances,
            TaskKey = task.Key,
            Binding = JsonSerializer.SerializeToElement(binding)
        };

        var traceContext = RemoteInvoker.CreateTraceContext(context.ScriptContext);
        var result = await RemoteInvoker.InvokeAsync(
            TaskTypes.GetInstances,
            task.Key,
            envelope,
            traceContext,
            cancellationToken);

        if (!result.IsSuccess)
        {
            Logger.TaskRemoteExecutionFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance.Id,
                result.Error.Message ?? "Unknown error");
        }

        return result;
    }

    private async Task<Result<GetInstancesBinding>> BuildGetInstancesBindingAsync(
        GetInstancesTask task,
        CancellationToken cancellationToken)
    {
        var preferredKind = task.UseDapr ? EndpointKind.Dapr : EndpointKind.Url;
        var endpointResult = await _endpointResolver.GetEndpointAsync(
            task.TriggerDomain, preferredKind, cancellationToken);

        if (!endpointResult.IsSuccess)
        {
            return Result<GetInstancesBinding>.Fail(endpointResult.Error);
        }

        var endpoint = endpointResult.Value!;

        return Result.Ok(new GetInstancesBinding
        {
            Domain = task.TriggerDomain,
            Workflow = task.TriggerFlow,
            Page = task.Page,
            PageSize = task.PageSize,
            Sort = task.Sort,
            Filter = task.Filter,
            UseDapr = task.UseDapr,
            ValidateSSL = task.ValidateSSL,
            BaseUrl = endpoint.BaseUrl.ToString(),
            DaprAppId = endpoint.DaprAppId
        });
    }
}
