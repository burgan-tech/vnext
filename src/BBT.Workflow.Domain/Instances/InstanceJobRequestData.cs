using BBT.Aether.Domain.Entities;

namespace BBT.Workflow.Instances;

/// <summary>
/// The request body of an async transition accept, stored in its OWN table so it is never loaded
/// by a metadata read of <see cref="InstanceJob"/>.
/// <para>
/// An oversized async transition body cannot travel in the Dapr job payload — the scheduler's
/// transport (etcd) refuses messages over its ceiling (2 MiB by default) at arm time, after the
/// Busy flip and the job row have committed, which left the instance durably Busy (finding AB-17,
/// vnext-client-sdk-core#58). The body is persisted here at accept and the payload carries a
/// reference (<see cref="BBT.Workflow.BackgroundJobs.Payloads.TransitionJobPayload.JobId"/> +
/// <c>DataInJobRow</c>); the job handler reads it back by <see cref="Id"/>.
/// </para>
/// <para>
/// A separate table, deliberately: <see cref="InstanceJob"/> is read metadata-only on hot paths
/// (the state function's scheduled-transition listing, the updateData continuation handoff,
/// cancellation), so the multi-MB body must not sit on a column those reads would transfer. Rows
/// exist only for bodies large enough to be offloaded (see
/// <c>WorkflowExecutionOptions.AsyncTransitionInlineBodyMaxBytes</c>); small bodies stay inline in
/// the payload and never create one.
/// </para>
/// </summary>
public class InstanceJobRequestData : Entity<Guid>
{
    private InstanceJobRequestData()
    {
    }

    private InstanceJobRequestData(Guid jobId, JsonData data) : base(jobId)
    {
        Data = data;
    }

    /// <summary>The request body (jsonb). Never null on a persisted row.</summary>
    public JsonData Data { get; private set; } = JsonData.Empty;

    /// <summary>
    /// Creates a row keyed by the job's <see cref="InstanceJob.JobId"/> (which equals the
    /// <see cref="InstanceJob.Id"/> for transition jobs — both are the same generated guid).
    /// </summary>
    public static InstanceJobRequestData Create(Guid jobId, JsonData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new InstanceJobRequestData(jobId, data);
    }
}
