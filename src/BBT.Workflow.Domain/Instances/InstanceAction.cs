using BBT.Aether;
using BBT.Aether.Auditing;
using BBT.Aether.Domain.Entities;

namespace BBT.Workflow.Instances;

public sealed class InstanceAction : Entity<Guid>, IHasCreatedAt
{
    private InstanceAction()
    {
        
    }

    public InstanceAction(
        Guid id,
        Guid taskId,
        string status,
        JsonData? detail)
        : base(id)
    {
        TaskId = taskId;
        Detail = detail ?? JsonData.Empty;
        StartedAt = DateTime.UtcNow;
        CreatedAt = DateTime.UtcNow;
        SetStatus(status);
    }

    /// <summary>
    /// Instance Task ID
    /// </summary>
    public Guid TaskId { get; private set; }
    public JsonData Detail { get; private set; }
    public DateTime StartedAt { get; private set; }
    public DateTime? FinishedAt { get; private set; }
    public TimeSpan? Duration { get; private set; }
    public string Status { get; private set; }
    public DateTime CreatedAt { get; set; }
    private void SetStatus(string status)
    {
        Status = Check.NotNullOrWhiteSpace(status, nameof(status), InstanceActionConstants.MaxStatusLength);
    }

    
}