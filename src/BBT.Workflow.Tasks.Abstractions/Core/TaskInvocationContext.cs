using System;
using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Tasks;

/// <summary>
/// Unified context for task invocation.
/// Contains all information needed by any invoker (local, remote, or custom).
/// Uses optional properties to support different execution modes.
/// </summary>
[Obsolete("Superseded by the ITaskInvocationRouter / ITaskInvocationDispatcher seam (see docs/runtime/task-invocation-routing.md). This type was never implemented or consumed and is kept only so the published package stays source- and binary-compatible; it will be removed in a later release — see vnext-meta/deprecations.json.")]
public sealed record TaskInvocationContext
{
    /// <summary>
    /// Task type identifier (e.g., "Http", "DaprService", "Script").
    /// </summary>
    public required string TaskType { get; init; }
    
    /// <summary>
    /// Task key for logging and tracing.
    /// </summary>
    public required string TaskKey { get; init; }
    
    /// <summary>
    /// Raw task binding as JSON (for remote/stateless invocation).
    /// Used when the task is executed in a separate process.
    /// </summary>
    public JsonElement? Binding { get; init; }
    
    /// <summary>
    /// Rich workflow task (for local/stateful invocation).
    /// Used when the task is executed in the same process with full domain context.
    /// </summary>
    public WorkflowTask? Task { get; init; }
    
    /// <summary>
    /// Script mapping code (for local invocation with mapping).
    /// </summary>
    public ScriptCode? Mapping { get; init; }
    
    /// <summary>
    /// Script context with workflow state (for local invocation).
    /// </summary>
    public ScriptContext? ScriptContext { get; init; }
    
    /// <summary>
    /// Trace context for distributed tracing.
    /// </summary>
    public TaskTraceContext? TraceContext { get; init; }
    
    /// <summary>
    /// Creates context for local invocation with full domain context.
    /// </summary>
    /// <param name="task">The workflow task to execute.</param>
    /// <param name="mapping">The script mapping code.</param>
    /// <param name="scriptContext">The script context with workflow state.</param>
    /// <returns>A context configured for local invocation.</returns>
    public static TaskInvocationContext ForLocal(
        WorkflowTask task,
        ScriptCode mapping,
        ScriptContext scriptContext)
    {
        return new TaskInvocationContext
        {
            TaskType = task.GetTaskType().ToString(),
            TaskKey = task.Key,
            Task = task,
            Mapping = mapping,
            ScriptContext = scriptContext,
            TraceContext = TaskTraceContext.Create(
                scriptContext.Instance.Id,
                scriptContext.Workflow.Domain,
                scriptContext.Workflow.Key,
                scriptContext.Workflow.Version)
        };
    }
    
    /// <summary>
    /// Creates context for remote invocation with serialized binding.
    /// </summary>
    /// <param name="taskType">The task type identifier.</param>
    /// <param name="taskKey">The task key for logging.</param>
    /// <param name="binding">The serialized task binding.</param>
    /// <param name="traceContext">Optional trace context for distributed tracing.</param>
    /// <returns>A context configured for remote invocation.</returns>
    public static TaskInvocationContext ForRemote(
        string taskType,
        string taskKey,
        JsonElement binding,
        TaskTraceContext? traceContext = null)
    {
        return new TaskInvocationContext
        {
            TaskType = taskType,
            TaskKey = taskKey,
            Binding = binding,
            TraceContext = traceContext
        };
    }
    
    /// <summary>
    /// Validates that this context has the required data for local invocation.
    /// </summary>
    public bool IsValidForLocal => Task != null && Mapping != null && ScriptContext != null;
    
    /// <summary>
    /// Validates that this context has the required data for remote invocation.
    /// </summary>
    public bool IsValidForRemote => Binding.HasValue;
}

