using System.Text.Json;
using BBT.Aether;
using BBT.Aether.Guids;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Discovery;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Executor for SubProcess tasks that start new subprocess workflow instances.
/// Domain-aware: uses local IInstanceCommandAppService for same domain,
/// RemoteInvokerService for cross-domain execution.
/// Correlation is ALWAYS saved locally regardless of execution path.
/// </summary>
public sealed class SubProcessTaskExecutor : TriggerTaskExecutorBase<SubProcessTask>
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IInstanceRepository _instanceRepository;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IConfiguration _configuration;
    private readonly IDomainDiscoveryResolver _endpointResolver;

    /// <summary>
    /// Initializes a new instance of SubProcessTaskExecutor.
    /// </summary>
    public SubProcessTaskExecutor(
        IServiceScopeFactory scopeFactory,
        IScriptEngine scriptEngine,
        IRuntimeInfoProvider runtimeInfoProvider,
        IRemoteInvokerService remoteInvoker,
        IInstanceRepository instanceRepository,
        IGuidGenerator guidGenerator,
        IConfiguration configuration,
        IDomainDiscoveryResolver endpointResolver,
        ILogger<SubProcessTaskExecutor> logger)
        : base(scriptEngine, runtimeInfoProvider, remoteInvoker, logger)
    {
        _serviceScopeFactory = scopeFactory;
        _instanceRepository = instanceRepository;
        _guidGenerator = guidGenerator;
        _configuration = configuration;
        _endpointResolver = endpointResolver;
    }

    /// <inheritdoc />
    public override TaskType TaskType => TaskType.SubProcess;

    /// <inheritdoc />
    protected override string GetTargetDomain(SubProcessTask task) => task.TriggerDomain;

    /// <inheritdoc />
    protected override async Task<Result<TaskInvocationResult>> InvokeAsync(
        SubProcessTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        // Generate IDs for correlation tracking
        var subFlowInstanceId = _guidGenerator.Create();
        var correlationId = _guidGenerator.Create();
        var isSameDomain = IsSameDomain(task);

        Logger.LogDebug("SubProcess task {TaskKey} targeting domain {TargetDomain}, same domain: {IsSameDomain}",
            task.Key, task.TriggerDomain, isSameDomain);

        TaskInvocationResult result;

        if (isSameDomain)
        {
            result = await ExecuteLocalAsync(task, context, subFlowInstanceId, correlationId, cancellationToken);
        }
        else
        {
            result = await ExecuteRemoteAsync(task, context, subFlowInstanceId, correlationId, cancellationToken);
        }

        return Result<TaskInvocationResult>.Ok(result);
    }

    /// <inheritdoc />
    protected override async Task<Result> PostProcessAsync(
        SubProcessTask task,
        TaskInvocationResult invocationResult,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        if (invocationResult.IsSuccess)
        {
            // Extract IDs from result metadata
            if (invocationResult.Metadata == null ||
                !invocationResult.Metadata.TryGetValue("SubFlowInstanceId", out var subFlowIdObj) ||
                !invocationResult.Metadata.TryGetValue("CorrelationId", out var correlationIdObj))
            {
                Logger.LogWarning("SubProcess task {TaskKey} result missing correlation metadata", task.Key);
                return Result.Ok();
            }

            var subFlowInstanceId = subFlowIdObj is Guid subGuid ? subGuid : Guid.Parse(subFlowIdObj.ToString()!);
            var correlationId = correlationIdObj is Guid corrGuid ? corrGuid : Guid.Parse(correlationIdObj.ToString()!);

            // Create and persist correlation
            return await CreateCorrelationAsync(context, task, correlationId, subFlowInstanceId, cancellationToken);
        }

        Logger.LogWarning("SubProcess task {TaskKey} result ignored correlation", task.Key);
        return Result.Ok();
    }

    private async Task<TaskInvocationResult> ExecuteLocalAsync(
        SubProcessTask task,
        TaskExecutorContext context,
        Guid subFlowInstanceId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        Logger.LogDebug("Using local IInstanceCommandAppService for SubProcess task {TaskKey}", task.Key);

        try
        {
            var input = BuildStartInstanceInput(task, context.ScriptContext, subFlowInstanceId);
            await using var scope = _serviceScopeFactory.CreateAsyncScope();
            var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
            var localCommandService = scope.ServiceProvider.GetRequiredService<IInstanceCommandAppService>();
            using (currentSchema.Use(input.Workflow))
            {
                var result = await localCommandService.StartAsync(input, cancellationToken);

                if (!result.IsSuccess)
                {
                    Logger.TaskLocalExecutionFailed(
                        task.Key,
                        TaskType.ToString(),
                        context.ScriptContext.Instance.Id.ToString(),
                        result.Error.Message ?? "SubProcess start failed");
                    return TaskInvocationResult.Failure(
                        error: result.Error.Message ?? "SubProcess start failed",
                        statusCode: 500,
                        taskType: TaskType.ToString(),
                        metadata: new Dictionary<string, object>
                        {
                            ["SubFlowInstanceId"] = subFlowInstanceId,
                            ["CorrelationId"] = correlationId
                        });
                }

                return TaskInvocationResult.Success(
                    data: new
                    {
                        result.Value!.Id,
                        result.Value.Status,
                        SubFlowInstanceId = subFlowInstanceId,
                        CorrelationId = correlationId
                    },
                    statusCode: 200,
                    taskType: TaskType.ToString(),
                    metadata: new Dictionary<string, object>
                    {
                        ["SubFlowInstanceId"] = subFlowInstanceId,
                        ["CorrelationId"] = correlationId
                    });
            }
        }
        catch (Exception ex)
        {
            Logger.TaskLocalExecutionFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance.Id.ToString(),
                ex.Message);

            return TaskInvocationResult.Failure(
                error: $"SubProcess execution failed: {ex.Message}",
                statusCode: 500,
                taskType: TaskType.ToString(),
                metadata: new Dictionary<string, object>
                {
                    ["SubFlowInstanceId"] = subFlowInstanceId,
                    ["CorrelationId"] = correlationId,
                    ["ExceptionType"] = ex.GetType().Name
                });
        }
    }

    private async Task<TaskInvocationResult> ExecuteRemoteAsync(
        SubProcessTask task,
        TaskExecutorContext context,
        Guid subFlowInstanceId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        Logger.LogDebug("Using RemoteInvokerService for SubProcess task {TaskKey}", task.Key);

        var binding = await BuildSubProcessBindingAsync(task, context.ScriptContext, subFlowInstanceId, cancellationToken);
        var envelope = new TaskEnvelope
        {
            TaskType = TaskTypes.SubProcess,
            TaskKey = task.Key,
            Binding = JsonSerializer.SerializeToElement(binding)
        };

        var traceContext = RemoteInvoker.CreateTraceContext(context.ScriptContext);
        var result = await RemoteInvoker.InvokeAsync(
            TaskTypes.SubProcess,
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
                result.Error.Message ?? "Remote SubProcess execution failed");
            return TaskInvocationResult.Failure(
                error: result.Error.Message ?? "Remote SubProcess execution failed",
                statusCode: 500,
                taskType: TaskType.ToString(),
                metadata: new Dictionary<string, object>
                {
                    ["SubFlowInstanceId"] = subFlowInstanceId,
                    ["CorrelationId"] = correlationId
                });
        }

        // Merge correlation metadata into result
        var remoteResult = result.Value!;
        var metadata = remoteResult.Metadata ?? new Dictionary<string, object>();
        metadata["SubFlowInstanceId"] = subFlowInstanceId;
        metadata["CorrelationId"] = correlationId;

        return new TaskInvocationResult
        {
            IsSuccess = remoteResult.IsSuccess,
            StatusCode = remoteResult.StatusCode,
            Data = remoteResult.Data ?? new
            {
                SubFlowInstanceId = subFlowInstanceId,
                CorrelationId = correlationId
            },
            Body = remoteResult.Body,
            ErrorMessage = remoteResult.ErrorMessage,
            ExecutionDurationMs = remoteResult.ExecutionDurationMs,
            TaskType = TaskType.ToString(),
            Headers = remoteResult.Headers,
            Metadata = metadata
        };
    }

    private StartInstanceInput BuildStartInstanceInput(SubProcessTask task, ScriptContext context,
        Guid subFlowInstanceId)
    {
        return new StartInstanceInput(
                domain: task.TriggerDomain,
                workflow: task.TriggerFlow,
                version: task.TriggerVersion,
                sync: false) // Always async
            {
                Instance = new CreateInstanceInput
                {
                    Id = subFlowInstanceId,
                    Key = task.TriggerKey,
                    Attributes = task.Body,
                    Tags = task.TriggerTags,
                    Callback = GetCallbackAppId(),
                    ExtraProperties = BuildExtraProperties(context, task)
                },
                Headers = ExtractHeaders(context) ?? new Dictionary<string, string?>()
            };
    }

    private async Task<SubProcessBinding> BuildSubProcessBindingAsync(
        SubProcessTask task,
        ScriptContext context,
        Guid subFlowInstanceId,
        CancellationToken cancellationToken)
    {
        var headers = ExtractHeaders(context);

        var preferredKind = task.UseDapr ? EndpointKind.Dapr : EndpointKind.Url;
        var endpoint = await _endpointResolver.GetEndpointAsync(
            task.TriggerDomain, preferredKind, cancellationToken);

        return new SubProcessBinding
        {
            Domain = task.TriggerDomain,
            Workflow = task.TriggerFlow,
            Version = task.TriggerVersion,
            InstanceId = subFlowInstanceId,
            Key = task.TriggerKey,
            Callback = GetCallbackAppId(),
            Body = task.Body,
            Tags = task.TriggerTags,
            Headers = headers != null ? JsonSerializer.Serialize(headers) : null,
            ExtraProperties = BuildExtraPropertiesAsDictionary(context, task),
            Sync = false, // Always Async
            UseDapr = task.UseDapr,
            BaseUrl = endpoint.BaseUrl.ToString(),
            DaprAppId = endpoint.DaprAppId
        };
    }

    private async Task<Result> CreateCorrelationAsync(
        TaskExecutorContext context,
        SubProcessTask task,
        Guid correlationId,
        Guid subFlowInstanceId,
        CancellationToken cancellationToken)
    {
        var result = await ResultExtensions.TryAsync(
            async ct =>
            {
                var correlation = InstanceCorrelation.Create(
                    correlationId,
                    context.ScriptContext.Instance.Id,
                    context.ScriptContext.Instance.GetCurrentState,
                    subFlowInstanceId,
                    SubFlowType.SubProcess.Code,
                    task.TriggerDomain,
                    task.TriggerFlow,
                    task.TriggerVersion);

                var trackedInstance = await _instanceRepository.GetAsync(context.ScriptContext.Instance.Id, true, ct);
                trackedInstance.AddCorrelation(correlation);
                await _instanceRepository.UpdateAsync(trackedInstance, true, ct);
            },
            cancellationToken,
            ex => Error.Failure(
                WorkflowErrorCodes.TriggerSubProcessExecutionFailed,
                $"Failed to create correlation for SubProcess task: {ex.Message}"));

        if (!result.IsSuccess)
        {
            Logger.TaskCorrelationFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance.Id,
                result.Error.Message ?? "Unknown error");
        }

        return result;
    }

    private string? GetCallbackAppId() => _configuration["DAPR_APP_ID"];

    private static ExtraPropertyDictionary BuildExtraProperties(ScriptContext context, SubProcessTask task)
    {
        return new ExtraPropertyDictionary
        {
            [DomainConsts.MetaDataKeys.Id] = context.Instance.Id,
            [DomainConsts.MetaDataKeys.Key] = context.Instance.Key ?? string.Empty,
            [DomainConsts.MetaDataKeys.Domain] = context.Workflow.Domain,
            [DomainConsts.MetaDataKeys.Flow] = context.Workflow.Key,
            [DomainConsts.MetaDataKeys.Version] = context.Workflow.Version,
            [DomainConsts.MetaDataKeys.State] = context.Instance.GetCurrentState,
            [DomainConsts.MetaDataKeys.Transition] = context.Transition.Key ?? string.Empty,
            [DomainConsts.MetaDataKeys.FlowType] = SubFlowType.SubProcess.Code
        };
    }

    private static Dictionary<string, object> BuildExtraPropertiesAsDictionary(ScriptContext context,
        SubProcessTask task)
    {
        return new Dictionary<string, object>
        {
            [DomainConsts.MetaDataKeys.Id] = context.Instance.Id,
            [DomainConsts.MetaDataKeys.Key] = context.Instance.Key ?? string.Empty,
            [DomainConsts.MetaDataKeys.Domain] = context.Workflow.Domain,
            [DomainConsts.MetaDataKeys.Flow] = context.Workflow.Key,
            [DomainConsts.MetaDataKeys.Version] = context.Workflow.Version,
            [DomainConsts.MetaDataKeys.State] = context.Instance.GetCurrentState,
            [DomainConsts.MetaDataKeys.Transition] = context.Transition.Key ?? string.Empty,
            [DomainConsts.MetaDataKeys.FlowType] = SubFlowType.SubProcess.Code
        };
    }
}