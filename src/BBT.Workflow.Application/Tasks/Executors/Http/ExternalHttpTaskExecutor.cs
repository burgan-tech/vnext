using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Mapping;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Executor for local HTTP tasks (<see cref="TaskType.ExternalHttp"/>, discriminator "21"):
/// the user-defined URL is invoked directly by the Orchestrator process instead of being
/// routed through the Execution service's <c>/execution/invoke/{type}/{key}</c> hop.
/// <para>
/// The lifecycle is identical to <see cref="HttpTaskExecutor"/> — input mapping runs locally,
/// the task is flattened through the same <see cref="TaskBindingMapper"/> into the same
/// <see cref="HttpTaskBinding"/> the remote path would put on the wire, and output mapping runs
/// locally against the same result shape. Only the transport differs: the binding is handed to
/// <see cref="IExternalHttpTaskInvoker"/> in-process, so no Dapr sidecar, circuit breaker or
/// remote-invocation timeout participates — the task's own <c>timeoutSeconds</c> (default 30)
/// is the only bound below the job budget.
/// </para>
/// <typeparamref name="TTask"/> is <see cref="HttpTask"/> because <see cref="ExternalHttpTask"/>
/// derives from it; the registry routes by <see cref="WorkflowTask.GetTaskType"/>, so plain
/// type-6 HTTP tasks never reach this executor.
/// </summary>
public sealed class ExternalHttpTaskExecutor : TaskExecutorBase<HttpTask>
{
    private readonly IExternalHttpTaskInvoker _localInvoker;
    private readonly IScriptEngine _scriptEngine;

    /// <summary>
    /// Initializes a new instance of ExternalHttpTaskExecutor.
    /// </summary>
    public ExternalHttpTaskExecutor(
        IExternalHttpTaskInvoker localInvoker,
        IScriptEngine scriptEngine,
        ILogger<ExternalHttpTaskExecutor> logger)
        : base(logger)
    {
        _localInvoker = localInvoker;
        _scriptEngine = scriptEngine;
    }

    /// <inheritdoc />
    public override TaskType TaskType => TaskType.ExternalHttp;

    /// <inheritdoc />
    protected override async Task<Result<ScriptResponse?>> PrepareInputAsync(
        HttpTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var mapping = context.OnExecuteTask.Mapping;
        if (mapping is null || !mapping.HasMappingCode)
        {
            return Result<ScriptResponse?>.Ok(null);
        }

        var result = await ResultExtensions.TryAsync<ScriptResponse?>(async ct =>
        {
            var scriptRunner = await _scriptEngine.CompileToInstanceAsync<IMapping>(
                mapping,
                flowScripts: context.ScriptContext.Workflow?.Scripts,
                cancellationToken: ct);

            return await scriptRunner.InputHandler(task, context.ScriptContext);
        }, cancellationToken, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"External HTTP task input handler failed: {ScriptDiagnostics.Explain(ex)}"));

        if (!result.IsSuccess)
        {
            Logger.TaskInputHandlerFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance?.Id ?? Guid.Empty,
                result.Error.Message ?? "Unknown error");
        }

        return result;
    }

    /// <inheritdoc />
    protected override async Task<Result<TaskInvocationResult>> InvokeAsync(
        HttpTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        // Flatten through the same binding mapper as the remote path so both HTTP task types share
        // one contract (rawBody precedence, content-type resolution, header serialization).
        var envelopeResult = TaskBindingMapper.CreateEnvelope(task);
        if (!envelopeResult.IsSuccess)
        {
            Logger.TaskEnvelopeCreationFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance?.Id ?? Guid.Empty,
                envelopeResult.Error.Message ?? "Unknown error");
            return Result<TaskInvocationResult>.Fail(envelopeResult.Error);
        }

        var binding = envelopeResult.Value!.Binding.Deserialize<HttpTaskBinding>();
        if (binding is null)
        {
            return Result<TaskInvocationResult>.Fail(Error.Failure(
                WorkflowErrorCodes.TaskExecution,
                $"External HTTP task {task.Key} produced an empty HTTP binding."));
        }

        var result = await _localInvoker.InvokeAsync(task.Key, binding, cancellationToken);

        if (!result.IsSuccess && result.StatusCode is null)
        {
            // Transport-level failure (no response). HTTP error responses flow through as results,
            // mirroring the remote path where the error boundary decides.
            Logger.TaskInvocationFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance?.Id ?? Guid.Empty,
                result.ErrorMessage ?? "Unknown error");
        }

        return Result<TaskInvocationResult>.Ok(result);
    }

    /// <inheritdoc />
    protected override async Task<Result<object?>> ProcessOutputAsync(
        HttpTask task,
        TaskInvocationResult invocationResult,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        UpdateScriptContextWithResponse(task.Key, invocationResult, context.ScriptContext);

        var mapping = context.OnExecuteTask.Mapping;
        if (mapping is null || !mapping.HasMappingCode)
        {
            return Result<object?>.Ok(invocationResult.Data);
        }

        var result = await ResultExtensions.TryAsync<object?>(async ct =>
        {
            var scriptRunner = await _scriptEngine.CompileToInstanceAsync<IMapping>(
                mapping,
                flowScripts: context.ScriptContext.Workflow?.Scripts,
                cancellationToken: ct);

            var outputResponse = await scriptRunner.OutputHandler(context.ScriptContext);
            return outputResponse.Data;
        }, cancellationToken, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"External HTTP task output handler failed: {ScriptDiagnostics.Explain(ex)}"));

        if (!result.IsSuccess)
        {
            Logger.TaskOutputHandlerFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance?.Id ?? Guid.Empty,
                result.Error.Message ?? "Unknown error");
        }

        return result;
    }
}
