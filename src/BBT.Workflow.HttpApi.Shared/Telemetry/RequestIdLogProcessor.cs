using BBT.Aether.Tracing;
using BBT.Workflow.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace BBT.Workflow.HttpApi.Shared.Telemetry;

/// <summary>
/// Stamps the originating request id (<see cref="TelemetryConstants.TagNames.RequestId"/>) onto
/// EVERY log record, in every service, from <see cref="ICorrelationIdProvider"/>.
/// <para>
/// Aether's header enricher cannot do this: it reads only the CURRENT inbound request's headers,
/// so it produces nothing without an <c>HttpContext</c> (background jobs, the Outbox worker) and —
/// worse — on requests the platform originates itself (Dapr job callbacks, Dapr pub/sub deliveries)
/// Aether's correlation middleware generates an id from <c>HttpContext.TraceIdentifier</c> and
/// writes it back into the request headers, so the enricher would report a fabricated value that
/// looks exactly like a real client request id.
/// </para>
/// <para>
/// The correlation provider is the right source because it is an AsyncLocal the platform already
/// populates at every entry point: Aether's middleware from the client's <c>X-Request-Id</c>, and
/// vNext's <c>TransitionJobHandler</c> / <c>EventTraceScope</c> from the captured id carried in the
/// job payload / event contract. OpenTelemetry invokes <see cref="OnEnd"/> synchronously inside
/// <c>ILogger.Log</c>, so the ambient value is the one in effect where the log was written.
/// </para>
/// </summary>
public sealed class RequestIdLogProcessor(ICorrelationIdProvider correlationIdProvider)
    : BaseProcessor<LogRecord>
{
    /// <inheritdoc />
    public override void OnEnd(LogRecord record)
    {
        if (record is null)
        {
            return;
        }

        var requestId = correlationIdProvider.Get();
        if (string.IsNullOrEmpty(requestId))
        {
            return;
        }

        var existing = record.Attributes ?? Array.Empty<KeyValuePair<string, object?>>();
        foreach (var attribute in existing)
        {
            // A scope or an explicit log parameter already carried it — never duplicate the key.
            if (attribute.Key == TelemetryConstants.TagNames.RequestId)
            {
                return;
            }
        }

        var merged = new List<KeyValuePair<string, object?>>(existing.Count + 1);
        merged.AddRange(existing);
        merged.Add(new KeyValuePair<string, object?>(TelemetryConstants.TagNames.RequestId, requestId));
        record.Attributes = merged;
    }
}
