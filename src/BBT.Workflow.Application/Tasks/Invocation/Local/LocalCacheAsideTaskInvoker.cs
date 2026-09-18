using System.Text.Json;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Core.Invocation;
using BBT.Workflow.Execution.Core.StateStores;
using BBT.Workflow.Logging;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Invocation.Local;

/// <summary>
/// Cache-aside read-through executed in the Orchestration host. The hit path costs one state-store
/// call and nothing else — before this, even a hit paid a full remote invoke round trip, which is
/// the opposite of what a cache is for. On a miss the source task is dispatched through the local
/// invoker registry when its type has one, and falls back to the Execution service otherwise, so a
/// source type that only exists remotely (python, conversation, triggers) keeps working unchanged.
/// </summary>
public sealed class LocalCacheAsideTaskInvoker(
    IStateStoreClient stateStore,
    ILocalTaskInvokerRegistry localInvokers,
    IRemoteInvokerService remoteInvoker,
    ILogger<LocalCacheAsideTaskInvoker> logger) : ILocalTaskInvoker
{
    /// <inheritdoc />
    public string TaskType => TaskTypes.CacheAside;

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        string? taskKey,
        JsonElement binding,
        TaskTraceContext? traceContext,
        CancellationToken cancellationToken = default)
    {
        var typedBinding = binding.Deserialize<CacheAsideBinding>();
        if (typedBinding is null)
        {
            return TaskInvocationResult.Failure(
                error: $"CacheAside task {taskKey} produced an empty binding.",
                taskType: TaskTypes.CacheAside);
        }

        var result = await CacheAsideInvocation.ExecuteAsync(
            stateStore,
            typedBinding,
            (sourceEnvelope, ct) => DispatchSourceAsync(sourceEnvelope, traceContext, ct),
            TaskTypes.CacheAside,
            cancellationToken,
            taskKey);

        // The shared core never throws (a caller cancellation aside, rethrown as-is) or logs;
        // classify the returned result here so this host's log lines carry the workflow-structured
        // events, same split every other local invoker makes (cancellation checked first). Reachable
        // only through a propagated source-task result whose own invoker flagged it cancelled — a
        // cancellation during the cache read/write itself is rethrown by the core, never returned as
        // a result to classify (see CacheAsideInvocation's type doc).
        if (!result.IsSuccess && HttpTaskInvocation.WasCancelled(result))
        {
            logger.LocalTaskInvocationCancelled(taskKey, TaskTypes.CacheAside);
        }
        else if (!result.IsSuccess && result.StatusCode is null)
        {
            logger.LocalTaskInvocationFailed(
                taskKey, TaskTypes.CacheAside, result.ErrorMessage ?? "Unknown error");
        }

        return LocalInvocationResultMapper.ToOrchestratorResult(result);
    }

    /// <summary>
    /// Runs the source task on a miss. Local when its type has an in-process invoker, remote
    /// otherwise — the same capability gate the router applies, expressed here because the source
    /// envelope is already flattened and carries no <c>WorkflowTask</c> to route on.
    /// </summary>
    private async Task<Execution.TaskInvocationResult> DispatchSourceAsync(
        Execution.TaskEnvelope sourceEnvelope,
        TaskTraceContext? traceContext,
        CancellationToken cancellationToken)
    {
        if (localInvokers.Get(sourceEnvelope.TaskType) is { } local)
        {
            var localResult = await local.InvokeAsync(
                sourceEnvelope.TaskKey, sourceEnvelope.Binding, traceContext, cancellationToken);
            return LocalInvocationResultMapper.ToWireResult(localResult);
        }

        var envelope = new TaskEnvelope
        {
            TaskType = sourceEnvelope.TaskType,
            TaskKey = sourceEnvelope.TaskKey,
            Binding = sourceEnvelope.Binding
        };

        var remote = await remoteInvoker.InvokeAsync(
            sourceEnvelope.TaskType, sourceEnvelope.TaskKey, envelope,
            traceContext ?? new TaskTraceContext(), cancellationToken);

        return remote.IsSuccess
            ? LocalInvocationResultMapper.ToWireResult(remote.Value!)
            : Execution.TaskInvocationResult.Failure(
                error: remote.Error.Message ?? "Source task invocation failed",
                taskType: sourceEnvelope.TaskType);
    }
}
