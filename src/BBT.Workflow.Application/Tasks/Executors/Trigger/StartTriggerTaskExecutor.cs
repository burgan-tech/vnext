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
/// Executor for StartTrigger tasks that start new workflow instances.
/// Domain-aware: uses local IInstanceCommandAppService for same domain,
/// RemoteInvokerService for cross-domain execution.
/// </summary>
public sealed class StartTriggerTaskExecutor : TriggerTaskExecutorBase<StartTask>
{
    private readonly IInstanceCommandGateway _instanceCommandGateway;
    private readonly IDomainDiscoveryResolver _endpointResolver;

    /// <summary>
    /// Initializes a new instance of StartTriggerTaskExecutor.
    /// </summary>
    public StartTriggerTaskExecutor(
        IScriptEngine scriptEngine,
        IRuntimeInfoProvider runtimeInfoProvider,
        IRemoteInvokerService remoteInvoker,
        IInstanceCommandGateway instanceCommandGateway,
        IDomainDiscoveryResolver endpointResolver,
        ILogger<StartTriggerTaskExecutor> logger)
        : base(scriptEngine, runtimeInfoProvider, remoteInvoker, logger)
    {
        _instanceCommandGateway = instanceCommandGateway;
        _endpointResolver = endpointResolver;
    }

    /// <inheritdoc />
    public override TaskType TaskType => TaskType.StartTrigger;

    /// <inheritdoc />
    protected override string GetTargetDomain(StartTask task) => task.TriggerDomain;

    /// <inheritdoc />
    protected override async Task<Result<TaskInvocationResult>> InvokeAsync(
        StartTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var isSameDomain = IsSameDomain(task);

        Logger.LogDebug("StartTrigger task {TaskKey} targeting domain {TargetDomain}, same domain: {IsSameDomain}",
            task.Key, task.TriggerDomain, isSameDomain);

        if (isSameDomain)
        {
            return await ExecuteLocalAsync(task, context, cancellationToken);
        }

        return await ExecuteRemoteAsync(task, context, cancellationToken);
    }

    private async Task<Result<TaskInvocationResult>> ExecuteLocalAsync(
        StartTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        Logger.LogDebug("Using local IInstanceCommandGateway for StartTrigger task {TaskKey}", task.Key);

        try
        {
            var input = BuildStartInstanceInput(task, context);
            var result = await _instanceCommandGateway.StartAsync(input, cancellationToken);

            if (!result.IsSuccess)
            {
                Logger.TaskLocalExecutionFailed(
                    task.Key,
                    TaskType.ToString(),
                    context.ScriptContext?.Instance?.Id.ToString() ?? string.Empty,
                    result.Error.Message ?? "StartTrigger failed");
                return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Failure(
                    error: result.Error.Message ?? "StartTrigger failed",
                    statusCode: MapErrorToStatusCode(result.Error),
                    taskType: TaskType.ToString()));
            }

            return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Success(
                data: new
                {
                    result.Value!.Id,
                    result.Value.Status
                },
                statusCode: 200,
                taskType: TaskType.ToString()));
        }
        catch (Exception ex)
        {
            Logger.TaskLocalExecutionFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext?.Instance?.Id.ToString() ?? string.Empty,
                ex.Message);

            return Result<TaskInvocationResult>.Fail(
                Error.Failure(
                    WorkflowErrorCodes.TaskExecution,
                    $"StartTrigger execution failed: {ex.Message}",
                    detail: ex.GetType().Name));
        }
    }

    private async Task<Result<TaskInvocationResult>> ExecuteRemoteAsync(
        StartTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        Logger.LogDebug("Using RemoteInvokerService for StartTrigger task {TaskKey}", task.Key);

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
        var enrichResult = await EnrichBindingAsync<StartTriggerBinding>(
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
            TaskTypes.StartTrigger,
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
        var binding = envelope.Binding.Deserialize<StartTriggerBinding>();
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
        var enrichedBinding = new StartTriggerBinding
        {
            Domain = binding.Domain,
            Workflow = binding.Workflow,
            Version = binding.Version,
            Key = binding.Key,
            Body = binding.Body,
            Tags = binding.Tags,
            Sync = binding.Sync,
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

    private StartInstanceInput BuildStartInstanceInput(StartTask task, TaskExecutorContext context)
    {
        var headers = ConvertTaskHeadersToDictionary(task.Headers);

        return new StartInstanceInput(
            domain: task.TriggerDomain,
            workflow: task.TriggerFlow,
            version: task.TriggerVersion,
            sync: task.TriggerSync)
        {
            Instance = new CreateInstanceInput
            {
                Key = task.TriggerKey,
                Attributes = task.Body,
                Tags = task.TriggerTags
            },
            Headers = headers ?? new Dictionary<string, string?>()
        };
    }
}