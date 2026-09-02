using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Logging;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Mapping;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Delegates the fixed JSON input and Python <c>main(input)</c> contract to the Execution service.
/// Python tasks intentionally do not run Orchestration input/output mappings.
/// </summary>
public sealed class PythonTaskExecutor(
    IRemoteInvokerService remoteInvoker,
    ILogger<PythonTaskExecutor> logger,
    IWorkflowMetrics metrics)
    : TaskExecutorBase<PythonTask>(logger, metrics)
{
    public override TaskType TaskType => TaskType.Python;

    protected override Task<Result<ScriptResponse?>> PrepareInputAsync(
        PythonTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
        => Task.FromResult(Result<ScriptResponse?>.Ok(null));

    protected override async Task<Result<TaskInvocationResult>> InvokeAsync(
        PythonTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
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

        var result = await remoteInvoker.InvokeAsync(
            TaskTypes.Python,
            task.Key,
            envelopeResult.Value!,
            remoteInvoker.CreateTraceContext(context.ScriptContext),
            cancellationToken);

        if (!result.IsSuccess)
        {
            Logger.TaskInvocationFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance?.Id ?? Guid.Empty,
                result.Error.Message ?? "Unknown error");
        }

        return result;
    }

    protected override Task<Result<object?>> ProcessOutputAsync(
        PythonTask task,
        TaskInvocationResult invocationResult,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
        => Task.FromResult(Result<object?>.Ok(invocationResult.Data));
}
