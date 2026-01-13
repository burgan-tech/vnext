using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Represents a task to be executed during a transition lifecycle.
/// Can be used for OnEntry, OnExit, and OnExecute task configurations.
/// </summary>
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
    /// The execution order of this task.
    /// </summary>
    public int Order { get; private set; }
    
    /// <summary>
    /// Reference to the task definition to execute.
    /// </summary>
    public Reference Task { get; private set; }
    
    /// <summary>
    /// Optional mapping script for input/output transformation.
    /// </summary>
    public ScriptCode Mapping { get; private set; }
    
    /// <summary>
    /// Task-level error boundary.
    /// Provides the most specific error handling for this task.
    /// </summary>
    [JsonPropertyName("errorBoundary")]
    public ErrorBoundary? ErrorBoundary { get; private set; }

    /// <summary>
    /// Creates a new OnExecuteTask instance.
    /// </summary>
    /// <param name="order">The execution order.</param>
    /// <param name="task">Reference to the task definition.</param>
    /// <param name="mapping">Optional mapping script.</param>
    /// <param name="errorBoundary">Optional task-level error boundary.</param>
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
}
