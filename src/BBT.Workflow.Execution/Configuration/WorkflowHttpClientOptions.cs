using System.ComponentModel.DataAnnotations;
using BBT.Workflow.Execution.Core.Http;

namespace BBT.Workflow.Execution.Configuration;

/// <summary>
/// Connection-pool settings of the Execution host's named workflow HTTP clients
/// (<see cref="WorkflowHttpClientNames"/>, the remote path of <c>http</c>/<c>soap</c> tasks). Bound
/// from configuration section <c>WorkflowHttpClient</c>. The Orchestration host's clients read the
/// same three dials from <c>Workflow:TaskInvocation</c> (<c>TaskInvocationOptions</c>); the defaults
/// are kept equal so a task behaves the same whichever host performs the call.
/// </summary>
public sealed class WorkflowHttpClientOptions
{
    public const string SectionName = "WorkflowHttpClient";

    /// <summary>
    /// Per-target connection cap. Was a hardcoded <c>10</c> on this host while Orchestration moved to
    /// <c>50</c> (issue #1007); every bank call goes to one APISIX host, so under load 10 was the
    /// ceiling for the whole Execution pod. Same default as Orchestration now; operator-tunable.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MaxConnectionsPerServer { get; set; } = 50;

    /// <summary>Maximum age of a pooled connection; see <see cref="WorkflowHttpClientPoolSettings"/>.</summary>
    [Range(1, int.MaxValue)]
    public int PooledConnectionLifetimeSeconds { get; set; } = 600;

    /// <summary>Idle retention of a pooled connection; see <see cref="WorkflowHttpClientPoolSettings"/>.</summary>
    [Range(1, int.MaxValue)]
    public int PooledConnectionIdleTimeoutSeconds { get; set; } = 120;

    /// <summary>Projects the bound values onto the host-neutral pool shape.</summary>
    public WorkflowHttpClientPoolSettings ToPoolSettings() => new(
        MaxConnectionsPerServer,
        TimeSpan.FromSeconds(PooledConnectionLifetimeSeconds),
        TimeSpan.FromSeconds(PooledConnectionIdleTimeoutSeconds));
}
