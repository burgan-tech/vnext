using BBT.Aether;
using BBT.Aether.Auditing;
using BBT.Aether.Domain.Entities;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances;

public class InstanceJob : Entity<Guid>, IHasCreatedAt, IHasModifyTime
{
    private InstanceJob()
    {
    }

    internal InstanceJob(
        Guid id,
        JobName jobName,
        Guid jobId,
        string domain,
        string flowName,
        Guid instanceId,
        DateTimeOffset? executeAt) : base(id)
    {
        ArgumentNullException.ThrowIfNull(jobName);
        JobName = Check.NotNullOrWhiteSpace(jobName.Value, nameof(JobName), InstanceJobConstants.MaxJobNameLength);
        JobType = jobName.Type;
        SourceState = jobName.SourceState;
        TransitionKey = jobName.TransitionKey;
        JobId = jobId;
        Domain = Check.NotNullOrWhiteSpace(domain, nameof(Domain), WorkflowConstants.MaxDomainLength);
        FlowName = Check.NotNullOrWhiteSpace(flowName, nameof(FlowName), WorkflowConstants.MaxFlowLength);
        InstanceId = instanceId;
        ExecuteAt = executeAt?.UtcDateTime;
        IsActive = true;
        CreatedAt = DateTime.UtcNow;
    }

    public string JobName { get; private set; }

    /// <summary>The job kind, projected from the structured <see cref="Instances.JobName"/> for queryable resolution.</summary>
    public JobType JobType { get; private set; }

    /// <summary>
    /// The source-state key the transition fires from, projected from the job name for queryable,
    /// state-scoped cancellation. <c>null</c> for jobs without source-state scoping (timeout,
    /// long-poll-ack, state-notify) and for legacy rows.
    /// </summary>
    public string? SourceState { get; private set; }

    /// <summary>
    /// The transition key (or well-known job key) this job targets, projected from the job name.
    /// <c>null</c> for jobs without a targeted key (e.g. timeout) and for legacy rows.
    /// </summary>
    public string? TransitionKey { get; private set; }

    public Guid JobId { get; private set; }
    public string FlowName { get; private set; }
    public string Domain { get; private set; }
    public Guid InstanceId { get; private set; }

    /// <summary>
    /// The UTC instant the scheduler was armed to fire this job at, captured at scheduling time so
    /// read paths (the state function's <c>kind: "scheduled"</c> transition entries) never have to
    /// reach into the scheduler's own store. Accepted as <see cref="DateTimeOffset"/> and stored as its UTC instant,
    /// so the value is unambiguous by construction. <c>null</c> for job kinds without a resolvable
    /// single instant and for rows persisted before the column existed.
    /// </summary>
    public DateTime? ExecuteAt { get; private set; }

    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ModifiedAt { get; set; }

    /// <summary>
    /// Whether this active job owns — or is about to take — the instance's Busy flag on behalf of a
    /// transition other than <paramref name="requestingTransitionKey"/>.
    /// <para>
    /// An async-transition job is an accept intent or a per-hop chain job: it already holds Busy.
    /// A scheduled transition holds nothing while it is merely ARMED — the instance rests and the
    /// timer goes through admission itself when it fires — so it counts only once it is due
    /// (<see cref="ExecuteAt"/> reached: firing, or about to). A row without <see cref="ExecuteAt"/>
    /// predates the column; it stays an owner, as before. Counting a far-off timer as an owner made
    /// an updateData drop a satisfied auto transition in a Busy-subtype wait state whose only
    /// "owner" was its own timeout, hours away.
    /// </para>
    /// </summary>
    public bool IsLiveTransitionOwner(string requestingTransitionKey, DateTime utcNow)
    {
        if (string.Equals(TransitionKey, requestingTransitionKey, StringComparison.OrdinalIgnoreCase))
            return false;

        return JobType switch
        {
            JobType.AsyncTransition => true,
            JobType.ScheduledTransition => ExecuteAt is null || ExecuteAt <= utcNow,
            _ => false
        };
    }

    public void MarkAsProcessed()
    {
        IsActive = false;
        ModifiedAt = DateTime.UtcNow;
    }

    public static InstanceJob Create(
        Guid id,
        JobName jobName,
        Guid jobId,
        string domain,
        string flowName,
        Guid instanceId,
        DateTimeOffset? executeAt = null)
    {
        return new InstanceJob(id, jobName, jobId, domain, flowName, instanceId, executeAt);
    }
}
