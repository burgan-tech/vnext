using System.Text.Json;
using BBT.Aether;
using BBT.Workflow;
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
using BBT.Workflow.Tasks.Mapping;
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
    private readonly IUrlTemplateBuilder _urlTemplateBuilder;

    /// <summary>
    /// Initializes a new instance of GetInstancesTaskExecutor.
    /// </summary>
    public GetInstancesTaskExecutor(
        IScriptEngine scriptEngine,
        IRuntimeInfoProvider runtimeInfoProvider,
        IRemoteInvokerService remoteInvoker,
        IInstanceQueryGateway instanceQueryGateway,
        IDomainDiscoveryResolver endpointResolver,
        IUrlTemplateBuilder urlTemplateBuilder,
        ILogger<GetInstancesTaskExecutor> logger)
        : base(scriptEngine, runtimeInfoProvider, remoteInvoker, logger)
    {
        _instanceQueryGateway = instanceQueryGateway;
        _endpointResolver = endpointResolver;
        _urlTemplateBuilder = urlTemplateBuilder;
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
            var headers = ConvertTaskHeadersToDictionary(task.Headers);

            // Step 1: Get list of instances
            var listInput = new GetInstanceListInput
            {
                Domain = task.TriggerDomain,
                Workflow = task.TriggerFlow,
                Page = task.Page,
                PageSize = task.PageSize,
                Sort = task.Sort,
                Filter = task.Filter,
                Headers = headers ?? new Dictionary<string, string?>()
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

            var listResponse = instanceListResult.Value!;
            var basePath = _urlTemplateBuilder.BuildFunctionListUrl(
                task.TriggerDomain,
                task.TriggerFlow,
                "data");

            var groupItems = TryMaterializeGroupSummaries(listResponse);
            if (groupItems != null)
            {
                // groupBy: items are GroupSummary — do not call GetInstanceData per row
                var currentPage = task.Page;
                var pageSize = task.PageSize;
                var totalEstimate = groupItems.Count;
                var hasNext = (currentPage * pageSize) < totalEstimate;

                var groupedResponseData = new
                {
                    links = new
                    {
                        self = $"{basePath}?page={currentPage}&pageSize={pageSize}",
                        first = $"{basePath}?page=1&pageSize={pageSize}",
                        next = hasNext
                            ? $"{basePath}?page={currentPage + 1}&pageSize={pageSize}"
                            : "",
                        prev = currentPage > 1
                            ? $"{basePath}?page={currentPage - 1}&pageSize={pageSize}"
                            : ""
                    },
                    items = groupItems
                };

                return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Success(
                    data: groupedResponseData,
                    statusCode: 200,
                    taskType: TaskType.ToString(),
                    metadata: new Dictionary<string, object>
                    {
                        ["Page"] = currentPage,
                        ["PageSize"] = pageSize,
                        ["HasNext"] = hasNext,
                        ["ItemCount"] = groupItems.Count,
                        ["Grouped"] = true
                    }));
            }

            // Step 2: Get data for each instance (flat list)
            // Note: ToPagedList uses Items.Count as totalItems estimate since InstanceListWithGroupsResponse doesn't preserve total count
            var pagedList = listResponse.ToPagedList(task.PageSize, task.Page, listResponse.Items.Count);
            var instanceDataResults = await ProcessDataFunctionListAsync(
                task.TriggerDomain,
                task.TriggerFlow,
                pagedList,
                cancellationToken);

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

    /// <summary>
    /// When the list API returns groupBy results, <see cref="InstanceListWithGroupsResponse{T}.Items"/> holds
    /// <see cref="GroupSummary"/> (or JSON elements after deserialization). Returns null if the payload is not
    /// a homogeneous group list so the flat instance path can run.
    /// </summary>
    private static List<GroupSummary>? TryMaterializeGroupSummaries(
        InstanceListWithGroupsResponse<GetInstanceOutput> listResponse)
    {
        if (listResponse.Items.Count == 0)
        {
            return null;
        }

        var groups = new List<GroupSummary>(listResponse.Items.Count);
        foreach (var item in listResponse.Items)
        {
            switch (item)
            {
                case GroupSummary groupSummary:
                    groups.Add(groupSummary);
                    break;
                case JsonElement jsonElement:
                    var deserialized = JsonSerializer.Deserialize<GroupSummary>(
                        jsonElement,
                        JsonSerializerConstants.JsonOptions);
                    if (deserialized == null)
                    {
                        return null;
                    }

                    groups.Add(deserialized);
                    break;
                default:
                    return null;
            }
        }

        return groups;
    }

    private async Task<Result<TaskInvocationResult>> ExecuteRemoteAsync(
        GetInstancesTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        Logger.LogDebug("Using RemoteInvokerService for GetInstances task {TaskKey}", task.Key);

        // Create envelope from task using TaskBindingMapper
        var envelopeResult = TaskBindingMapper.CreateEnvelope(task);
        if (!envelopeResult.IsSuccess)
        {
            Logger.TaskEnvelopeCreationFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance.Id,
                envelopeResult.Error.Message ?? "Failed to create envelope");
            return Result<TaskInvocationResult>.Fail(envelopeResult.Error);
        }

        // Enrich binding with runtime context (endpoint, headers)
        var enrichResult = await EnrichBindingAsync<GetInstancesBinding>(
            envelopeResult.Value!,
            task.TriggerDomain,
            task.UseDapr,
            context,
            cancellationToken);

        if (!enrichResult.IsSuccess)
        {
            Logger.TaskRemoteExecutionFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance.Id,
                enrichResult.Error.Message ?? "Failed to resolve endpoint");
            return Result<TaskInvocationResult>.Fail(enrichResult.Error);
        }

        var traceContext = RemoteInvoker.CreateTraceContext(context.ScriptContext);
        var result = await RemoteInvoker.InvokeAsync(
            TaskTypes.GetInstances,
            task.Key,
            enrichResult.Value!,
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

    private async Task<Result<TaskEnvelope>> EnrichBindingAsync<TBinding>(
        TaskEnvelope envelope,
        string targetDomain,
        bool useDapr,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        // Deserialize binding
        var binding = envelope.Binding.Deserialize<GetInstancesBinding>();
        if (binding == null)
        {
            return Result<TaskEnvelope>.Fail(Error.Failure(
                WorkflowErrorCodes.TaskBindingMappingFailed,
                "Failed to deserialize binding"));
        }

        // Resolve endpoint
        var preferredKind = useDapr ? EndpointKind.Dapr : EndpointKind.Url;
        var endpointResult = await _endpointResolver.GetEndpointAsync(
            targetDomain, preferredKind, cancellationToken);

        if (!endpointResult.IsSuccess)
        {
            return Result<TaskEnvelope>.Fail(endpointResult.Error);
        }

        var endpoint = endpointResult.Value!;

        // Create enriched binding with runtime context
        var enrichedBinding = new GetInstancesBinding
        {
            Domain = binding.Domain,
            Workflow = binding.Workflow,
            Page = binding.Page,
            PageSize = binding.PageSize,
            Sort = binding.Sort,
            Filter = binding.Filter,
            UseDapr = binding.UseDapr,
            ValidateSSL = binding.ValidateSSL,
            TimeoutSeconds = binding.TimeoutSeconds,
            Headers = binding.Headers,
            BaseUrl = endpoint.BaseUrl.ToString(),
            DaprAppId = endpoint.DaprAppId
        };

        return Result.Ok(new TaskEnvelope
        {
            TaskType = envelope.TaskType,
            TaskKey = envelope.TaskKey,
            Binding = JsonSerializer.SerializeToElement(enrichedBinding)
        });
    }
}
