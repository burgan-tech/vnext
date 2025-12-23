using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Represents a task execution configuration within a transition or state lifecycle.
/// Contains task reference, execution order, input mapping, and error handling configuration.
/// </summary>
/// <remarks>
/// The ErrorBoundary is placed here rather than on WorkflowTask because:
/// - The same task can be executed with different error handling in different contexts
/// - Error handling is part of the execution configuration, not the task definition
/// - This enables fine-grained control per transition/state rather than global task settings
/// </remarks>
public sealed class OnExecuteTask
{
    private OnExecuteTask()
    {
    }

    [JsonConstructor]
    private OnExecuteTask(
        int order,
        Reference task,
        ScriptCode mapping,
        ErrorBoundary? errorBoundary
    )
    {
        Order = order;
        Task = task;
        Mapping = mapping;
        ErrorBoundary = errorBoundary;
    }

    /// <summary>
    /// Execution order of this task within the collection.
    /// </summary>
    public int Order { get; private set; }

    /// <summary>
    /// Reference to the task definition.
    /// </summary>
    public Reference Task { get; private set; }

    /// <summary>
    /// Input mapping script for task execution.
    /// </summary>
    public ScriptCode Mapping { get; private set; }

    /// <summary>
    /// Error boundary configuration for this task execution.
    /// Provides task-level error handling that overrides state and workflow-level policies.
    /// </summary>
    [JsonPropertyName("errorBoundary")]
    public ErrorBoundary? ErrorBoundary { get; private set; }

    /// <summary>
    /// Creates a new OnExecuteTask instance.
    /// </summary>
    /// <param name="order">Execution order.</param>
    /// <param name="task">Reference to the task definition.</param>
    /// <param name="mapping">Input mapping script.</param>
    /// <param name="errorBoundary">Optional error boundary for task-level error handling.</param>
    public static OnExecuteTask Create(
        int order,
        IReference task,
        ScriptCode mapping,
        ErrorBoundary? errorBoundary = null)
    {
        return new OnExecuteTask(
            order,
            task.ToReference(),
            mapping,
            errorBoundary);
    }

    /// <summary>
    /// Sets the error boundary for this task execution.
    /// </summary>
    /// <param name="errorBoundary">The error boundary configuration.</param>
    public void SetErrorBoundary(ErrorBoundary errorBoundary)
    {
        ErrorBoundary = errorBoundary;
    }
}