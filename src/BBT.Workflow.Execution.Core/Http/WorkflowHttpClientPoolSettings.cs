namespace BBT.Workflow.Execution.Core.Http;

/// <summary>
/// Connection-pool shape of the named workflow HTTP clients (<see cref="WorkflowHttpClientNames"/>),
/// host-neutral: Orchestration fills it from <c>Workflow:TaskInvocation</c>, Execution from
/// <c>WorkflowHttpClient</c>, and both hand it to <see cref="WorkflowHttpMessageHandlerFactory"/>.
/// </summary>
/// <param name="MaxConnectionsPerServer">Per-target connection cap (<c>SocketsHttpHandler.MaxConnectionsPerServer</c>).</param>
/// <param name="PooledConnectionLifetime">
/// Maximum age of a pooled connection before it is retired and re-established — how long a DNS or
/// gateway change can go unnoticed. Replaces <c>IHttpClientFactory</c>'s handler rotation, which
/// discarded the WHOLE warm pool every two minutes.
/// </param>
/// <param name="PooledConnectionIdleTimeout">
/// How long an idle connection stays in the pool. Keep it below the gateway's own keep-alive
/// timeout so the pool never offers a connection the gateway has already closed.
/// </param>
public sealed record WorkflowHttpClientPoolSettings(
    int MaxConnectionsPerServer,
    TimeSpan PooledConnectionLifetime,
    TimeSpan PooledConnectionIdleTimeout);
