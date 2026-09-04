using BBT.Workflow.Discovery;

namespace BBT.Workflow.Remote;

/// <summary>
/// Plain-HTTP transport: the endpoint's <see cref="DiscoveryEndpoint.BaseUrl"/> plus the relative
/// path, sent through the named <see cref="HttpClient"/> that <c>AddRemoteService</c> registers
/// for <typeparamref name="TClient"/>.
/// </summary>
/// <remarks>
/// This is the pre-Dapr behaviour, byte for byte: the same <c>IHttpClientFactory</c> pipeline
/// (timeout / profile-gated retry / circuit breaker / decompression / <c>X-Internal-Operation</c>),
/// the same URI composition, the same exceptions. It is what <c>ServiceDiscovery:Provider=http</c>
/// — and a <c>DomainOverrides</c> entry of <c>url</c> — route to, which is why flipping the provider
/// back is a real rollback and not an approximation of one.
/// </remarks>
public sealed class HttpRemoteTransport<TClient> : IRemoteTransport<TClient> where TClient : class
{
    /// <summary>
    /// Named-client name for <typeparamref name="TClient"/>. Equals the name the former typed
    /// registration produced, so configuration and logging keyed on it are unaffected.
    /// </summary>
    public static string HttpClientName => typeof(TClient).Name;

    private readonly Func<HttpClient> _clientAccessor;

    /// <summary>
    /// Production constructor. A client is taken from the factory per send so handler rotation
    /// (and with it DNS refresh) keeps working; holding one <see cref="HttpClient"/> for the
    /// lifetime of this singleton would pin a single handler forever.
    /// </summary>
    public HttpRemoteTransport(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _clientAccessor = () => httpClientFactory.CreateClient(HttpClientName);
    }

    /// <summary>Test constructor: wraps one prebuilt client (typically over a stub handler).</summary>
    public HttpRemoteTransport(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _clientAccessor = () => httpClient;
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

        var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));
        var request = new HttpRequestMessage(method, requestUri);
        configure?.Invoke(request);

        return _clientAccessor().SendAsync(request, cancellationToken);
    }
}
