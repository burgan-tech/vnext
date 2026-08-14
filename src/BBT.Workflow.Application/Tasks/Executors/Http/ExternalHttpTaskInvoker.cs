using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// In-process implementation of <see cref="IExternalHttpTaskInvoker"/> for the Orchestration host.
/// Delegates the HTTP call to <see cref="HttpTaskInvocation"/> — the single send implementation
/// shared with the Execution service's <c>HttpTaskInvoker</c> — so the two HTTP task types (6 and
/// 21) cannot drift behaviorally. This wrapper only adds the orchestrator's logging and maps the
/// wire-side result type to the orchestrator-side <see cref="TaskInvocationResult"/> twin.
/// </summary>
public sealed class ExternalHttpTaskInvoker(
    IHttpClientFactory httpClientFactory,
    ILogger<ExternalHttpTaskInvoker> logger) : IExternalHttpTaskInvoker
{
    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        string? taskKey,
        HttpTaskBinding binding,
        CancellationToken cancellationToken = default)
    {
        if (!binding.ValidateSSL)
        {
            logger.ExternalHttpTaskSslValidationDisabled(taskKey, binding.Url);
        }

        var result = await HttpTaskInvocation.SendAsync(
            httpClientFactory.CreateClient,
            binding,
            TaskType.ExternalHttp.ToString(),
            cancellationToken);

        // The shared core never throws or logs; classify the failed results here so this host's
        // log lines carry the workflow-structured events.
        if (!result.IsSuccess && HttpTaskInvocation.WasCancelled(result))
        {
            logger.ExternalHttpTaskRequestCancelled(taskKey, binding.Url);
        }
        else if (!result.IsSuccess && result.StatusCode is null)
        {
            logger.ExternalHttpTaskRequestFailed(taskKey, binding.Url, result.ErrorMessage ?? "Unknown error");
        }

        return ToOrchestratorResult(result);
    }

    /// <summary>
    /// Maps the wire-side result (<c>BBT.Workflow.Execution.TaskInvocationResult</c>) to the
    /// orchestrator-side twin the executor pipeline consumes. The two types are structurally
    /// identical by convention; this is the same translation the remote path performs when it
    /// unwraps the <c>/execution/invoke</c> response.
    /// </summary>
    private static TaskInvocationResult ToOrchestratorResult(Execution.TaskInvocationResult result) => new()
    {
        IsSuccess = result.IsSuccess,
        StatusCode = result.StatusCode,
        Body = result.Body,
        Data = result.Data,
        ErrorMessage = result.ErrorMessage,
        Headers = result.Headers,
        Metadata = result.Metadata,
        TaskType = result.TaskType,
        ExecutionDurationMs = result.ExecutionDurationMs
    };
}
