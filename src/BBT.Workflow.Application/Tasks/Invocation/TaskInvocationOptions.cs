using System.ComponentModel.DataAnnotations;

namespace BBT.Workflow.Tasks.Invocation;

/// <summary>
/// Host-level task invocation routing. Bound from configuration section
/// "Workflow:TaskInvocation". Decides whether a task's prepared binding is executed
/// in-process (Orchestration) or shipped to the Execution service.
/// </summary>
/// <remarks>
/// Per-type keys are the wire task-type constants from
/// <see cref="BBT.Workflow.Execution.TaskTypes"/> ("http", "daprservice", "soap",
/// "statestore"), matched case-insensitively. A type with no local invoker
/// registered resolves to Remote regardless of configuration — see
/// <see cref="TaskInvocationRouter"/>.
/// </remarks>
public sealed class TaskInvocationOptions
{
    public const string SectionName = "Workflow:TaskInvocation";

    /// <summary>
    /// Mode applied when no per-type entry matches. Ships as Remote so enabling the local
    /// path is an explicit, reversible operator decision.
    /// </summary>
    public ExecutionMode DefaultMode { get; set; } = ExecutionMode.Remote;

    /// <summary>
    /// Per-task-type overrides, keyed by wire task type.
    /// </summary>
    public Dictionary<string, ExecutionMode> Modes { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-target connection cap (<c>SocketsHttpHandler.MaxConnectionsPerServer</c>) for the named
    /// HTTP clients shared by every in-process HTTP and SOAP task and by the (deprecated) type-22
    /// External HTTP task. Before issue #1007 this bounded only type-22 egress; it now bounds all
    /// HTTP/SOAP egress the orchestrator performs, so it is an operator dial rather than a
    /// hardcoded constant. 50 is the shipped default — the previous hardcoded value was 10. The
    /// Execution host's own named clients read the same dial from <c>WorkflowHttpClient</c>
    /// (<c>BBT.Workflow.Execution.Configuration.WorkflowHttpClientOptions</c>, same default).
    /// <c>WorkflowInfrastructureModuleServiceCollectionExtensions.AddExternalHttpTaskClients</c>
    /// resolves this property lazily through <see cref="IServiceProvider"/> rather than reading raw
    /// configuration a second time, since it registers before the container that would bind this
    /// options object is built.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MaxConnectionsPerServer { get; set; } = 50;

    /// <summary>
    /// Maximum age of a pooled connection of those same named clients
    /// (<c>SocketsHttpHandler.PooledConnectionLifetime</c>). The handler is no longer rotated by
    /// <c>IHttpClientFactory</c> — that rotation dropped the whole warm pool every two minutes and
    /// re-paid TCP+TLS to the gateway on the next task — so this is what bounds how long a DNS or
    /// gateway change goes unnoticed. See <c>WorkflowHttpMessageHandlerFactory</c>.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int PooledConnectionLifetimeSeconds { get; set; } = 600;

    /// <summary>
    /// Idle retention of a pooled connection (<c>SocketsHttpHandler.PooledConnectionIdleTimeout</c>).
    /// Keep it below the gateway's keep-alive timeout, or the pool offers connections the gateway
    /// has already closed.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int PooledConnectionIdleTimeoutSeconds { get; set; } = 120;

    /// <summary>Projects the three pool dials onto the host-neutral shape the handler factory takes.</summary>
    public BBT.Workflow.Execution.Core.Http.WorkflowHttpClientPoolSettings ToHttpClientPoolSettings() => new(
        MaxConnectionsPerServer,
        TimeSpan.FromSeconds(PooledConnectionLifetimeSeconds),
        TimeSpan.FromSeconds(PooledConnectionIdleTimeoutSeconds));

    /// <summary>
    /// Per-invocation deadline <see cref="TaskInvocationDispatcher"/> applies around every
    /// in-process (local) task call — the local-path counterpart of the remote path's
    /// <c>ExecutionApi:InvocationTimeoutSeconds</c> (see <c>RemoteInvokerService</c>). Only
    /// <c>HttpTaskBinding</c> and <c>SoapTaskBinding</c> carry their own <c>timeoutSeconds</c>
    /// field; <c>DaprServiceBinding</c> and <c>StateStoreBinding</c> have
    /// none, so without this dial those two types had NO deadline at all on the local path — not
    /// even the job budget behind a <c>sync=true</c> transition. Applied uniformly to every local
    /// invoker regardless of task type, on top of whichever per-task-binding timeout also applies.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int LocalInvocationTimeoutSeconds { get; set; } = 60;
}
