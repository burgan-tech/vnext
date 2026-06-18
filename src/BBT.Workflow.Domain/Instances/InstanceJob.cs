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
        Guid instanceId) : base(id)
    {
        ArgumentNullException.ThrowIfNull(jobName);
        JobName = Check.NotNullOrWhiteSpace(jobName.Value, nameof(JobName), InstanceJobConstants.MaxJobNameLength);
        JobType = jobName.Type;
        TransitionKey = jobName.Segment;
        JobId = jobId;
        Domain = Check.NotNullOrWhiteSpace(domain, nameof(Domain), WorkflowConstants.MaxDomainLength);
        FlowName = Check.NotNullOrWhiteSpace(flowName, nameof(FlowName), WorkflowConstants.MaxFlowLength);
        InstanceId = instanceId;
        IsActive = true;
        CreatedAt = DateTime.UtcNow;
    }

    public string JobName { get; private set; }

    /// <summary>The job kind, projected from the structured <see cref="Instances.JobName"/> for queryable resolution.</summary>
    public JobType JobType { get; private set; }

    /// <summary>
    /// The transition key (or well-known job key) this job targets, projected from the job name.
    /// <c>null</c> for jobs without a targeted key (e.g. timeout) and for legacy rows.
    /// </summary>
    public string? TransitionKey { get; private set; }

    public Guid JobId { get; private set; }
    public string FlowName { get; private set; }
    public string Domain { get; private set; }
    public Guid InstanceId { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ModifiedAt { get; set; }

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
        Guid instanceId)
    {
        return new InstanceJob(id, jobName, jobId, domain, flowName, instanceId);
    }
}
