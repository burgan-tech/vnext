using BBT.Aether.AspNetCore.Controllers;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Logging;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Execution.Controllers.Executions;

/// <summary>
/// Controller for handling stateless task execution requests from the Orchestration service.
/// Receives task envelopes with strongly-typed bindings via Dapr Service Invocation and executes them.
/// No database access, pure execution only.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/execution")]
public sealed class ExecutionController(
    ITaskInvokerRegistry invokerRegistry,
    ILogger<ExecutionController> logger)
    : AetherControllerBase
{
    /// <summary>
    /// Invokes a task using the envelope-based routing pattern.
    /// The envelope contains TaskType, Version, TaskKey and strongly-typed Binding.
    /// The registry routes the invocation to the appropriate invoker based on TaskType.
    /// </summary>
    /// <param name="type">Task type discriminator for invoker resolution (e.g., "http", "daprservice").</param>
    /// <param name="key">Task key for logging and tracing.</param>
    /// <param name="request">The task invocation request with envelope and trace context.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>Task invocation response with result.</returns>
    /// <response code="200">Task invoked successfully</response>
    /// <response code="400">Validation error or unknown task type</response>
    /// <response code="500">Internal server error</response>
    [HttpPost("invoke/{type}/{key}")]
    [ProducesResponseType(typeof(TaskInvokeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> InvokeTaskAsync(
        [FromRoute] string type,
        [FromRoute] string key,
        [FromBody] TaskInvokeRequest request,
        CancellationToken cancellationToken = default)
    {
        var envelope = request.Envelope;
        var traceContext = request.TraceContext;

        using (logger.BeginScope(new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.Domain] = traceContext?.Domain ?? "unknown",
            [TelemetryConstants.TagNames.Flow] = traceContext?.WorkflowKey ?? "unknown",
            [TelemetryConstants.TagNames.InstanceId] = traceContext?.InstanceId ?? Guid.Empty,
            [TelemetryConstants.TagNames.TaskKey] = envelope.TaskKey,
            [TelemetryConstants.TagNames.TaskType] = envelope.TaskType
        }))
        {
            // TODO: burada her zaman ok dönğyoruz. Bu böyle olmak zorunda ama TaskInvokeResponse'da ErrorCode ve ExceptionTypeName kısımlarının değerlerini TaskInvocationResult'a aktarıyoruz.
            var result = await invokerRegistry.InvokeAsync(envelope, cancellationToken);
            return Ok(new TaskInvokeResponse
            {
                Success = result.IsSuccess,
                ErrorMessage = result.ErrorMessage,
                Result = result,
                ExecutionDurationMs = result.ExecutionDurationMs
            });
        }
    }
}
