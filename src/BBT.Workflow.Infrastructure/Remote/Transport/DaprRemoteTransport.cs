using System.Net;
using System.Text.Json;
using BBT.Workflow.Discovery;
using BBT.Workflow.Execution;
using Dapr.Client;
using Polly;

namespace BBT.Workflow.Remote;

/// <summary>
/// Dapr service-invocation transport: the endpoint's <see cref="DiscoveryEndpoint.DaprAppId"/>
/// is invoked through the local sidecar over the SDK's invocation <see cref="HttpClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which SDK surface, and why this one.</b> In Dapr .NET SDK 1.17 the entire
/// <c>DaprClient.InvokeMethod*</c> family — HTTP and gRPC alike, <c>InvokeMethodWithResponseAsync</c>
/// included — carries <c>[Obsolete("Recommended guidance is to use a native HTTP or gRPC client for
/// service invocation")]</c>. The non-obsolete path the message points at is
/// <see cref="DaprClient.CreateInvokeHttpClient(string?, string?, string?)"/>: an <see cref="HttpClient"/>
/// whose <see cref="InvocationHandler"/> rewrites <c>http://{appId}/{path}</c> to the sidecar's
/// <c>/v1.0/invoke/{appId}/method/{path}</c>. The SDK still owns the sidecar contract —
/// <c>DAPR_HTTP_ENDPOINT</c>/<c>DAPR_HTTP_PORT</c> and <c>DAPR_API_TOKEN</c> are resolved by
/// <c>DaprDefaults</c> inside the handler, the token is attached per request and stripped again in
/// its <c>finally</c>. Same API <c>DaprOrchestrationForwarder</c> already uses.
/// </para>
/// <para>
/// <b>What the wire is.</b> App → sidecar is HTTP; sidecar → sidecar is gRPC + mTLS; the callee's
/// sidecar delivers HTTP to the orchestrator. gRPC on the first hop is not available for these
/// endpoints: the SDK's gRPC invocation requires Protobuf bodies and a gRPC callee, and is itself
/// obsolete.
/// </para>
/// <para>
/// <b>Query string.</b> The request is built as one absolute URI, <c>http://{appId}/{relativePath}</c>,
/// query included. <see cref="InvocationHandler"/> rewrites scheme/host/port/path through
/// <c>UriBuilder(uri)</c> and leaves the query untouched, so the values the call sites already
/// escaped arrive byte-identical on both transports. (The SDK's <c>CreateInvokeMethodRequest</c>
/// pairs overload would re-escape them — it is deliberately not used.)
/// </para>
/// <para>
/// <b>Resilience.</b> The same <see cref="RemotePolicyFactory"/> policies as the HTTP shell are
/// applied programmatically around each send, so the profile rule holds on this wire too: a
/// mutating call is attempted exactly once. A retrying policy builds a <i>fresh</i> request per
/// attempt (a sent <see cref="HttpRequestMessage"/> cannot be reused), which is why <c>configure</c>
/// runs per attempt.
/// </para>
/// <para>
/// <b>Failure normalization</b> — easy to miss, expensive to get wrong. A sidecar that cannot reach
/// the callee does not fail the socket: it answers <c>HTTP 500</c> with
/// <c>{"errorCode":"ERR_DIRECT_INVOKE",…}</c>, which <c>MapToErrorAsync</c> would classify as a
/// permanent remote 5xx. It is converted to <see cref="HttpRequestException"/> so the ~28
/// <c>catch (HttpRequestException)</c> sites keep producing <c>Error.Transient("remote_network_error", …)</c>.
/// The predicate is deliberately narrow (5xx, no <c>_aether_error_format</c> header — only a vNext
/// app emits it — and an <c>ERR_</c> code) so a genuine callee 500 still maps normally. A socket
/// failure to the sidecar is already a native <see cref="HttpRequestException"/> on this path.
/// </para>
/// </remarks>
public sealed class DaprRemoteTransport<TClient> : IDaprRemoteTransport<TClient> where TClient : class
{
    /// <summary>Largest error body inspected for a sidecar error code.</summary>
    private const int MaxInspectedErrorBodyBytes = 8 * 1024;

    private static readonly JsonSerializerOptions ErrorJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _invokeClient;
    private readonly IAsyncPolicy<HttpResponseMessage> _policy;

    /// <summary>
    /// Creates the transport.
    /// </summary>
    /// <param name="invokeClient">
    /// An invocation client from <see cref="DaprClient.CreateInvokeHttpClient(string?, string?, string?)"/>
    /// (production), or any <see cref="HttpClient"/> whose pipeline contains an
    /// <see cref="InvocationHandler"/> (tests: the real handler over a stub inner handler).
    /// </param>
    /// <param name="policy">
    /// The composed resilience policy for this client's profile
    /// (<see cref="RemotePolicyFactory.Compose"/>). Stateful (circuit breaker) — one per transport.
    /// </param>
    public DaprRemoteTransport(HttpClient invokeClient, IAsyncPolicy<HttpResponseMessage> policy)
    {
        ArgumentNullException.ThrowIfNull(invokeClient);
        ArgumentNullException.ThrowIfNull(policy);
        _invokeClient = invokeClient;
        _policy = policy;
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> SendAsync(
        DiscoveryEndpoint endpoint,
        HttpMethod method,
        string relativePath,
        Action<HttpRequestMessage>? configure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(relativePath);

        var appId = endpoint.DaprAppId;
        if (string.IsNullOrWhiteSpace(appId))
        {
            // A transport failure, not an ArgumentException: it must land in the callers'
            // Error.Transient path rather than escape as an unhandled exception.
            throw new HttpRequestException(
                $"Endpoint '{endpoint.BaseUrl}' is marked for Dapr service invocation but carries no app-id.");
        }

        return _policy.ExecuteAsync(ct => InvokeOnceAsync(appId, method, relativePath, configure, ct), cancellationToken);
    }

    private async Task<HttpResponseMessage> InvokeOnceAsync(
        string appId,
        HttpMethod method,
        string relativePath,
        Action<HttpRequestMessage>? configure,
        CancellationToken cancellationToken)
    {
        // The InvocationHandler contract (host = app-id), defined once in
        // DaprServiceInvocationClient.CreateRequest and shared with the Execution invokers. A
        // cross-namespace app-id (appid.namespace) is a valid host and reaches the sidecar path
        // verbatim, which is exactly the form Dapr splits on.
        var request = DaprServiceInvocationClient.CreateRequest(method, appId, relativePath);
        configure?.Invoke(request);

        HttpResponseMessage response;
        try
        {
            response = await _invokeClient.SendAsync(request, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            // InvocationHandler rejects a URI it cannot rewrite with ArgumentException. The URI is
            // built above so this should not happen; if it ever does it must still be a transport
            // failure to the callers, not an unhandled argument error.
            throw new HttpRequestException(
                $"Dapr service invocation of '{appId}' rejected the request URI: {ex.Message}", ex);
        }

        await ThrowIfSidecarFailureAsync(response, appId, cancellationToken);
        return response;
    }

    /// <summary>
    /// Converts a sidecar-originated failure into <see cref="HttpRequestException"/>; see the class
    /// remarks for why this exists and why the predicate is this narrow.
    /// </summary>
    private static async Task ThrowIfSidecarFailureAsync(
        HttpResponseMessage response,
        string appId,
        CancellationToken cancellationToken)
    {
        if ((int)response.StatusCode < 500)
            return;

        // Only a vNext app emits this header, so its presence proves the callee answered.
        if (response.Headers.Contains("_aether_error_format"))
            return;

        if (response.Content.Headers.ContentLength > MaxInspectedErrorBodyBytes)
            return;

        string body;
        try
        {
            // Buffer so the caller's own MapToErrorAsync can still read the body afterwards.
            await response.Content.LoadIntoBufferAsync(MaxInspectedErrorBodyBytes);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception)
        {
            // Unreadable body — fall through to the normal status-code mapping rather than guess.
            return;
        }

        var errorCode = TryReadDaprErrorCode(body);
        if (errorCode is null)
            return;

        throw new HttpRequestException(
            $"Dapr sidecar could not invoke '{appId}': {errorCode}. {body}",
            inner: null,
            statusCode: HttpStatusCode.ServiceUnavailable);
    }

    private static string? TryReadDaprErrorCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > MaxInspectedErrorBodyBytes)
            return null;

        try
        {
            var code = JsonSerializer.Deserialize<DaprSidecarError>(body, ErrorJsonOptions)?.ErrorCode;
            return !string.IsNullOrEmpty(code) && code.StartsWith("ERR_", StringComparison.Ordinal) ? code : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record DaprSidecarError
    {
        public string? ErrorCode { get; init; }
    }
}
