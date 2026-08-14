using System.Diagnostics;
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
    /// ActivitySource for spans restored from the request body's trace context when
    /// transport-level (traceparent header) propagation did not produce an ambient activity.
    /// </summary>
    private static readonly ActivitySource ActivitySource = new("BBT.Workflow.Execution");

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
        var subject = TelemetryConstants.TryNormalizeIdentityClaim(traceContext?.Sub, out var normalizedSubject)
            ? normalizedSubject
            : null;
        var actSub = TelemetryConstants.TryNormalizeIdentityClaim(traceContext?.ActSub, out var normalizedActSub)
            ? normalizedActSub
            : null;

        using var restoredActivity = RestoreActivityFromBodyIfDetached(traceContext);
        var activity = Activity.Current;
        activity?.SetTag(TelemetryConstants.TagNames.Domain, traceContext?.Domain ?? "unknown");
        activity?.SetTag(TelemetryConstants.TagNames.Flow, traceContext?.WorkflowKey ?? "unknown");
        activity?.SetTag(TelemetryConstants.TagNames.FlowVersion, traceContext?.WorkflowVersion ?? "unknown");
        activity?.SetTag(TelemetryConstants.TagNames.InstanceId, traceContext?.InstanceId.ToString() ?? Guid.Empty.ToString());
        activity?.SetTag(
            TelemetryConstants.TagNames.WorkflowInstanceId,
            traceContext?.InstanceId.ToString("D").ToLowerInvariant() ?? Guid.Empty.ToString("D"));
        activity?.SetTag(TelemetryConstants.TagNames.CorrelationId, traceContext?.CorrelationId);
        activity?.SetTag(TelemetryConstants.TagNames.Sub, subject);
        activity?.SetTag(TelemetryConstants.TagNames.ActSub, actSub);
        activity?.SetTag(TelemetryConstants.TagNames.TaskKey, envelope.TaskKey);
        activity?.SetTag(TelemetryConstants.TagNames.TaskType, envelope.TaskType);
        activity?.SetTag(TelemetryConstants.TagNames.Layer, TelemetryConstants.Layers.Execution);
        activity?.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        if (!string.IsNullOrEmpty(traceContext?.RequestId))
            activity?.SetTag(TelemetryConstants.TagNames.RequestId, traceContext.RequestId);

        if (traceContext?.InstanceId is { } instanceId && instanceId != Guid.Empty)
        {
            activity?.SetBaggage(TelemetryConstants.TagNames.InstanceId, instanceId.ToString("D").ToLowerInvariant());
            activity?.SetBaggage(TelemetryConstants.TagNames.WorkflowInstanceId, instanceId.ToString("D").ToLowerInvariant());
        }

        if (Guid.TryParseExact(traceContext?.CorrelationId, "N", out var correlationId)
            && correlationId != Guid.Empty)
        {
            activity?.SetBaggage(TelemetryConstants.TagNames.CorrelationId, correlationId.ToString("N"));
        }

        if (subject is not null)
        {
            activity?.SetBaggage(TelemetryConstants.TagNames.Sub, subject);
        }

        if (actSub is not null)
        {
            activity?.SetBaggage(TelemetryConstants.TagNames.ActSub, actSub);
        }

        var scope = new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.Domain] = traceContext?.Domain ?? "unknown",
            [TelemetryConstants.TagNames.Flow] = traceContext?.WorkflowKey ?? "unknown",
            [TelemetryConstants.TagNames.InstanceId] = traceContext?.InstanceId ?? Guid.Empty,
            [TelemetryConstants.TagNames.WorkflowInstanceId] = traceContext?.InstanceId.ToString("D").ToLowerInvariant() ?? Guid.Empty.ToString("D"),
            [TelemetryConstants.TagNames.CorrelationId] = traceContext?.CorrelationId ?? "unknown",
            [TelemetryConstants.TagNames.TaskKey] = envelope.TaskKey,
            [TelemetryConstants.TagNames.TaskType] = envelope.TaskType,
            [TelemetryConstants.TagNames.RequestId] = traceContext?.RequestId ?? "N/A"
        };

        if (subject is not null)
        {
            scope[TelemetryConstants.TagNames.Sub] = subject;
        }
        if (actSub is not null)
        {
            scope[TelemetryConstants.TagNames.ActSub] = actSub;
        }

        using (logger.BeginScope(scope))
        {
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

    /// <summary>
    /// Restores the caller's trace from the body's <see cref="TaskTraceContext"/> when transport
    /// propagation left no ambient activity — the fallback keeps the trace tree whole if the
    /// implicit traceparent header is ever lost. When an ambient activity exists but belongs to a
    /// DIFFERENT trace than the body claims, the body is NOT trusted over the transport: the
    /// mismatch is only surfaced via an ActivityLink + tag for diagnostics.
    /// </summary>
    private static Activity? RestoreActivityFromBodyIfDetached(TaskTraceContext? traceContext)
    {
        if (string.IsNullOrEmpty(traceContext?.TraceParent) ||
            !ActivityContext.TryParse(traceContext.TraceParent, traceContext.TraceState, isRemote: true, out var bodyContext))
        {
            return null;
        }

        var ambient = Activity.Current;
        if (ambient is null)
        {
            return ActivitySource.StartActivity(
                "Execution.InvokeTask",
                ActivityKind.Server,
                parentContext: bodyContext);
        }

        if (ambient.Context.TraceId != bodyContext.TraceId)
        {
            ambient.AddLink(new ActivityLink(bodyContext));
            ambient.SetTag("vnext.trace.mismatch", true);
        }

        return null;
    }
}
