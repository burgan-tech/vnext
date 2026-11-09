using System.Text.Json;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Core.Invocation;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Invocation.Local;

/// <summary>
/// Runs a Dapr service-invocation task from the Orchestration host's own sidecar, removing the
/// Execution round trip (orchestrator sidecar → execution sidecar → execution app → target
/// sidecar becomes orchestrator sidecar → target sidecar). Shares
/// <see cref="DaprServiceInvocation"/> with the Execution host's invoker, so request shaping,
/// reserved-header filtering and accepted-status-code matching are defined once.
/// </summary>
public sealed class LocalDaprServiceTaskInvoker(
    DaprServiceInvocationClient daprInvocation,
    ILogger<LocalDaprServiceTaskInvoker> logger) : ITypedLocalTaskInvoker<DaprServiceBinding>
{
    /// <inheritdoc />
    public string TaskType => TaskTypes.DaprService;

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        string? taskKey,
        JsonElement binding,
        TaskTraceContext? traceContext,
        CancellationToken cancellationToken = default)
    {
        var typedBinding = binding.Deserialize<DaprServiceBinding>();
        if (typedBinding is null)
        {
            return TaskInvocationResult.Failure(
                error: $"Dapr service task {taskKey} produced an empty binding.",
                taskType: TaskTypes.DaprService);
        }

        return await InvokeAsync(taskKey, typedBinding, traceContext, cancellationToken);
    }

    /// <summary>
    /// The typed entry point: the orchestrator's local path hands the mapped binding object straight
    /// here, skipping the JSON round trip the <see cref="JsonElement"/> overload exists for.
    /// </summary>
    public async Task<TaskInvocationResult> InvokeAsync(
        string? taskKey,
        DaprServiceBinding typedBinding,
        TaskTraceContext? traceContext,
        CancellationToken cancellationToken = default)
    {
        var result = await DaprServiceInvocation.SendAsync(
            daprInvocation,
            typedBinding,
            TaskTypes.DaprService,
            cancellationToken,
            LocalInvocationResultMapper.ToWireTraceContext(traceContext),
            taskKey);

        if (!result.IsSuccess && HttpTaskInvocation.WasCancelled(result))
        {
            logger.LocalTaskInvocationCancelled(taskKey, TaskTypes.DaprService);
        }
        else if (!result.IsSuccess && result.StatusCode is null)
        {
            logger.LocalTaskInvocationFailed(
                taskKey, TaskTypes.DaprService, result.ErrorMessage ?? "Unknown error",
                LocalInvocationResultMapper.HasExceptionType(result, out var exceptionType)
                    ? exceptionType
                    : null);
        }

        return LocalInvocationResultMapper.ToOrchestratorResult(result);
    }
}
