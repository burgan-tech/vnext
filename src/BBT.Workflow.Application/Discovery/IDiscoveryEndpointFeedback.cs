namespace BBT.Workflow.Discovery;

/// <summary>
/// Receives the one signal that a cached endpoint has gone bad: something tried to reach it and
/// could not.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is what makes an expiry-free discovery cache safe.</b> The cache is invalidated by
/// events — a deployment finishing, an operator forcing a refresh — and neither covers the case
/// where a PEER domain moves while this domain has no deployment of its own. A TTL used to bound
/// that by making every entry die on a clock; this bounds it by noticing the failure instead. A
/// stale entry causes harm only while something is using it, and that is precisely the moment a
/// transport failure is observable.
/// </para>
/// <para>
/// <b>Only transport-level failures belong here.</b> Connection refused, DNS failure, unreachable
/// host — the shapes that mean "nothing is listening at this address". An HTTP 4xx or 5xx must never
/// be reported: a domain that answers with an error is a domain at the right address, and evicting
/// on it would make every downstream bug look like a discovery problem and put the registry in the
/// path of every error.
/// </para>
/// <para>
/// <b>Implementations never throw and never block the caller's error.</b> The report is made from a
/// catch block that is about to rethrow; a failure to evict must not replace the failure the caller
/// actually needs to see.
/// </para>
/// </remarks>
public interface IDiscoveryEndpointFeedback
{
    /// <summary>
    /// Reports that a cached endpoint for <paramref name="domain"/> could not be reached.
    /// </summary>
    /// <param name="domain">
    /// The domain the endpoint was resolved for. A null or blank value is ignored — an endpoint that
    /// did not record its domain cannot be mapped back to a cache entry.
    /// </param>
    /// <param name="reason">Short description of the transport failure, for the log.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ReportUnreachableAsync(string? domain, string reason, CancellationToken cancellationToken);
}

/// <summary>
/// The implementation used when there is no discovery cache to evict from.
/// </summary>
/// <remarks>
/// Registered under <c>ServiceDiscovery:Provider=dapr</c> and whenever <c>Cache:Enabled</c> is
/// false, so the report site stays a single unconditional call. The alternative — a nullable
/// dependency checked at each call site — puts the same branch in the hot path of every remote
/// transport.
/// </remarks>
public sealed class NullDiscoveryEndpointFeedback : IDiscoveryEndpointFeedback
{
    /// <inheritdoc />
    public Task ReportUnreachableAsync(string? domain, string reason, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
