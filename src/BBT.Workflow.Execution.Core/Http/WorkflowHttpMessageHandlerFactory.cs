using System.Net;

namespace BBT.Workflow.Execution.Core.Http;

/// <summary>
/// The one place the primary handler of the named workflow HTTP clients is built, for both hosts.
/// <para>
/// <see cref="SocketsHttpHandler"/> rather than <see cref="HttpClientHandler"/>, so the pool is
/// governed by <see cref="WorkflowHttpClientPoolSettings"/> instead of by <c>IHttpClientFactory</c>'s
/// handler lifetime: with the default two-minute rotation every warm TCP+TLS connection to the
/// gateway was thrown away twice a minute at low traffic, and the next task paid the handshake
/// again (digital-investment, 2026-11-02: first call 244 ms, warm 76 ms to the same APISIX host).
/// Register the clients with <see cref="HandlerLifetime"/> so the factory never rotates the handler;
/// <c>PooledConnectionLifetime</c> is what retires connections now, per connection, not per pool.
/// </para>
/// </summary>
public static class WorkflowHttpMessageHandlerFactory
{
    /// <summary>
    /// Handler lifetime to pass to <c>SetHandlerLifetime</c> for clients built by this factory:
    /// infinite, because connection retirement is the handler's own job here. A finite value would
    /// re-introduce the pool-wide drop this factory exists to remove.
    /// </summary>
    public static readonly TimeSpan HandlerLifetime = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// Builds the primary handler: gzip/deflate, no cookie container, pool shaped by
    /// <paramref name="settings"/>, and — for the <c>NoSslValidation</c> client — a validation
    /// callback that accepts any server certificate.
    /// </summary>
    /// <param name="settings">Pool shape, already validated by the host's options.</param>
    /// <param name="validateSsl"><c>false</c> builds the handler behind <see cref="WorkflowHttpClientNames.NoSslValidation"/>.</param>
    public static SocketsHttpHandler Create(WorkflowHttpClientPoolSettings settings, bool validateSsl)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            MaxConnectionsPerServer = settings.MaxConnectionsPerServer,
            PooledConnectionLifetime = settings.PooledConnectionLifetime,
            PooledConnectionIdleTimeout = settings.PooledConnectionIdleTimeout,
            UseCookies = false
        };

        if (!validateSsl)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        }

        return handler;
    }
}
