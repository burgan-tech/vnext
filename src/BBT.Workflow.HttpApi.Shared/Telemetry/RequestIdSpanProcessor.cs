using System.Diagnostics;
using BBT.Aether.Tracing;
using BBT.Workflow.Logging;
using OpenTelemetry;

namespace BBT.Workflow.HttpApi.Shared.Telemetry;

/// <summary>
/// Stamps the originating request id (<see cref="TelemetryConstants.TagNames.RequestId"/>) onto
/// spans, so a trace can be filtered by the same <c>x_request_id</c> value as the logs — the span
/// counterpart of <see cref="RequestIdLogProcessor"/>.
/// <para>
/// The tag is applied in <see cref="OnStart"/> because the activity is started inside the ambient
/// correlation scope: Aether's middleware opens it for HTTP requests, and vNext's
/// <c>TransitionJobHandler</c> / <c>EventTraceScope</c> open it for the job and event entry points,
/// each before any business activity is created. <c>OnStart</c> also runs before Aether's
/// Business-profile filter makes its export decision at <c>OnEnd</c>.
/// </para>
/// <para>
/// One span is out of reach here: the ASP.NET Core server span is started by instrumentation
/// BEFORE the correlation middleware runs, so the AsyncLocal is still empty at its
/// <see cref="OnStart"/>. <c>ParentInstanceIdEnrichmentMiddleware</c> — which runs immediately
/// after <c>UseCorrelationId()</c> — tags that span instead.
/// </para>
/// </summary>
public sealed class RequestIdSpanProcessor(ICorrelationIdProvider correlationIdProvider)
    : BaseProcessor<Activity>
{
    /// <inheritdoc />
    public override void OnStart(Activity data)
    {
        if (data is null || data.GetTagItem(TelemetryConstants.TagNames.RequestId) is not null)
        {
            return;
        }

        var requestId = correlationIdProvider.Get();
        if (!string.IsNullOrEmpty(requestId))
        {
            data.SetTag(TelemetryConstants.TagNames.RequestId, requestId);
        }
    }
}
