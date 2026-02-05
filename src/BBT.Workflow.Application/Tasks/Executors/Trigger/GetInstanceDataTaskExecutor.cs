using System.Text.Json;
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
/// Executor for GetInstanceData tasks that retrieve instance data from workflow instances.
/// Domain-aware: uses local IInstanceQueryAppService for same domain,
/// RemoteInvokerService for cross-domain execution.
/// </summary>
public sealed class GetInstanceDataTaskExecutor : TriggerTaskExecutorBase<GetInstanceDataTask>
{
    private readonly IInstanceQueryGateway _instanceQueryGateway;
    private readonly IDomainDiscoveryResolver _endpointResolver;

    /// <summary>
    /// Initializes a new instance of GetInstanceDataTaskExecutor.
    /// </summary>
    public GetInstanceDataTaskExecutor(
        IScriptEngine scriptEngine,
        IRuntimeInfoProvider runtimeInfoProvider,
        IRemoteInvokerService remoteInvoker,
        IInstanceQueryGateway instanceQueryGateway,
        IDomainDiscoveryResolver endpointResolver,
        ILogger<GetInstanceDataTaskExecutor> logger)
        : base(scriptEngine, runtimeInfoProvider, remoteInvoker, logger)
    {
        _instanceQueryGateway = instanceQueryGateway;
        _endpointResolver = endpointResolver;
    }

    /// <inheritdoc />
    public override TaskType TaskType => TaskType.GetInstanceData;

    /// <inheritdoc />
    protected override string GetTargetDomain(GetInstanceDataTask task) => task.TriggerDomain;

    /// <inheritdoc />
    protected override async Task<Result<TaskInvocationResult>> InvokeAsync(
        GetInstanceDataTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        // Resolve instance identifier
        var instanceIdResult = ResolveInstanceIdentifier(task);
        if (!instanceIdResult.IsSuccess)
        {
            Logger.TaskInstanceResolutionFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance.Id,
                instanceIdResult.Error.Message ?? "Unknown error");
            return Result<TaskInvocationResult>.Fail(instanceIdResult.Error);
        }

        var instanceIdentifier = instanceIdResult.Value!;
        var isSameDomain = IsSameDomain(task);

        Logger.LogDebug(
            "GetInstanceData task {TaskKey} targeting domain {TargetDomain}, instance {InstanceId}, same domain: {IsSameDomain}",
            task.Key, task.TriggerDomain, instanceIdentifier, isSameDomain);

        if (isSameDomain)
        {
            return await ExecuteLocalAsync(task, instanceIdentifier, context, cancellationToken);
        }

        return await ExecuteRemoteAsync(task, instanceIdentifier, context, cancellationToken);
    }

    private Result<string> ResolveInstanceIdentifier(GetInstanceDataTask task)
    {
        // Priority: TriggerInstanceId > TriggerKey > Error
        if (!string.IsNullOrEmpty(task.Identifier))
        {
            return Result<string>.Ok(task.Identifier);
        }

        return Result<string>.Fail(Error.Validation(
            WorkflowErrorCodes.TaskExecution,
            "GetInstanceData task requires either instanceId or key to be specified"));
    }

    private async Task<Result<TaskInvocationResult>> ExecuteLocalAsync(
        GetInstanceDataTask task,
        string instanceIdentifier,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        Logger.LogDebug("Using local IInstanceQueryGateway for GetInstanceData task {TaskKey}", task.Key);

        try
        {
            var headers = ConvertTaskHeadersToDictionary(task.Headers);

            var input = new GetInstanceDataInput
            {
                Domain = task.TriggerDomain,
                Workflow = task.TriggerFlow,
                Instance = instanceIdentifier,
                Extensions = task.Extensions,
                Headers = headers
            };

            var result = await _instanceQueryGateway.GetInstanceDataAsync(input, cancellationToken);
            if (!result.Result.IsSuccess)
            {
                Logger.TaskLocalExecutionFailed(
                    task.Key,
                    TaskType.ToString(),
                    instanceIdentifier,
                    result.Result.Error.Message ?? "GetInstanceData failed");
                return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Failure(
                    error: result.Result.Error.Message ?? "GetInstanceData failed",
                    statusCode: 500,
                    taskType: TaskType.ToString()));
            }

            // Handle not modified (304) scenario
            if (result.IsNotModified)
            {
                return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Success(
                    data: null,
                    statusCode: 304,
                    taskType: TaskType.ToString(),
                    metadata: new Dictionary<string, object>
                    {
                        ["NotModified"] = true
                    }));
            }

            return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Success(
                data: result.Result.Value,
                statusCode: 200,
                taskType: TaskType.ToString()));
        }
        catch (Exception ex)
        {
            Logger.TaskLocalExecutionFailed(
                task.Key,
                TaskType.ToString(),
                instanceIdentifier,
                ex.Message);

            return Result<TaskInvocationResult>.Fail(
                Error.Failure(
                    WorkflowErrorCodes.TaskExecution,
                    $"GetInstanceData execution failed: {ex.Message}",
                    detail: ex.GetType().Name));
        }
    }

    private async Task<Result<TaskInvocationResult>> ExecuteRemoteAsync(
        GetInstanceDataTask task,
        string instanceIdentifier,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        Logger.LogDebug("Using RemoteInvokerService for GetInstanceData task {TaskKey}", task.Key);

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

        // Enrich binding with runtime context (endpoint, headers, resolved instance)
        var enrichResult = await EnrichBindingAsync(
            envelopeResult.Value!,
            task.TriggerDomain,
            task.UseDapr,
            instanceIdentifier,
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
            TaskTypes.GetInstanceData,
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

    private async Task<Result<TaskEnvelope>> EnrichBindingAsync(
        TaskEnvelope envelope,
        string targetDomain,
        bool useDapr,
        string instanceIdentifier,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        // Deserialize binding
        var binding = envelope.Binding.Deserialize<GetInstanceDataBinding>();
        if (binding == null)
        {
            return Result<TaskEnvelope>.Fail(Error.Failure(
                WorkflowErrorCodes.TaskBindingMappingFailed,
                "Failed to deserialize GetInstanceDataBinding"));
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
        var enrichedBinding = new GetInstanceDataBinding
        {
            Domain = binding.Domain,
            Workflow = binding.Workflow,
            Instance = instanceIdentifier,
            Extensions = binding.Extensions,
            ETag = binding.ETag,
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