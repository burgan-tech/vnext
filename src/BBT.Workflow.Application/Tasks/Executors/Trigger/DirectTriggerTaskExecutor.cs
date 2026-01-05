using System.Text.Json;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Discovery;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Resilience;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Executor for DirectTrigger tasks that trigger transitions on existing workflow instances.
/// Domain-aware: uses local IInstanceCommandAppService for same domain,
/// RemoteInvokerService for cross-domain execution.
/// Implements retry logic for transient failures (e.g., instance lock scenarios).
/// </summary>
public sealed class DirectTriggerTaskExecutor : TriggerTaskExecutorBase<DirectTriggerTask>
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IDomainDiscoveryResolver _endpointResolver;
    private readonly ResiliencePipeline<Result<TransitionOutput>> _retryPipeline;

    /// <summary>
    /// Initializes a new instance of DirectTriggerTaskExecutor.
    /// </summary>
    public DirectTriggerTaskExecutor(
        IServiceScopeFactory scopeFactory,
        IScriptEngine scriptEngine,
        IRuntimeInfoProvider runtimeInfoProvider,
        IRemoteInvokerService remoteInvoker,
        IDomainDiscoveryResolver endpointResolver,
        IResultResiliencePipelineFactory resilienceFactory,
        ILogger<DirectTriggerTaskExecutor> logger)
        : base(scriptEngine, runtimeInfoProvider, remoteInvoker, logger)
    {
        _serviceScopeFactory = scopeFactory;
        _endpointResolver = endpointResolver;
        _retryPipeline = resilienceFactory.CreatePipeline<TransitionOutput>(
            operationName: "DirectTrigger.ExecuteLocal");
    }

    /// <inheritdoc />
    public override TaskType TaskType => TaskType.DirectTrigger;

    /// <inheritdoc />
    protected override string GetTargetDomain(DirectTriggerTask task) => task.TriggerDomain;

    /// <inheritdoc />
    protected override async Task<Result<TaskInvocationResult>> InvokeAsync(
        DirectTriggerTask task,
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
            "DirectTrigger task {TaskKey} targeting domain {TargetDomain}, instance {InstanceId}, same domain: {IsSameDomain}",
            task.Key, task.TriggerDomain, instanceIdentifier, isSameDomain);

        if (isSameDomain)
        {
            return await ExecuteLocalAsync(task, instanceIdentifier, context, cancellationToken);
        }

        return await ExecuteRemoteAsync(task, context, cancellationToken);
    }

    private Result<string> ResolveInstanceIdentifier(DirectTriggerTask task)
    {
        // Priority: TriggerInstanceId > TriggerKey > Error
        if (!string.IsNullOrEmpty(task.Identifier))
        {
            return Result<string>.Ok(task.Identifier);
        }

        return Result<string>.Fail(Error.Validation(
            WorkflowErrorCodes.TaskExecution,
            "DirectTrigger task requires either instanceId or key to be specified"));
    }

    private async Task<Result<TaskInvocationResult>> ExecuteLocalAsync(
        DirectTriggerTask task,
        string instanceIdentifier,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        Logger.LogDebug("Using local IInstanceCommandAppService for DirectTrigger task {TaskKey}", task.Key);

        var headers = ExtractHeaders(context.ScriptContext);

        var transitionData = task.Body.HasValue
            ? new TransitionDataInput(task.Body)
            {
                Key = task.TriggerKey,
                Tags = task.TriggerTags
            }
            : null;

        var input = new TransitionInput(
            domain: task.TriggerDomain,
            workflow: task.TriggerFlow,
            version: task.TriggerVersion,
            data: transitionData,
            sync: task.TriggerSync)
        {
            Headers = headers ?? new Dictionary<string, string?>()
        };

        // Execute with retry pipeline for transient failures (e.g., instance lock scenarios)
        var result = await _retryPipeline.ExecuteAsync(
            async token => await ExecuteTransitionWithUowAsync(task, instanceIdentifier, input, token),
            cancellationToken);

        if (!result.IsSuccess)
        {
            Logger.TaskLocalExecutionFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance.Id.ToString(),
                result.Error.Message ?? "DirectTrigger transition failed");

            return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Failure(
                error: result.Error.Message ?? "DirectTrigger transition failed",
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

    /// <summary>
    /// Executes the transition within a Unit of Work scope.
    /// Each retry attempt gets a fresh UoW to ensure clean transaction state.
    /// </summary>
    private async Task<Result<TransitionOutput>> ExecuteTransitionWithUowAsync(
        DirectTriggerTask task,
        string instanceIdentifier,
        TransitionInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _serviceScopeFactory.CreateAsyncScope();
            var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
            var localCommandService = scope.ServiceProvider.GetRequiredService<IInstanceCommandAppService>();
            using (currentSchema.Use(input.Workflow))
            {
                var result = await localCommandService.TransitionAsync(
                    instanceIdentifier,
                    task.TransitionName,
                    input,
                    cancellationToken);

                if (!result.IsSuccess)
                {
                    Logger.TaskLocalExecutionFailed(
                        task.Key,
                        TaskType.ToString(),
                        instanceIdentifier,
                        result.Error.Message ?? "DirectTrigger transition failed");
                    return Result<TransitionOutput>.Fail(result.Error);
                }

                return result;
            }
        }
        catch (Exception ex)
        {
            Logger.TaskLocalExecutionFailed(
                task.Key,
                TaskType.ToString(),
                instanceIdentifier,
                ex.Message);
            
            return Result<TransitionOutput>.Fail(
                Error.Failure(
                    WorkflowErrorCodes.ExecutionStepFailed,
                    $"DirectTrigger transition execution failed: {ex.Message}",
                    detail: ex.GetType().Name));
        }
    }

    private async Task<Result<TaskInvocationResult>> ExecuteRemoteAsync(
        DirectTriggerTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        Logger.LogDebug("Using RemoteInvokerService for DirectTrigger task {TaskKey}", task.Key);

        var binding = await BuildDirectTriggerBindingAsync(task, context, cancellationToken);
        var envelope = new TaskEnvelope
        {
            TaskType = TaskTypes.DirectTrigger,
            TaskKey = task.Key,
            Binding = JsonSerializer.SerializeToElement(binding)
        };

        var traceContext = RemoteInvoker.CreateTraceContext(context.ScriptContext);
        var result = await RemoteInvoker.InvokeAsync(
            TaskTypes.DirectTrigger,
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

    private async Task<DirectTriggerBinding> BuildDirectTriggerBindingAsync(
        DirectTriggerTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var headers = ExtractHeaders(context.ScriptContext);

        var preferredKind = task.UseDapr ? EndpointKind.Dapr : EndpointKind.Url;
        var endpoint = await _endpointResolver.GetEndpointAsync(
            task.TriggerDomain, preferredKind, cancellationToken);

        return new DirectTriggerBinding
        {
            Domain = task.TriggerDomain,
            Workflow = task.TriggerFlow,
            InstanceId = task.TriggerInstanceId,
            Key = task.TriggerKey,
            TransitionName = task.TransitionName,
            Tags = task.TriggerTags,
            Version = task.TriggerVersion,
            Body = task.Body,
            Headers = headers != null ? JsonSerializer.Serialize(headers) : null,
            Sync = task.TriggerSync,
            UseDapr = task.UseDapr,
            BaseUrl = endpoint.BaseUrl.ToString(),
            DaprAppId = endpoint.DaprAppId
        };
    }
}