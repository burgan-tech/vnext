using System.Text.Json;
using BBT.Aether.Domain.Entities;
using BBT.Workflow.Definitions;
using TaskStatus = BBT.Workflow.Definitions.TaskStatus;

namespace BBT.Workflow.Instances;

/// <summary>
/// Represents a task execution record within a workflow instance transition.
/// Tracks both platform/infrastructure status (Status) and business outcome (BusinessStatus).
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
        BusinessStatus = BusinessStatus.Unknown;
        Request = new JsonData("");
        Response = new JsonData("");
    }

    /// <summary>
    /// Instance Transition ID
    /// </summary>
    public Guid TransitionId { get; private set; }

    /// <summary>
    /// The task definition key/ID.
    /// </summary>
    public string TaskId { get; private set; }

    /// <summary>
    /// Platform/infrastructure execution status.
    /// Indicates whether the task was successfully invoked by the platform.
    /// </summary>
    public TaskStatus Status { get; private set; }

    /// <summary>
    /// Business-level outcome status.
    /// Indicates the business result of the task execution.
    /// Separate from Status to distinguish platform success from business success.
    /// </summary>
    /// <remarks>
    /// - Success: StandardTaskResponse.IsSuccess = true
    /// - Failed: StandardTaskResponse.IsSuccess = false (e.g., HTTP 4xx/5xx)
    /// - Unknown: Task not yet completed or infrastructure error
    /// </remarks>
    public BusinessStatus BusinessStatus { get; private set; } = BusinessStatus.Unknown;

    public DateTime StartedAt { get; private set; }
    public DateTime? FinishedAt { get; private set; }
    public TimeSpan? Duration { get; private set; }
    public Guid? FaultedTaskId { get; private set; }

    /// <summary>
    /// Request payload sent to the task.
    /// <see cref="JsonData"/>
    /// </summary>
    public JsonData Request { get; private set; }

    /// <summary>
    /// Response payload received from the task.
    /// <see cref="JsonData"/>
    /// </summary>
    public JsonData Response { get; private set; }

    /// <summary>
    /// Sets the request payload that was sent to the task.
    /// </summary>
    /// <param name="request">The request data from InputHandler.</param>
    public void SetRequest(JsonData request)
    {
        Request = request;
    }

    /// <summary>
    /// Marks the task as completed with business success.
    /// Platform successfully invoked the task and business logic succeeded.
    /// </summary>
    /// <param name="response">The response data from the task.</param>
    public void Completed(JsonData response)
    {
        Completed(response, isBusinessSuccess: true);
    }

    /// <summary>
    /// Marks the task as completed with explicit business status.
    /// Platform successfully invoked the task, business outcome specified separately.
    /// </summary>
    /// <param name="response">The response data from the task.</param>
    /// <param name="isBusinessSuccess">Whether the business logic succeeded (StandardTaskResponse.IsSuccess).</param>
    public void Completed(JsonData response, bool isBusinessSuccess)
    {
        FinishedAt = DateTime.UtcNow;
        Duration = FinishedAt - StartedAt;
        Status = TaskStatus.Completed;
        Response = response;
        BusinessStatus = isBusinessSuccess ? BusinessStatus.Success : BusinessStatus.Failed;
    }

    /// <summary>
    /// Marks the task as faulted due to infrastructure/platform error.
    /// The task could not be invoked or completed due to an error.
    /// BusinessStatus remains Unknown since business logic was not executed.
    /// </summary>
    /// <param name="reason">The error reason.</param>
    public void Faulted(string reason)
    {
        FinishedAt = DateTime.UtcNow;
        Duration = FinishedAt - StartedAt;
        Status = TaskStatus.Faulted;
        BusinessStatus = BusinessStatus.Unknown;
        Response = new JsonData(JsonSerializer.Serialize(new { error = reason }));
    }

    /// <summary>
    /// Indicates whether this task completed with business success.
    /// </summary>
    public bool IsBusinessSuccess => Status == TaskStatus.Completed && BusinessStatus == BusinessStatus.Success;

    /// <summary>
    /// Indicates whether this task completed with business failure.
    /// </summary>
    public bool IsBusinessFailed => Status == TaskStatus.Completed && BusinessStatus == BusinessStatus.Failed;
}
