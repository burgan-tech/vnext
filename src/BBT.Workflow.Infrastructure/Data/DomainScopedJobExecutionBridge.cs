using BBT.Aether.BackgroundJob;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Data;

/// <summary>
/// Opens the job's <see cref="DomainScope"/> before Aether claims it, under multi-domain hosting.
/// </summary>
/// <remarks>
/// Aether's dispatcher claims the job row in <c>sys_queues.BackgroundJobs</c> in its own unit of work
/// <b>before</b> the vNext handler runs, and the Dapr job carries its payload by reference (no domain in
/// it) — so the domain travels in the job name: a multi-domain process writes
/// <c>vnext.job.v2.{domain}…</c> (<see cref="JobName"/>). The scope makes the claim, and everything after
/// it, use that domain's database. A <c>vnext.job.v1</c> name (single-domain process, or a job armed
/// before multi-domain hosting) opens no scope: the inner bridge runs exactly as before.
/// </remarks>
public sealed class DomainScopedJobExecutionBridge(IJobExecutionBridge inner) : IJobExecutionBridge
{
    /// <inheritdoc />
    public async Task ExecuteAsync(string jobName, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var domain = JobName.TryParse(jobName, out var name) ? name.Domain : null;
        using (DomainScope.Begin(domain))
        {
            await inner.ExecuteAsync(jobName, payload, cancellationToken);
        }
    }
}
