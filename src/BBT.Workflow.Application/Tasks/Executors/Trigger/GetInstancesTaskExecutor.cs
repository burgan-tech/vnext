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
            var itemCount = listResponse.Items.Count;
            var hasNext = itemCount == task.PageSize;
            var isGrouped = listResponse.Items.FirstOrDefault() is GroupSummary;

            var metadata = new Dictionary<string, object>
            {
                ["Page"] = task.Page,
                ["PageSize"] = task.PageSize,
                ["HasNext"] = hasNext,
                ["ItemCount"] = itemCount,
            };

            if (isGrouped)
            {
                metadata["Grouped"] = true;
            }

            return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Success(
                data: listResponse,
                statusCode: 200,
                taskType: TaskType.ToString(),
                metadata: metadata));
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
                context.ScriptContext?.Instance?.Id ?? Guid.Empty,
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
                context.ScriptContext?.Instance?.Id ?? Guid.Empty,
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
                context.ScriptContext?.Instance?.Id ?? Guid.Empty,
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