using System.Diagnostics;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Invocation;

/// <inheritdoc />
public sealed class TaskInvocationDispatcher(
    ITaskInvocationRouter router,
    ILocalTaskInvokerRegistry localInvokers,
    IRemoteInvokerService remoteInvoker,
    ILogger<TaskInvocationDispatcher> logger) : ITaskInvocationDispatcher
{
    /// <inheritdoc />
    public async Task<Result<TaskInvocationResult>> DispatchAsync(
        WorkflowTask task,
        string wireTaskType,
        TaskEnvelope envelope,
        TaskTraceContext traceContext,
        CancellationToken cancellationToken = default)
    {
        var decision = router.Resolve(task, wireTaskType);

        // Tagged on the ambient task span so "which path did this task take" is answerable from a
        // trace instead of from configuration archaeology.
        Activity.Current?.SetTag("vnext.task.invocation.mode", decision.Mode.ToString());

        // The registry lookup repeats the router's capability gate deliberately: this is the code
        // that would dereference a missing invoker, and it must not depend on the router being right.
        if (decision.Mode == ExecutionMode.Local && localInvokers.Get(wireTaskType) is { } localInvoker)
        {
            logger.TaskInvokedLocally(task.Key, wireTaskType, decision.Reason);

            var localResult = await localInvoker.InvokeAsync(
                task.Key, envelope.Binding, traceContext, cancellationToken);

            return Result<TaskInvocationResult>.Ok(localResult);
        }

        return await remoteInvoker.InvokeAsync(
            wireTaskType, task.Key, envelope, traceContext, cancellationToken);
    }
}
