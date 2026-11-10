using System.Text.Json;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Core.Invocation;
using BBT.Workflow.Logging;
using Dapr.Client;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Invocation.Local;

/// <summary>
/// Runs a Dapr binding task in-process through <see cref="DaprBindingInvocation"/>, the body the Execution service's
/// <c>DaprBindingTaskInvoker</c> also uses, so the binding is called from the ORCHESTRATION sidecar. That is where
/// the x-storage components live (Helm scopes <c>blobStorageComponents</c> to the orchestrator), so a flow that reads,
/// lists or deletes its stored files needs this path: <c>Workflow:TaskInvocation:Modes:daprbinding = Local</c>
/// (vnext-client-sdk-core#142-B/D). The default stays Remote: components scoped to the Execution app-id keep working.
/// </summary>
public sealed class LocalDaprBindingTaskInvoker(
    DaprClient daprClient,
    ILogger<LocalDaprBindingTaskInvoker> logger) : ILocalTaskInvoker
{
    /// <inheritdoc />
    public string TaskType => TaskTypes.DaprBinding;

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        string? taskKey,
        JsonElement binding,
        TaskTraceContext? traceContext,
        CancellationToken cancellationToken = default)
    {
        var typedBinding = binding.Deserialize<DaprBindingTaskBinding>();
        if (typedBinding is null)
        {
            return TaskInvocationResult.Failure(
                error: $"Dapr binding task {taskKey} produced an empty binding.",
                taskType: TaskTypes.DaprBinding);
        }

        var result = await DaprBindingInvocation.ExecuteAsync(daprClient, typedBinding, TaskTypes.DaprBinding, cancellationToken, taskKey);

        if (!result.IsSuccess && result.Metadata?.ContainsKey("Cancelled") == true)
            logger.LocalTaskInvocationCancelled(taskKey, TaskTypes.DaprBinding);
        else if (!result.IsSuccess && LocalInvocationResultMapper.HasExceptionType(result, out var exceptionType))
            logger.LocalTaskInvocationFailed(taskKey, TaskTypes.DaprBinding, result.ErrorMessage ?? "Unknown error", exceptionType);

        return LocalInvocationResultMapper.ToOrchestratorResult(result);
    }
}
