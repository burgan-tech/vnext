using System.Diagnostics;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
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
    /// <see cref="OperationCanceledException"/> propagates so the pipeline's own cancellation
    /// handling runs, exactly as it would have before this timeout existed.
    /// <para>
    /// <b>That distinction has to be made on the RESULT, not only in a catch clause.</b> The
    /// difference between this and the remote path is that here the invocation core runs
    /// in-process, and four of the five cores (<c>HttpTaskInvocation</c>,
    /// <c>DaprServiceInvocation</c>, <c>SoapInvocation</c>, <c>StateStoreInvocation</c>) catch
    /// cancellation themselves — guarded on <c>cancellationToken.IsCancellationRequested</c>, which
    /// is OUR linked token, so the guard holds whichever side cancelled — and return a failed
    /// result stamped <c>Metadata["Cancelled"] = true</c> rather than letting the exception out.
    /// Relying on the catch alone therefore made both branches unreachable for those four: a local
    /// timeout surfaced as the core's own failure instead of a 408, <c>LocalTaskInvocationTimedOut</c>
    /// never fired, and — the part that actually changes pipeline behaviour — caller cancellation
    /// was downgraded to an ordinary task failure that the error boundary then acted on, instead of
    /// unwinding. Only <c>CacheAsideInvocation</c> rethrows, which is why the catch below stays:
    /// both shapes are real and both must map to the same two outcomes.
    /// </para>
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

            // A core that swallowed the cancellation: recover the same decision the catch makes.
            if (!localResult.IsSuccess && HttpTaskInvocation.WasCancelled(ToWire(localResult)))
            {
                // Caller cancelled — the pipeline owns this, exactly as on the remote path.
                cancellationToken.ThrowIfCancellationRequested();

                if (timeoutCts.IsCancellationRequested)
                    return LocalTimeout(task.Key, wireTaskType);
            }

            return Result<TaskInvocationResult>.Ok(localResult);
        }
        catch (OperationCanceledException) when (
            timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // A core that let the cancellation out (CacheAsideInvocation): own per-invocation
            // timeout, not caused by parent pipeline cancellation.
            return LocalTimeout(task.Key, wireTaskType);
        }
    }

    /// <summary>
    /// The one shape a local per-invocation timeout takes, whichever way the core reported it: a
    /// failed result the error boundary can match on (<c>408</c>), never an exception.
    /// </summary>
    private Result<TaskInvocationResult> LocalTimeout(string taskKey, string wireTaskType)
    {
        logger.LocalTaskInvocationTimedOut(taskKey, wireTaskType, _options.LocalInvocationTimeoutSeconds);

        return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Failure(
            error: $"Local invocation timeout after {_options.LocalInvocationTimeoutSeconds}s",
            statusCode: 408,
            taskType: wireTaskType));
    }

    /// <summary>
    /// <c>WasCancelled</c> is a metadata probe that happens to hang off the wire-side result type;
    /// the dispatcher holds the orchestrator-side twin. Only <c>Metadata</c> is read, so this
    /// carries nothing else.
    /// </summary>
    private static Execution.TaskInvocationResult ToWire(TaskInvocationResult result) =>
        new() { Metadata = result.Metadata };
}
