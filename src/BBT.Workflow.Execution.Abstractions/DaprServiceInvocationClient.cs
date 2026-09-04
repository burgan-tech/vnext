namespace BBT.Workflow.Execution;

/// <summary>
/// The one Dapr service-invocation surface the runtime uses: an <see cref="HttpClient"/> produced
/// by <c>DaprClient.CreateInvokeHttpClient()</c>, plus the request shape its
/// <c>InvocationHandler</c> expects.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> In Dapr .NET SDK 1.17 the entire <c>DaprClient.InvokeMethod*</c> family
/// — <c>InvokeMethodAsync</c>, <c>InvokeMethodWithResponseAsync</c>, <c>InvokeMethodGrpcAsync</c>,
/// every overload — is <c>[Obsolete("Recommended guidance is to use a native HTTP or gRPC client
/// for service invocation")]</c>. The non-obsolete surface the message points at is
/// <c>DaprClient.CreateInvokeHttpClient()</c>: an <see cref="HttpClient"/> whose
/// <c>InvocationHandler</c> rewrites an absolute <c>http://{appId}/{path}</c> to the sidecar's
/// <c>{DAPR_HTTP_ENDPOINT}/v1.0/invoke/{appId}/method/{path}</c>, resolving
/// <c>DAPR_HTTP_ENDPOINT</c>/<c>DAPR_HTTP_PORT</c>/<c>DAPR_API_TOKEN</c> itself and attaching the
/// token per request. Nine call sites (the Execution invokers and the orchestration → execution
/// invoker) used the obsolete family; this type is what they were moved onto, so the SDK's contract
/// with the sidecar is expressed in exactly one place.
/// </para>
/// <para>
/// <b>The request contract</b> is <see cref="CreateRequest"/>: host = app-id, path = method name.
/// A cross-namespace app-id (<c>appid.namespace</c>) and an <c>HTTPEndpoint</c> resource name are
/// both valid hosts and reach the sidecar path verbatim — the same strings the obsolete
/// <c>CreateInvokeMethodRequest</c> placed there. The query string is left untouched by the
/// handler (it rewrites through <c>UriBuilder(uri)</c>), so a method name that already carries
/// <c>?a=b</c> arrives byte-identical; the obsolete <c>queryStringParameters</c> overload would have
/// re-escaped it.
/// </para>
/// <para>
/// <b>Behaviour parity with the obsolete path.</b> <see cref="SendAsync"/> performs no status
/// validation — every 2xx/4xx/5xx comes back as a response, exactly like
/// <c>InvokeMethodWithResponseAsync</c> did — so task invokers keep handing full responses to
/// output mapping. A socket failure to the sidecar surfaces as a native
/// <see cref="HttpRequestException"/>; the obsolete path wrapped it in <c>InvocationException</c>,
/// and no caller depended on that type. A URI the handler cannot rewrite is an
/// <see cref="ArgumentException"/> from the SDK — unreachable when <see cref="CreateRequest"/> built it.
/// </para>
/// <para>
/// Lives in <c>Execution.Abstractions</c> because both consumers reference it and it needs
/// nothing from the Dapr package: the SDK dependency sits only where the client is constructed
/// (<c>DaprClient.CreateInvokeHttpClient()</c> at DI registration). Register one instance per host
/// as a singleton — it targets the local sidecar, so handler rotation for DNS refresh is not a concern.
/// </para>
/// </remarks>
public sealed class DaprServiceInvocationClient
{
    private readonly HttpClient _invokeClient;

    /// <summary>
    /// Wraps an invocation client.
    /// </summary>
    /// <param name="invokeClient">
    /// Production: <c>DaprClient.CreateInvokeHttpClient()</c>. Tests: an <see cref="HttpClient"/> over
    /// the SDK's <c>InvocationHandler</c> with a stub inner handler, which exercises the real rewrite
    /// without a network.
    /// </param>
    public DaprServiceInvocationClient(HttpClient invokeClient)
    {
        ArgumentNullException.ThrowIfNull(invokeClient);
        _invokeClient = invokeClient;
    }

    /// <summary>
    /// Builds the request the invocation client expects: <c>{method} http://{appId}/{methodName}</c>.
    /// </summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="appId">Target app-id (optionally <c>appid.namespace</c>) or HTTPEndpoint name.</param>
    /// <param name="methodName">Path on the target, optionally with a query string. A leading slash is tolerated.</param>
    public static HttpRequestMessage CreateRequest(HttpMethod method, string appId, string methodName)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentNullException.ThrowIfNull(methodName);

        return new HttpRequestMessage(method, new Uri($"http://{appId}/{methodName.TrimStart('/')}"));
    }

    /// <summary>
    /// Sends the request through the sidecar and returns the full response without validating its
    /// status code.
    /// </summary>
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _invokeClient.SendAsync(request, cancellationToken);
    }
}
