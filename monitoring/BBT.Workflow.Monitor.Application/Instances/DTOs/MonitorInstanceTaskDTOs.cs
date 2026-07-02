using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Monitor.Instances.DTOs;

/// <summary>Input for listing all tasks an instance has executed.</summary>
public sealed class MonitorGetInstanceTasksInput : IHasDomain
{
    /// <summary>The tenant/domain key.</summary>
    [Required]
    public string Domain { get; set; } = string.Empty;

    /// <summary>The workflow (flow) key.</summary>
    [Required]
    public string Workflow { get; set; } = string.Empty;

    /// <summary>Instance key or ID.</summary>
    [Required]
    public string Instance { get; set; } = string.Empty;
}

/// <summary>Input for retrieving a single task detail by task entity ID.</summary>
public sealed class MonitorGetInstanceTaskDetailInput : IHasDomain
{
    /// <summary>The tenant/domain key.</summary>
    [Required]
    public string Domain { get; set; } = string.Empty;

    /// <summary>The workflow (flow) key.</summary>
    [Required]
    public string Workflow { get; set; } = string.Empty;

    /// <summary>Instance key or ID.</summary>
    [Required]
    public string Instance { get; set; } = string.Empty;

    /// <summary>The unique entity ID of the task row.</summary>
    [Required]
    public Guid TaskId { get; set; }
}

/// <summary>A single task in the instance task list.</summary>
public sealed class MonitorTaskListItem
{
    /// <summary>Unique entity ID of the task row.</summary>
    public Guid Id { get; set; }

    /// <summary>Task definition key (e.g. "send-notification").</summary>
    public string TaskDefinitionKey { get; set; } = string.Empty;

    /// <summary>Execution status of the task (Waiting, Busy, Completed, Faulted).</summary>
    public string? Status { get; set; }

    /// <summary>Business-level outcome of the task (Unknown, Success, Failed).</summary>
    public string? BusinessStatus { get; set; }

    /// <summary>When the task started execution.</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>When the task finished execution.</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>Wall-clock duration in milliseconds, or null if the task has not finished.</summary>
    public long? DurationMs { get; set; }
}

/// <summary>
/// Where in the workflow definition the task was placed.
/// <c>TriggerLocation</c> indicates the lifecycle hook; <c>ContextType</c> and <c>ContextKey</c>
/// identify the owning state or transition.
/// </summary>
public sealed class MonitorTaskTriggerContext
{
    /// <summary>Lifecycle hook where the task was triggered: OnExecute, OnExit, or OnEntry.</summary>
    public string TriggerLocation { get; set; } = string.Empty;

    /// <summary>
    /// Whether <c>ContextKey</c> refers to a state or a transition.
    /// Values: "State" | "Transition".
    /// </summary>
    public string ContextType { get; set; } = string.Empty;

    /// <summary>
    /// The key of the owning element: a state key when <c>ContextType</c> is "State"
    /// (OnExit → fromState, OnEntry → toState), or a transition key when "Transition" (OnExecute).
    /// </summary>
    public string ContextKey { get; set; } = string.Empty;

    /// <summary>0-based position of this task within its slot.</summary>
    public int Order { get; set; }

    /// <summary>Optional data-mapping script configured on the slot entry.</summary>
    public string? MappingScript { get; set; }
}

/// <summary>Summary of the task definition looked up from the component cache.</summary>
public sealed class MonitorTaskDefinitionInfo
{
    /// <summary>Task definition key.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Human-readable task type discriminator (e.g. "Http", "Script", "Dapr").</summary>
    public string? Type { get; set; }

    /// <summary>The definition version the task was resolved from.</summary>
    public string? Version { get; set; }

    /// <summary>The task's raw configuration block from the definition.</summary>
    public JsonElement? Config { get; set; }
}

/// <summary>Full detail of a single task execution, including definition info, trigger context, and payloads.</summary>
public sealed class MonitorTaskDetailResponse
{
    /// <summary>Unique entity ID of the task row.</summary>
    public Guid Id { get; set; }

    /// <summary>Task definition key.</summary>
    public string TaskDefinitionKey { get; set; } = string.Empty;

    /// <summary>Execution status of the task.</summary>
    public string? Status { get; set; }

    /// <summary>Business-level outcome of the task.</summary>
    public string? BusinessStatus { get; set; }

    /// <summary>When the task started execution.</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>When the task finished execution.</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>Wall-clock duration in milliseconds.</summary>
    public long? DurationMs { get; set; }

    /// <summary>Where in the transition lifecycle this task sits (best-effort; null if the definition version is unavailable).</summary>
    public MonitorTaskTriggerContext? TriggerContext { get; set; }

    /// <summary>Task definition metadata looked up from the component cache (best-effort; null if unavailable).</summary>
    public MonitorTaskDefinitionInfo? Definition { get; set; }

    /// <summary>The input payload sent to the task executor.</summary>
    public JsonElement? Input { get; set; }

    /// <summary>The raw output returned by the task executor.</summary>
    public JsonElement? Output { get; set; }

    /// <summary>GUID of another InstanceTask that caused this task to fault (fault cascade), or null.</summary>
    public Guid? FaultedByTaskId { get; set; }

    /// <summary>
    /// Structured error info. Always present — fields are populated when Status=Faulted,
    /// BusinessStatus=Failed, or InvocationResult.isSuccess=false. Empty object when no error.
    /// </summary>
    public MonitorTaskErrorInfo Error { get; set; } = new();

    /// <summary>
    /// Structured invocation result (HTTP/Dapr/script execution metadata).
    /// Null when the task never reached the invocation step (e.g. mapping error).
    /// </summary>
    public MonitorTaskInvocationResult? InvocationResult { get; set; }

    /// <summary>Execution sub-steps from the InstanceActions table. Empty array when no actions are recorded.</summary>
    public List<MonitorTaskActionItem> Actions { get; set; } = [];
}

/// <summary>Paged list of task items for an instance.</summary>
public sealed class MonitorInstanceTaskListResponse
{
    /// <summary>All tasks the instance has executed, ordered by StartedAt ascending.</summary>
    public List<MonitorTaskListItem> Items { get; set; } = [];

    /// <summary>Total number of tasks.</summary>
    public int Total { get; set; }
}

/// <summary>
/// Structured error block. Non-null when <see cref="MonitorTaskDetailResponse.Status"/> is Faulted
/// or <see cref="MonitorTaskDetailResponse.BusinessStatus"/> is Failed.
/// Populated from InvocationResult metadata (invocation errors) or from
/// Response.error string (mapping errors — when InvokeAsync was never reached).
/// </summary>
public sealed class MonitorTaskErrorInfo
{
    /// <summary>Human-readable error message.</summary>
    public string? Message { get; set; }

    /// <summary>
    /// .NET exception type name (e.g. "HttpRequestException", "NullReferenceException").
    /// Null for business-logic failures or when metadata is unavailable.
    /// </summary>
    public string? ExceptionType { get; set; }

    /// <summary>Full .NET stack trace. Null for business-logic failures or mapping errors.</summary>
    public string? StackTrace { get; set; }
}

/// <summary>Structured invocation result — HTTP/execution metadata only, no error duplication.</summary>
public sealed class MonitorTaskInvocationResult
{
    /// <summary>Whether the invocation itself succeeded (independent of business outcome).</summary>
    public bool IsSuccess { get; set; }

    /// <summary>HTTP status code returned by the task endpoint, or null if not applicable.</summary>
    public int? StatusCode { get; set; }

    /// <summary>Execution duration in milliseconds as reported by the invoker.</summary>
    public long? ExecutionDurationMs { get; set; }

    /// <summary>Response headers from the task endpoint, or null.</summary>
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>
    /// Parsed response data from the task endpoint.
    /// Presented as a JSON object/array when the body is valid JSON;
    /// as a JSON string when the body is plain text.
    /// Null when no body was returned.
    /// </summary>
    public JsonElement? Body { get; set; }
}

/// <summary>One execution sub-step of a task, from the InstanceActions table.</summary>
public sealed class MonitorTaskActionItem
{
    /// <summary>Action entity GUID.</summary>
    public Guid Id { get; set; }

    /// <summary>Step status: Pending, Processing, Completed, Failed, Cancelled.</summary>
    public string? Status { get; set; }

    /// <summary>UTC start of this step.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>UTC end of this step, or null if not finished.</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>Step duration in milliseconds, or null if not finished.</summary>
    public double? DurationMs { get; set; }

    /// <summary>Step-specific JSON payload (parameters, result, metadata).</summary>
    public JsonElement? Detail { get; set; }
}
