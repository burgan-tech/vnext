namespace BBT.Workflow.Remote;

/// <summary>
/// Whether a remote typed client's endpoints are safe to retry at the transport layer.
/// </summary>
/// <remarks>
/// <para>
/// The distinction lands exactly on the existing typed-client boundaries, so no per-endpoint
/// plumbing is needed: <c>IRemoteInstanceCommandAppService</c> is 12 POST/PATCH/PUT endpoints,
/// <c>IRemoteInstanceRetryAppService</c> is one, and the remaining three clients
/// (<c>IRemoteInstanceQueryAppService</c>, <c>IRemoteAuthorizeAppService</c>,
/// <c>RemoteRelatedInstanceReader</c>) are read-only.
/// </para>
/// <para>
/// Why the split cannot live in Dapr instead: a <c>Resiliency</c> target is an app-id, a domain's
/// reads and mutations share one app-id (<c>vnext-{domain}-app</c>), and <c>retry.matching</c>
/// filters by HTTP status code — there is no path or method matcher. So Dapr gets a circuit
/// breaker plus an explicit no-op default retry, and this enum owns the retry decision.
/// </para>
/// </remarks>
public enum RemoteServiceProfile
{
    /// <summary>
    /// Read-only endpoints — safe to retry, because repeating them changes nothing.
    /// </summary>
    Read = 0,

    /// <summary>
    /// Side-effecting endpoints — attempted exactly once by the transport.
    /// </summary>
    /// <remarks>
    /// A duplicate <c>instances/start</c> or <c>internal/subflow-forward</c> is data corruption,
    /// not a slow call. The failure surfaces as <c>Error.Transient("remote_network_error", …)</c>
    /// and the user-defined error boundary decides whether repeating is safe — it is the only
    /// layer that knows.
    /// </remarks>
    Mutating = 1
}
