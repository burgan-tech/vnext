using System.Text.Json;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Core.Invocation;
using BBT.Workflow.Execution.Core.StateStores;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Invocation.Local;

/// <summary>
/// Runs a state-store task in-process through <see cref="StateStoreInvocation"/>, the body the
/// Execution service's <c>StateStoreTaskInvoker</c> also uses — command routing (get/set/delete),
/// the shared <c>custom:</c> key namespace and result shaping are defined once, composed on the
/// same <see cref="IStateStoreClient"/> injected here (also used by the function response cache,
/// issue #1007).
/// </summary>
public sealed class LocalStateStoreTaskInvoker(
    IStateStoreClient stateStore,
    ILogger<LocalStateStoreTaskInvoker> logger) : ILocalTaskInvoker
{
    /// <inheritdoc />
    public string TaskType => TaskTypes.StateStore;

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        string? taskKey,
        JsonElement binding,
        TaskTraceContext? traceContext,
        CancellationToken cancellationToken = default)
    {
        var typedBinding = binding.Deserialize<StateStoreBinding>();
        if (typedBinding is null)
        {
            return TaskInvocationResult.Failure(
                error: $"State store task {taskKey} produced an empty binding.",
                taskType: TaskTypes.StateStore);
        }

        var result = await StateStoreInvocation.ExecuteAsync(
            stateStore, typedBinding, TaskTypes.StateStore, cancellationToken, taskKey);

        // Cancellation is ordinary traffic (a caller timing out, an instance cancelled mid-call)
        // and must not trip anything alerting on this invoker's Error rate — checked first, same
        // split every other local invoker makes. A state-store result never carries a numeric
        // StatusCode on any path (there is no wire status code for a Dapr state-store call), so
        // unlike the HTTP-shaped local invokers this "transport failure" branch is reached by
        // every non-cancelled failure here — a missing store name and an unsupported command
        // included, not only a connection-level failure.
        if (!result.IsSuccess && HttpTaskInvocation.WasCancelled(result))
        {
            logger.LocalTaskInvocationCancelled(taskKey, TaskTypes.StateStore);
        }
        else if (!result.IsSuccess && result.StatusCode is null)
        {
            logger.LocalTaskInvocationFailed(
                taskKey, TaskTypes.StateStore, result.ErrorMessage ?? "Unknown error");
        }

        return LocalInvocationResultMapper.ToOrchestratorResult(result);
    }
}
