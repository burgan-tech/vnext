using System.Text.Json;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Results;
using TaskStatus = BBT.Workflow.Definitions.TaskStatus;

namespace BBT.Workflow.Instances;

/// <summary>
/// Instance Task
/// </summary>
public sealed class InstanceTask : Entity<Guid>
{
    private InstanceTask()
    {
    }

    public InstanceTask(
        Guid id,
        Guid transitionId,
        string taskId) : base(id)
    {
        TransitionId = transitionId;
        TaskId = taskId;
        StartedAt = DateTime.UtcNow;
        Status = TaskStatus.Waiting;
        Request = new JsonData("");
        Response = new JsonData("");
    }

    /// <summary>
    /// Instance Transition ID
    /// </summary>
    public Guid TransitionId { get; private set; }

    public string TaskId { get; private set; }
    public TaskStatus Status { get; private set; }
    public DateTime StartedAt { get; private set; }
    public DateTime? FinishedAt { get; private set; }
    public TimeSpan? Duration { get; private set; }
    public Guid? FaultedTaskId { get; private set; }

    /// <summary>
    /// Request
    /// <see cref="JsonData"/>
    /// </summary>
    public JsonData Request { get; private set; }

    /// <summary>
    /// Response
    /// <see cref="JsonData"/>
    /// </summary>
    public JsonData Response { get; private set; }

    public void Completed(JsonData response)
    {
        FinishedAt = DateTime.UtcNow;
        Status = TaskStatus.Completed;
        Response = response;
    }

    public void Faulted(string reason)
    {
        FinishedAt = DateTime.UtcNow;
        Status = TaskStatus.Faulted;
        Response = new JsonData(JsonSerializer.Serialize(new { error = reason }));
    }
    
    public void Faulted(Error error)
    {
        FinishedAt = DateTime.UtcNow;
        Status = TaskStatus.Faulted;
        Response = new JsonData(JsonSerializer.Serialize(new { error }));
    }
}