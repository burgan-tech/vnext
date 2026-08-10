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
        SourceState = jobName.SourceState;
        TransitionKey = jobName.TransitionKey;
        JobId = jobId;
        Domain = Check.NotNullOrWhiteSpace(domain, nameof(Domain), WorkflowConstants.MaxDomainLength);
        FlowName = Check.NotNullOrWhiteSpace(flowName, nameof(FlowName), WorkflowConstants.MaxFlowLength);
        InstanceId = instanceId;
        IsActive = true;
        DispatchStatus = InstanceJobDispatchStatus.Scheduled;
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
    public bool IsActive { get; private set; }

    /// <summary>The durable delivery/execution state.</summary>
    public InstanceJobDispatchStatus DispatchStatus { get; private set; }

    /// <summary>
    /// Serialized transition payload retained while the job is active so dispatch/recovery does
    /// not depend on the original HTTP request still being alive.
    /// </summary>
    public string? Payload { get; private set; }

    /// <summary>Token that owns the instance Busy reservation.</summary>
    public Guid? AdmissionToken { get; private set; }

    /// <summary>Instance revision observed when the job was admitted.</summary>
    public long? AdmittedRevision { get; private set; }

    public string? IdempotencyKey { get; private set; }
    public string? RequestFingerprint { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTime? NextAttemptAt { get; private set; }
    public DateTime? ProcessingAt { get; private set; }
    public DateTime? ProcessingLeaseUntil { get; private set; }
    /// <summary>
    /// Fencing token for the delivery that currently owns the processing lease. A new token is
    /// written on every successful claim; terminal updates must match it so a stale worker cannot
    /// complete, fail or supersede a newer claimant.
    /// </summary>
    public Guid? ProcessingToken { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? ErrorDetails { get; private set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ModifiedAt { get; set; }

    public void MarkAsProcessed()
    {
        IsActive = false;
        DispatchStatus = InstanceJobDispatchStatus.Completed;
        ProcessingLeaseUntil = null;
        ProcessingToken = null;
        Payload = null;
        ModifiedAt = DateTime.UtcNow;
    }

    public void MarkAsScheduled()
    {
        if (!IsActive)
            return;

        DispatchStatus = InstanceJobDispatchStatus.Scheduled;
        NextAttemptAt = null;
        ProcessingToken = null;
        ModifiedAt = DateTime.UtcNow;
    }

    public void MarkAsFailed(string errorCode, string? errorDetails = null)
    {
        IsActive = false;
        DispatchStatus = InstanceJobDispatchStatus.Failed;
        ErrorCode = Check.Length(errorCode, nameof(errorCode), InstanceJobConstants.MaxErrorCodeLength);
        ErrorDetails = errorDetails;
        ProcessingLeaseUntil = null;
        ProcessingToken = null;
        Payload = null;
        ModifiedAt = DateTime.UtcNow;
    }

    public void MarkAsSuperseded(string? errorDetails = null)
    {
        IsActive = false;
        DispatchStatus = InstanceJobDispatchStatus.Superseded;
        ErrorDetails = errorDetails;
        ProcessingLeaseUntil = null;
        ProcessingToken = null;
        Payload = null;
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

    /// <summary>
    /// Creates the durable intent for an admitted transition. It starts as pending and is marked
    /// scheduled in the same unit of work after the enqueue gateway accepts the delivery intent.
    /// </summary>
    public static InstanceJob CreateTransitionAdmission(
        Guid id,
        JobName jobName,
        Guid jobId,
        string domain,
        string flowName,
        Guid instanceId,
        string payload,
        Guid admissionToken,
        long? admittedRevision,
        string? idempotencyKey = null,
        string? requestFingerprint = null)
    {
        var job = new InstanceJob(id, jobName, jobId, domain, flowName, instanceId)
        {
            DispatchStatus = InstanceJobDispatchStatus.PendingDispatch,
            Payload = Check.NotNullOrWhiteSpace(payload, nameof(payload)),
            AdmissionToken = admissionToken,
            AdmittedRevision = admittedRevision,
            IdempotencyKey = Check.Length(
                idempotencyKey,
                nameof(idempotencyKey),
                InstanceJobConstants.MaxIdempotencyKeyLength),
            RequestFingerprint = Check.Length(
                requestFingerprint,
                nameof(requestFingerprint),
                InstanceJobConstants.MaxRequestFingerprintLength)
        };

        return job;
    }
}
