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
                    context.ScriptContext.Instance.Id.ToString(),
                    result.Error.Message ?? "StartTrigger failed");
                return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Failure(
                    error: result.Error.Message ?? "StartTrigger failed",
                    statusCode: 500,
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
                context.ScriptContext.Instance.Id.ToString(),
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

        var bindingResult = await BuildStartTriggerBindingAsync(task, context, cancellationToken);
        
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
            TaskType = TaskTypes.StartTrigger,
            TaskKey = task.Key,
            Binding = JsonSerializer.SerializeToElement(binding)
        };

        var traceContext = RemoteInvoker.CreateTraceContext(context.ScriptContext);
        var result = await RemoteInvoker.InvokeAsync(
            TaskTypes.StartTrigger,
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

    private StartInstanceInput BuildStartInstanceInput(StartTask task, TaskExecutorContext context)
    {
        var headers = ExtractHeaders(context.ScriptContext);

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

    private async Task<Result<StartTriggerBinding>> BuildStartTriggerBindingAsync(
        StartTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var headers = ExtractHeaders(context.ScriptContext);

        var preferredKind = task.UseDapr ? EndpointKind.Dapr : EndpointKind.Url;
        var endpointResult = await _endpointResolver.GetEndpointAsync(
            task.TriggerDomain, preferredKind, cancellationToken);

        if (!endpointResult.IsSuccess)
        {
            return Result<StartTriggerBinding>.Fail(endpointResult.Error);
        }

        var endpoint = endpointResult.Value!;

        return Result.Ok(new StartTriggerBinding
        {
            Domain = task.TriggerDomain,
            Workflow = task.TriggerFlow,
            Version = task.TriggerVersion,
            Key = task.TriggerKey,
            Tags = task.TriggerTags,
            Body = task.Body,
            Headers = headers != null ? JsonSerializer.Serialize(headers) : null,
            Sync = task.TriggerSync,
            UseDapr = task.UseDapr,
            ValidateSSL = task.ValidateSSL,
            BaseUrl = endpoint.BaseUrl.ToString(),
            DaprAppId = endpoint.DaprAppId
        });
    }
}