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
/// <c>Slot</c> indicates the lifecycle hook; <c>ContextType</c> and <c>ContextKey</c>
/// identify the owning state or transition.
/// </summary>
public sealed class MonitorTaskTriggerContext
{
    /// <summary>Lifecycle hook: OnExecute, OnExit, or OnEntry.</summary>
    public string Slot { get; set; } = string.Empty;

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

    /// <summary>The final invocation result merged back into the instance data.</summary>
    public JsonElement? InvocationResult { get; set; }
}

/// <summary>Paged list of task items for an instance.</summary>
public sealed class MonitorInstanceTaskListResponse
{
    /// <summary>All tasks the instance has executed, ordered by StartedAt ascending.</summary>
    public List<MonitorTaskListItem> Items { get; set; } = [];

    /// <summary>Total number of tasks.</summary>
    public int Total { get; set; }
}
