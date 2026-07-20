using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Evaluators;
using BBT.Workflow.Tasks.Factory;
using BBT.Workflow.Tasks.Mapping;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Executor for Cache-Aside (read-through) tasks.
/// Mirrors <c>StateStoreTaskExecutor</c>: it runs the input mapping locally, delegates the cache
/// read-through to the Execution service via <see cref="IRemoteInvokerService"/> (the <c>cacheaside</c>
/// invoker performs the state-store get/set and runs the source task on a miss), then runs the output
/// mapping locally to shape the cached raw result. The dynamic cache key is set by the input mapping's
/// <c>InputHandler</c> via <c>task.SetCacheKey(...)</c>, exactly as the State Store task does.
/// </summary>
public sealed class CacheAsideTaskExecutor : TaskExecutorBase<CacheAsideTask>
{
    private readonly IRemoteInvokerService _remoteInvoker;
    private readonly IScriptEngine _scriptEngine;
    private readonly ITaskFactory _taskFactory;
    private readonly IDynamicExpressoValueEvaluator _expressoEvaluator;

    /// <summary>
    /// Initializes a new instance of <see cref="CacheAsideTaskExecutor"/>.
    /// </summary>
    public CacheAsideTaskExecutor(
        IRemoteInvokerService remoteInvoker,
        IScriptEngine scriptEngine,
        ITaskFactory taskFactory,
        IDynamicExpressoValueEvaluator expressoEvaluator,
        ILogger<CacheAsideTaskExecutor> logger)
        : base(logger)
    {
        _remoteInvoker = remoteInvoker;
        _scriptEngine = scriptEngine;
        _taskFactory = taskFactory;
        _expressoEvaluator = expressoEvaluator;
    }

    /// <inheritdoc />
    public override TaskType TaskType => TaskType.CacheAside;

    /// <inheritdoc />
    protected override async Task<Result<ScriptResponse?>> PrepareInputAsync(
        CacheAsideTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        // 1. Standard input mapping (InputHandler) — may set the cache key, like the State Store task.
        ScriptResponse? inputResponse = null;
        var mapping = context.OnExecuteTask.Mapping;
        if (mapping is not null && mapping.HasMappingCode)
        {
            var result = await ResultExtensions.TryAsync<ScriptResponse?>(async ct =>
            {
                var scriptRunner = await _scriptEngine.CompileToInstanceAsync<IMapping>(
                    mapping,
                    flowScripts: context.ScriptContext.Workflow?.Scripts,
                    cancellationToken: ct);

                return await scriptRunner.InputHandler(task, context.ScriptContext);
            }, cancellationToken, ex => Error.Failure(
                WorkflowErrorCodes.TaskExecution,
                $"CacheAside task input handler failed: {ex.Message}"));

            if (!result.IsSuccess)
            {
                return result;
            }

            inputResponse = result.Value;
        }

        // 2. A Dynamic Expresso key expression computes the key from the request/context and overrides
        //    the static key — the lightweight vary-by mechanism (no full .csx required).
        if (task.KeyExpression is { } keyExpression && keyExpression.HasMappingCode)
        {
            var keyResult = _expressoEvaluator.Evaluate(keyExpression, context.ScriptContext);
            if (!keyResult.IsSuccess)
            {
                return Result<ScriptResponse?>.Fail(keyResult.Error);
            }

            if (!string.IsNullOrWhiteSpace(keyResult.Value))
            {
                task.SetCacheKey(keyResult.Value);
            }
        }

        return Result<ScriptResponse?>.Ok(inputResponse);
    }

    /// <inheritdoc />
    protected override async Task<Result<TaskInvocationResult>> InvokeAsync(
        CacheAsideTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        if (task.SourceTask is null)
        {
            return Result<TaskInvocationResult>.Fail(Error.Validation(
                WorkflowErrorCodes.TaskExecution,
                "CacheAside task requires a 'sourceTask' reference."));
        }

        // Resolve the source task and pre-build its envelope so the invoker can run it on a miss.
        var sourceTaskResult = await _taskFactory.CreateExecutionTaskAsync(task.SourceTask, cancellationToken);
        if (!sourceTaskResult.IsSuccess)
        {
            return Result<TaskInvocationResult>.Fail(sourceTaskResult.Error);
        }

        var sourceEnvelopeResult = TaskBindingMapper.CreateEnvelope(sourceTaskResult.Value!);
        if (!sourceEnvelopeResult.IsSuccess)
        {
            return Result<TaskInvocationResult>.Fail(sourceEnvelopeResult.Error);
        }

        var sourceEnvelope = sourceEnvelopeResult.Value!;
        var binding = new CacheAsideBinding
        {
            Key = task.CacheKey,
            StoreName = string.IsNullOrWhiteSpace(task.StoreName) ? null : task.StoreName,
            TtlInSeconds = task.TtlInSeconds,
            Consistency = task.Consistency,
            BypassOnCacheError = task.BypassOnCacheError,
            ForceRefresh = task.ForceRefresh,
            SourceTask = new Execution.TaskEnvelope
            {
                TaskType = sourceEnvelope.TaskType,
                TaskKey = sourceEnvelope.TaskKey,
                Binding = sourceEnvelope.Binding
            }
        };

        var envelope = new TaskEnvelope
        {
            TaskType = TaskTypes.CacheAside,
            TaskKey = task.Key,
            Binding = JsonSerializer.SerializeToElement(binding)
        };

        var traceContext = _remoteInvoker.CreateTraceContext(context.ScriptContext);
        return await _remoteInvoker.InvokeAsync(
            TaskTypes.CacheAside, task.Key, envelope, traceContext, cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task<Result<object?>> ProcessOutputAsync(
        CacheAsideTask task,
        TaskInvocationResult invocationResult,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var mapping = task.SourceMapping;
        if (mapping is null || !mapping.HasMappingCode)
        {
            return Result<object?>.Ok(invocationResult.Data);
        }

        // Expose the cached/raw result on the script context so the mapping's OutputHandler can read it.
        UpdateScriptContextWithResponse(task.Key, invocationResult, context.ScriptContext);

        return await ResultExtensions.TryAsync<object?>(async ct =>
        {
            var scriptRunner = await _scriptEngine.CompileToInstanceAsync<IMapping>(
                mapping,
                flowScripts: context.ScriptContext.Workflow?.Scripts,
                cancellationToken: ct);

            var outputResponse = await scriptRunner.OutputHandler(context.ScriptContext);
            return outputResponse.Data;
        }, cancellationToken, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"CacheAside task source mapping failed: {ex.Message}"));
    }
}
