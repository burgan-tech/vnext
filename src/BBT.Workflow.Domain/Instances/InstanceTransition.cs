using BBT.Aether.Auditing;
using BBT.Aether.Domain.Entities;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances;

public sealed class InstanceTransition : Entity<Guid>, ICreationAuditedObject
{
    private InstanceTransition()
    {
    }

    internal InstanceTransition(
        Guid id,
        Guid instanceId,
        string transitionId,
        string fromState,
        TriggerType triggerType,
        JsonData body,
        JsonData header
    ) : base(id)
    {
        InstanceId = instanceId;
        TransitionId = transitionId;
        FromState = fromState;
        TriggerType = triggerType;
        StartedAt = DateTime.UtcNow;
        CreatedAt = DateTime.UtcNow;
        Body = body;
        Header = header;
    }

    public Guid InstanceId { get; private set; }
    public string TransitionId { get; private set; }
    public string FromState { get; private set; }
    public string? ToState { get; private set; }
    public DateTime StartedAt { get; private set; }
    public DateTime? FinishedAt { get; private set; }
    public TimeSpan? Duration { get; private set; }

    /// <summary>
    /// Trigger type that initiated this transition. Used to resolve $PreviousUser (manual only).
    /// </summary>
    public TriggerType TriggerType { get; private set; }

    /// <summary>
    /// Created at
    /// </summary>
    public DateTime CreatedAt { get; set; }
    
    /// <summary>
    /// Creator user identifier.
    /// </summary>
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Creator behalf-of user identifier.
    /// </summary>
    public string? CreatedByBehalfOf { get; set; }

    /// <summary>
    /// Body
    /// <see cref="JsonData"/>
    /// </summary>
    public JsonData Body { get; private set; }

    /// <summary>
    /// Header
    /// <see cref="JsonData"/>
    /// </summary>
    public JsonData Header { get; private set; }
    
    public void Completed(string toState)
    {
        ToState = toState;
        FinishedAt = DateTime.UtcNow;
        Duration = FinishedAt - StartedAt;
    }

    public static InstanceTransition Create(
        Guid id,
        Guid instanceId,
        string transitionId,
        string fromState,
        TriggerType triggerType,
        JsonData body,
        JsonData header)
    {
        return new InstanceTransition(id, instanceId, transitionId, fromState, triggerType, body, header);
    }
}