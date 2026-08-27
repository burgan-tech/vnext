using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BBT.Aether.Auditing;
using BBT.Aether.Domain.Entities;
using BBT.Workflow.Definitions;
using TaskStatus = BBT.Workflow.Definitions.TaskStatus;

namespace BBT.Workflow.Instances;

/// <summary>
/// Represents a task execution record within a workflow instance transition.
/// Tracks both platform/infrastructure status (Status) and business outcome (BusinessStatus).
/// </summary>
public sealed class InstanceTask : Entity<Guid>, IHasCreatedAt
{
    //TODO: CreateAt Koyulacak
    private InstanceTask()
    {
    }

    public InstanceTask(
        Guid id,
        Guid transitionId,
        string taskId,
        TaskTrigger taskTrigger,
        int order) : base(id)
    {
        TransitionId = transitionId;
        TaskId = taskId;
        ExecutionKey = CreateExecutionKey(transitionId, taskId, taskTrigger, order);
        StartedAt = DateTime.UtcNow;
        CreatedAt = DateTime.UtcNow;
        Status = TaskStatus.Waiting;
        BusinessStatus = BusinessStatus.Unknown;
        Request = new JsonData("");
        Response = new JsonData("");
        InvocationResult = new JsonData("");
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
    /// Stable idempotency key for this task OCCURRENCE within the transition — not for the
    /// (transition, task) pair. The same task key can legitimately appear more than once in a
    /// single transition's <c>onExecute</c> list (e.g. run twice with different mappings), and it
    /// can separately appear under different hooks of the same transition (onExecute / onEntry /
    /// onExit) via shared task references. Hashing only (transitionId, taskId) collapsed all of
    /// those into one key, so the second occurrence's INSERT hit
    /// <c>UX_InstanceTasks_ExecutionKey</c> and faulted the instance — previously this was masked
    /// because the idempotency probe (<c>IInstanceTaskRepository.FindByTransitionAndTaskAsync</c>,
    /// implemented in Infrastructure) found the first row and both occurrences silently shared one
    /// journal row. Folding in <see cref="TaskTrigger"/> and <c>order</c> makes the key identify
    /// one occurrence: order alone is not enough since the same task key can repeat across hooks
    /// at the same order. Legacy rows (written before this change) remain null; new journal rows
    /// are protected by a filtered unique index.
    /// </summary>
    /// <remarks>
    /// Deploy-window note: rows written before this change hash only (transitionId, taskId) — the
    /// OLD shape. A task journaled before the deploy and retried after it computes a NEW-shape key,
    /// so the idempotency probe will not find the old row and inserts a second one. That is a
    /// duplicate AUDIT row for in-flight instances straddling the deploy, not a fault: the two hash
    /// shapes are computed from different source strings and cannot collide, so no 23505 results.
    /// </remarks>
    public string? ExecutionKey { get; private set; }

    /// <summary>
    /// Computes the occurrence-scoped execution key. The source string format is
    /// <c>{transitionId:N}:{taskId}:{(int)taskTrigger}:{order}</c> — stable and documented because
    /// any change to it shifts every NEW row's key relative to rows already on disk (see the
    /// deploy-window note on <see cref="ExecutionKey"/>).
    /// </summary>
    public static string CreateExecutionKey(Guid transitionId, string taskId, TaskTrigger taskTrigger, int order)
    {
        var source = Encoding.UTF8.GetBytes($"{transitionId:N}:{taskId}:{(int)taskTrigger}:{order}");
        return Convert.ToHexString(SHA256.HashData(source));
    }

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
    /// Raw invocation result captured after InvokeAsync, before output mapping (ProcessOutput).
    /// Stores TaskInvocationResult as-is: Body, Data, StatusCode, Headers, Metadata, ErrorMessage.
    /// Remains empty if the task never reached the invocation step (e.g., infra error before invoke).
    /// </summary>
    public JsonData InvocationResult { get; private set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>
    /// Sets the request payload that was sent to the task.
    /// </summary>
    /// <param name="request">The request data from InputHandler.</param>
    public void SetRequest(JsonData request)
    {
        Request = request;
    }

    /// <summary>
    /// Sets the raw invocation result before output mapping.
    /// Called after InvokeAsync succeeds, before ProcessOutputAsync transforms the data.
    /// </summary>
    /// <param name="invocationResult">The serialized TaskInvocationResult.</param>
    public void SetInvocationResult(JsonData invocationResult)
    {
        InvocationResult = invocationResult;
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
