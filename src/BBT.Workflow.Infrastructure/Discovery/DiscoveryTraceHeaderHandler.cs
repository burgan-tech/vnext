using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Discovery;

/// <summary>
/// Stamps the caller's W3C trace id onto every outbound service-discovery request
/// (<see cref="DomainRegistrationService.HttpClientName"/>), so the discovery service can log and
/// query the calling runtime's <c>trace.id</c> with a plain header enricher.
/// <para>
/// Deliberately does NOT touch <c>traceparent</c>/<c>tracestate</c>: those are injected by the HTTP
/// stack's own diagnostics handler further down the pipeline, and writing them here would emit the
/// header twice (see <c>DuplicateTolerantTraceContextPropagator</c> for what that costs). This
/// handler only adds the flat, already-parsed id.
/// </para>
/// <para>
/// Fill-if-absent: a caller that set the header explicitly keeps its value. When there is no
/// ambient <see cref="Activity"/> (a background bulk-cache refresh outside any request, for
/// example) nothing is added.
/// </para>
/// </summary>
public sealed class DiscoveryTraceHeaderHandler(
    IOptions<ServiceDiscoveryOptions> serviceDiscoveryOptions) : DelegatingHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var headerName = serviceDiscoveryOptions.Value.TraceIdHeader;

        if (!string.IsNullOrWhiteSpace(headerName) && !request.Headers.Contains(headerName))
        {
            var traceId = Activity.Current?.TraceId.ToString();

            if (!string.IsNullOrEmpty(traceId))
            {
                request.Headers.TryAddWithoutValidation(headerName, traceId);
            }
        }

        return base.SendAsync(request, cancellationToken);
    }
}
