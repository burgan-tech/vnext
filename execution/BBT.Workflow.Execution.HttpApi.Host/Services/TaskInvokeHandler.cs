using System.Diagnostics;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Execution.Invocation;

/// <summary>
/// Shared task-invocation logic for the Execution service. Both the HTTP controller and the
/// gRPC service front this handler so the two transports execute the exact same behavior —
/// identity-claim normalization, trace/activity restoration and tagging, logging scope, and the
/// registry invocation — instead of each transport re-implementing it and drifting apart.
/// </summary>
public sealed class TaskInvokeHandler(
    ITaskInvokerRegistry invokerRegistry,
    ILogger<TaskInvokeHandler> logger)
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
    /// <param name="request">The task invocation request with envelope and trace context.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>Task invocation response with result.</returns>
    public async Task<TaskInvokeResponse> HandleAsync(
        TaskInvokeRequest request,
        CancellationToken cancellationToken)
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

        // The ASP.NET/gRPC transaction — captured BEFORE the child span below so every
        // SetTag/SetBaggage keeps landing on the TRANSACTION document. Elastic prod queries
        // filter execution transactions by labels.vnext_task_key; letting the child span become
        // Activity.Current first would silently move those labels off the transaction.
        var activity = Activity.Current;

        // Everything from here (tagging, registry resolution, invocation, response mapping) is
        // inside one always-on span; the remaining head of the transaction is then pure
        // transport work (model binding / protobuf parse / middleware), measurable by
        // subtraction. Closes the 57.8 ms unattributed head found in trace 036088b9….
        using var handleActivity = ActivitySource.StartActivity(
            "Execution.HandleInvoke", ActivityKind.Internal);
        handleActivity?.SetTag(TelemetryConstants.TagNames.TaskKey, envelope.TaskKey);
        handleActivity?.SetTag(TelemetryConstants.TagNames.TaskType, envelope.TaskType);
        handleActivity?.SetTag(TelemetryConstants.TagNames.Layer, TelemetryConstants.Layers.Execution);
        handleActivity?.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);

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
            [TelemetryConstants.TagNames.TaskType] = envelope.TaskType

        };

        // sub / act_sub are deliberately NOT in this scope: RemoteInvokerService forwards them as
        // request headers and the log enricher (Telemetry:Logging:Enrichers:Headers, emitted
        // without a prefix) already attaches them to EVERY log record of this request — a wider
        // reach than this block. Adding them here too would put the same value on the record
        // twice, since the scope key act.sub flattens to act_sub in the log backend. They remain
        // span tags and baggage above.
        using (logger.BeginScope(scope))
        {
            var result = await invokerRegistry.InvokeAsync(envelope, cancellationToken);
            return new TaskInvokeResponse
            {
                Success = result.IsSuccess,
                ErrorMessage = result.ErrorMessage,
                Result = result,
                ExecutionDurationMs = result.ExecutionDurationMs
            };
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
