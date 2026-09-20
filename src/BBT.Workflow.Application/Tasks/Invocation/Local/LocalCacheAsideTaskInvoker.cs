using System.Text.Json;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Core.Invocation;
using BBT.Workflow.Execution.Core.StateStores;
using BBT.Workflow.Logging;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Invocation.Local;

/// <summary>
/// Cache-aside read-through executed in the Orchestration host. The hit path costs one state-store
/// call and nothing else — before this, even a hit paid a full remote invoke round trip, which is
/// the opposite of what a cache is for. On a miss the source task is dispatched locally only when
/// BOTH gates agree: the registry has an in-process invoker for its type AND the router's own
/// policy verdict for that type is <see cref="ExecutionMode.Local"/> — asking only the registry
/// would let an operator's <c>Modes.http = Remote</c> (meant to stop orchestrator HTTP egress) be
/// silently bypassed by an <c>http</c> source task reached through a cache-aside miss, since the
/// registry is a pure capability check with no awareness of the configured policy. Falls back to
/// the Execution service when either gate says no, so a source type that only exists remotely
/// (python, conversation, triggers) keeps working unchanged. A cache read/write failure swallowed
/// under <c>bypassOnCacheError=true</c> is reported by the shared core through a callback and
/// logged here via a <c>[LoggerMessage]</c> generator (the repo's no-raw-<c>LogWarning</c> rule) —
/// the Execution host's own <c>CacheAsideTaskInvoker</c> restores the same signal with its own
/// message text.
/// </summary>
public sealed class LocalCacheAsideTaskInvoker(
    IStateStoreClient stateStore,
    IServiceProvider serviceProvider,
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
            taskKey,
            onBypassedCacheError: (stage, ex) => logger.LocalCacheAsideBypassedCacheError(
                ex, taskKey, stage == CacheAsideInvocation.CacheAsideBypassStage.Read ? "read" : "write"));

        // The shared core never throws (a caller cancellation aside, rethrown as-is) or logs;
        // classify the returned result here so this host's log lines carry the workflow-structured
        // events, same split every other local invoker makes (cancellation checked first). Reachable
        // only through a propagated source-task result whose own invoker flagged it cancelled — a
        // cancellation during the cache read/write itself is rethrown by the core, never returned as
        // a result to classify (see CacheAsideInvocation's type doc). The ExceptionType gate below
        // matches the Execution host's own invokers (e.g. StateStoreTaskInvoker): a returned
        // validation failure (a missing key, a source task's own business failure) is not logged
        // at Error here — only a genuine thrown exception the shared core caught is.
        if (!result.IsSuccess && HttpTaskInvocation.WasCancelled(result))
        {
            logger.LocalTaskInvocationCancelled(taskKey, TaskTypes.CacheAside);
        }
        else if (!result.IsSuccess && result.StatusCode is null
                 && LocalInvocationResultMapper.HasExceptionType(result, out var exceptionType))
        {
            logger.LocalTaskInvocationFailed(
                taskKey, TaskTypes.CacheAside, result.ErrorMessage ?? "Unknown error", exceptionType);
        }

        return LocalInvocationResultMapper.ToOrchestratorResult(result);
    }

    /// <summary>
    /// Runs the source task on a miss. Local only when the router's policy verdict for the source
    /// type is <see cref="ExecutionMode.Local"/> AND the registry actually has an invoker for it —
    /// the router is asked (not just the registry) so an operator's per-type
    /// <c>Modes.&lt;type&gt; = Remote</c> is honoured for a source task reached through a
    /// cache-aside miss exactly as it would be for that type invoked directly. The registry lookup
    /// is a defensive re-check, same posture as <c>TaskInvocationDispatcher</c>: this is the code
    /// that would dereference a missing invoker, so it must not depend on the router being right.
    /// <paramref name="sourceEnvelope"/> carries no <c>WorkflowTask</c> to route on — the router is
    /// asked with a null task, which resolves purely on type-config/default (see
    /// <see cref="ITaskInvocationRouter.Resolve"/>'s remarks).
    /// </summary>
    /// <remarks>
    /// The <see cref="IServiceProvider"/> — NOT <see cref="ILocalTaskInvokerRegistry"/> and
    /// <see cref="ITaskInvocationRouter"/> — is resolved as a constructor parameter deliberately: this invoker
    /// is itself discovered by <see cref="ILocalTaskInvokerRegistry"/> via
    /// <c>IEnumerable&lt;ILocalTaskInvoker&gt;</c>, whose constructor builds its lookup table
    /// eagerly by constructing every registered invoker. Taking the registry (or the router, which
    /// also depends on the registry) as a constructor parameter closes a cycle at container-build
    /// time: <c>HttpTaskExecutor → ITaskInvocationDispatcher → ITaskInvocationRouter →
    /// ILocalTaskInvokerRegistry → IEnumerable&lt;ILocalTaskInvoker&gt; →
    /// LocalCacheAsideTaskInvoker → ILocalTaskInvokerRegistry</c> — this failed
    /// <c>ValidateOnBuild</c> and kept the Orchestration host from starting at all (issue #1007
    /// follow-up). Both are resolved HERE instead, lazily, only on an actual cache miss/forceRefresh
    /// — exactly mirroring the Execution host's own <c>CacheAsideTaskInvoker</c>, which resolves its
    /// <c>ITaskInvokerRegistry</c> the same way for the same reason. Do not "clean this up" back into
    /// constructor parameters; that reintroduces the cycle and a host that cannot boot.
    /// </remarks>
    private async Task<Execution.TaskInvocationResult> DispatchSourceAsync(
        Execution.TaskEnvelope sourceEnvelope,
        TaskTraceContext? traceContext,
        CancellationToken cancellationToken)
    {
        var router = serviceProvider.GetRequiredService<ITaskInvocationRouter>();
        var localInvokers = serviceProvider.GetRequiredService<ILocalTaskInvokerRegistry>();

        var decision = router.Resolve(task: null, sourceEnvelope.TaskType);

        if (decision.Mode == ExecutionMode.Local
            && localInvokers.Get(sourceEnvelope.TaskType) is { } local)
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

        // The `!remote.IsSuccess` arm is unreachable with today's IRemoteInvokerService
        // implementation (RemoteInvokerService): every failure path — transport error, own-timeout,
        // gRPC failure — wraps `Result.Ok(TaskInvocationResult.Failure(...))`, never `Result.Fail`,
        // and a parent-cancelled call rethrows OperationCanceledException instead of returning.
        // Kept as defensive handling for a future IRemoteInvokerService implementation that DOES use
        // Result.Fail for a pre-flight failure, not a live gap in the current wiring.
        return remote.IsSuccess
            ? LocalInvocationResultMapper.ToWireResult(remote.Value!)
            : Execution.TaskInvocationResult.Failure(
                error: remote.Error.Message ?? "Source task invocation failed",
                taskType: sourceEnvelope.TaskType);
    }
}
