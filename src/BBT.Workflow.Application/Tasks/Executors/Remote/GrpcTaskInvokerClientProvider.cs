using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Grpc;
using Dapr.Client;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Process-lifetime holder for the gRPC channel + generated client that
/// <see cref="RemoteInvokerService"/> uses for the "grpc" transport variant of
/// <c>ExecutionApi:Transport</c>.
/// <para>
/// Registered as a DI <b>singleton</b> (see <c>TaskServiceCollectionExtensions</c>) on
/// purpose: <see cref="RemoteInvokerService"/> is scoped (one instance per request), so a
/// channel built inside it would open a brand-new <c>SocketsHttpHandler</c> and HTTP/2
/// connection to the Dapr sidecar on every scope, with nothing to dispose it — under load
/// that leaks handlers/sockets and defeats connection reuse, the opposite of the point of a
/// gRPC transport. Owning the channel here means every scoped invoker shares ONE connection,
/// and the container disposes it once at process shutdown because this class is
/// <see cref="IDisposable"/> and singleton-registered.
/// </para>
/// <para>
/// Still lazy: the channel is not opened until <see cref="Client"/> is first read, so an
/// environment running the default "http" transport never constructs one — only the fields
/// this constructor reads are cheap (an app-id string from configuration).
/// </para>
/// <para>
/// Both <see cref="Lazy{T}"/> fields use <see cref="LazyThreadSafetyMode.PublicationOnly"/>
/// rather than the default <c>ExecutionAndPublication</c>: the default mode CACHES a thrown
/// exception forever, so a transient failure while building the channel (e.g. a malformed
/// <c>DAPR_GRPC_ENDPOINT</c>) would permanently poison every future "grpc" transport call for
/// the lifetime of the process, since this class is a singleton. <c>PublicationOnly</c> lets a
/// failed attempt be retried on the next access instead. Concurrent racing initializations are
/// still safe — only one published value wins — the retry-ability is the reason for this
/// choice, not thread safety, which the default mode already provides.
/// </para>
/// </summary>
public sealed class GrpcTaskInvokerClientProvider : IDisposable
{
    private readonly string _executionServiceAppId;
    private readonly Lazy<GrpcChannel> _channel;
    private readonly Lazy<TaskInvoker.TaskInvokerClient> _client;

    public GrpcTaskInvokerClientProvider(IConfiguration configuration)
    {
        _executionServiceAppId = VNextAppIds.ExecutionOrDefault(
            configuration[VNextAppIds.ConfigKeys.Execution],
            configuration[VNextAppIds.ConfigKeys.AppDomain]);

        _channel = new Lazy<GrpcChannel>(() =>
            // Dapr.Client.CreateInvocationInvoker(appId) resolves the sidecar's gRPC endpoint
            // and wires the appId/api-token interceptor for us, but it offers no hook to pass
            // GrpcChannelOptions — and without one, Grpc.Net.Client defaults
            // MaxReceiveMessageSize to 4 MB, well under what a large instance-data payload
            // needs. So the channel is built by hand here, replicating exactly what
            // CreateInvocationInvoker does internally (GrpcChannel.ForAddress +
            // InvocationInterceptor), with our own 64 MB options applied.
            GrpcChannel.ForAddress(ResolveDaprGrpcEndpoint(), new GrpcChannelOptions
            {
                // Aligned with the sidecar's http-max-request-size: "64" (MB) and the
                // server's AddGrpc limits — the three must agree or large payloads fail
                // on whichever hop has the smallest cap.
                MaxReceiveMessageSize = 64 * 1024 * 1024,
                MaxSendMessageSize = 64 * 1024 * 1024
            }),
            LazyThreadSafetyMode.PublicationOnly);

        _client = new Lazy<TaskInvoker.TaskInvokerClient>(() =>
        {
            var daprApiToken = Environment.GetEnvironmentVariable("DAPR_API_TOKEN") ?? string.Empty;
            var invoker = _channel.Value.Intercept(new InvocationInterceptor(_executionServiceAppId, daprApiToken));
            return new TaskInvoker.TaskInvokerClient(invoker);
        },
        LazyThreadSafetyMode.PublicationOnly);
    }

    /// <summary>The shared gRPC client. Building it (and the channel underneath it) is deferred to first access.</summary>
    public TaskInvoker.TaskInvokerClient Client => _client.Value;

    /// <summary>
    /// Resolves the Dapr sidecar's gRPC endpoint the same way
    /// <see cref="Dapr.Client.DaprClient.CreateInvocationInvoker(string, string?, string?)"/> does
    /// internally (<c>DAPR_GRPC_ENDPOINT</c>, else <c>localhost:DAPR_GRPC_PORT</c>, else the
    /// sidecar default port 50001) — duplicated here because that resolution logic lives on an
    /// internal type in Dapr.Common and isn't reachable from this assembly.
    /// </summary>
    private static string ResolveDaprGrpcEndpoint()
    {
        var configuredEndpoint = Environment.GetEnvironmentVariable("DAPR_GRPC_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(configuredEndpoint))
        {
            var uri = new Uri(configuredEndpoint);
            return new UriBuilder { Scheme = uri.Scheme, Host = uri.Host, Port = uri.Port }.ToString();
        }

        var portValue = Environment.GetEnvironmentVariable("DAPR_GRPC_PORT");
        var port = string.IsNullOrWhiteSpace(portValue) ? 50001 : int.Parse(portValue);
        return new UriBuilder { Scheme = "http", Host = "localhost", Port = port }.ToString();
    }

    /// <summary>Disposes the channel — and its underlying socket/handler — if one was ever built.</summary>
    public void Dispose()
    {
        if (_channel.IsValueCreated)
            _channel.Value.Dispose();
    }
}
