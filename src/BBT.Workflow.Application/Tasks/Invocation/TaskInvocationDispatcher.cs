using System.Diagnostics;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Tasks.Invocation;

/// <inheritdoc />
public sealed class TaskInvocationDispatcher(
    ITaskInvocationRouter router,
    ILocalTaskInvokerRegistry localInvokers,
    IRemoteInvokerService remoteInvoker,
    IOptions<TaskInvocationOptions> options,
    ILogger<TaskInvocationDispatcher> logger) : ITaskInvocationDispatcher
{
    private readonly TaskInvocationOptions _options = options.Value;

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

            return await InvokeLocalWithTimeoutAsync(
                task, wireTaskType, envelope, traceContext, localInvoker, cancellationToken);
        }

        return await remoteInvoker.InvokeAsync(
            wireTaskType, task.Key, envelope, traceContext, cancellationToken);
    }

    /// <summary>
    /// Local-path counterpart of <c>RemoteInvokerService.InvokeAsync</c>'s per-invocation timeout
    /// layer (<c>ExecutionApi:InvocationTimeoutSeconds</c>). Before this, the caller's own
    /// cancellation token was passed straight through to a local invoker with no deadline of its
    /// own — harmless for HTTP/SOAP, whose bindings carry their own <c>timeoutSeconds</c>, but a
    /// genuine unbounded wait for DaprService/StateStore/CacheAside, whose bindings have no timeout
    /// field at all. Follows the remote path's own-timer-vs-caller-token distinction: if only our
    /// linked timer fired, this is a local timeout and comes back as a failed result (408) the
    /// error boundary can act on, never an exception; if the caller's token fired, the
    /// <see cref="OperationCanceledException"/> is left to propagate so the pipeline's own
    /// cancellation handling runs, exactly as it would have before this timeout existed.
    /// </summary>
    private async Task<Result<TaskInvocationResult>> InvokeLocalWithTimeoutAsync(
        WorkflowTask task,
        string wireTaskType,
        TaskEnvelope envelope,
        TaskTraceContext traceContext,
        ILocalTaskInvoker localInvoker,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.LocalInvocationTimeoutSeconds));

        try
        {
            var localResult = await localInvoker.InvokeAsync(
                task.Key, envelope.Binding, traceContext, timeoutCts.Token);

            return Result<TaskInvocationResult>.Ok(localResult);
        }
        catch (OperationCanceledException) when (
            timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Own per-invocation timeout — not caused by parent pipeline cancellation.
            logger.LocalTaskInvocationTimedOut(
                task.Key, wireTaskType, _options.LocalInvocationTimeoutSeconds);

            return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Failure(
                error: $"Local invocation timeout after {_options.LocalInvocationTimeoutSeconds}s",
                statusCode: 408,
                taskType: wireTaskType));
        }
    }
}
