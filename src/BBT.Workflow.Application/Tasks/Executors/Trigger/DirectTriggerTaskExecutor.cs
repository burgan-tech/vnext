using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Discovery;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Resilience;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Mapping;
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
    private readonly IInstanceCommandGateway _instanceCommandGateway;
    private readonly IInstanceQueryGateway _instanceQueryGateway;
    private readonly IDomainDiscoveryResolver _endpointResolver;
    private readonly ResiliencePipeline<Result<TransitionOutput>> _retryPipeline;

    /// <summary>
    /// Initializes a new instance of DirectTriggerTaskExecutor.
    /// </summary>
    public DirectTriggerTaskExecutor(
        IScriptEngine scriptEngine,
        IRuntimeInfoProvider runtimeInfoProvider,
        IRemoteInvokerService remoteInvoker,
        IInstanceCommandGateway instanceCommandGateway,
        IInstanceQueryGateway instanceQueryGateway,
        IDomainDiscoveryResolver endpointResolver,
        IResultResiliencePipelineFactory resilienceFactory,
        ILogger<DirectTriggerTaskExecutor> logger)
        : base(scriptEngine, runtimeInfoProvider, remoteInvoker, logger)
    {
        _instanceCommandGateway = instanceCommandGateway;
        _instanceQueryGateway = instanceQueryGateway;
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
        Logger.LogDebug("Using local IInstanceCommandGateway for DirectTrigger task {TaskKey}", task.Key);

        var instanceIdResult = await ResolveInstanceIdAsync(task, instanceIdentifier, cancellationToken);
        if (!instanceIdResult.IsSuccess)
        {
            Logger.TaskLocalExecutionFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance.Id.ToString(),
                instanceIdResult.Error.Message ?? "DirectTrigger instance resolution failed");

            return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Failure(
                error: instanceIdResult.Error.Message ?? "DirectTrigger instance resolution failed",
                statusCode: 404,
                taskType: TaskType.ToString()));
        }
        
        var headers = ConvertTaskHeadersToDictionary(task.Headers);
        
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
            data: transitionData,
            sync: task.TriggerSync)
        {
            Headers = headers ?? new Dictionary<string, string?>()
        };

        // Execute with retry pipeline for transient failures (e.g., instance lock scenarios)
        var result = await _retryPipeline.ExecuteAsync(
            async token => await ExecuteTransitionWithUowAsync(task, instanceIdResult.Value, input, token),
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
        Guid instanceId,
        TransitionInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _instanceCommandGateway.TransitionAsync(
                instanceId,
                task.TransitionName,
                input,
                cancellationToken);

            if (!result.IsSuccess)
            {
                Logger.TaskLocalExecutionFailed(
                    task.Key,
                    TaskType.ToString(),
                    instanceId.ToString(),
                    result.Error.Message ?? "DirectTrigger transition failed");
                return Result<TransitionOutput>.Fail(result.Error);
            }

            return result;
        }
        catch (Exception ex)
        {
            Logger.TaskLocalExecutionFailed(
                task.Key,
                TaskType.ToString(),
                instanceId.ToString(),
                ex.Message);
            
            return Result<TransitionOutput>.Fail(
                Error.Failure(
                    WorkflowErrorCodes.ExecutionStepFailed,
                    $"DirectTrigger transition execution failed: {ex.Message}",
                    detail: ex.GetType().Name));
        }
    }

    private async Task<Result<Guid>> ResolveInstanceIdAsync(
        DirectTriggerTask task,
        string instanceIdentifier,
        CancellationToken cancellationToken)
    {
        if (Guid.TryParse(instanceIdentifier, out var instanceId))
        {
            return Result<Guid>.Ok(instanceId);
        }

        var queryInput = new GetInstanceInput
        {
            Domain = task.TriggerDomain,
            Workflow = task.TriggerFlow,
            Instance = instanceIdentifier
        };

        var instanceResult = await _instanceQueryGateway.GetInstanceAsync(queryInput, cancellationToken);
        if (!instanceResult.Result.IsSuccess || instanceResult.Result.Value?.Id == null)
        {
            return Result<Guid>.Fail(Error.Validation(
                WorkflowErrorCodes.TaskExecution,
                "DirectTrigger instance could not be resolved to a valid identifier"));
        }

        return Result<Guid>.Ok(instanceResult.Result.Value.Id.Value);
    }

    private async Task<Result<TaskInvocationResult>> ExecuteRemoteAsync(
        DirectTriggerTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        Logger.LogDebug("Using RemoteInvokerService for DirectTrigger task {TaskKey}", task.Key);

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
        var enrichResult = await EnrichBindingAsync<DirectTriggerBinding>(
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
            TaskTypes.DirectTrigger,
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
        var binding = envelope.Binding.Deserialize<DirectTriggerBinding>();
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
        var enrichedBinding = new DirectTriggerBinding
        {
            Domain = binding.Domain,
            Workflow = binding.Workflow,
            TransitionName = binding.TransitionName,
            InstanceId = binding.InstanceId,
            Key = binding.Key,
            Tags = binding.Tags,
            Body = binding.Body,
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
}