using System;
using BBT.Aether.Results;

namespace BBT.Workflow.Tasks;

/// <summary>
/// Unified registry for all task invokers.
/// Resolves the appropriate invoker based on task type and optional execution mode.
/// 
/// This interface unifies:
/// - ILocalTaskInvokerRegistry (Domain layer)
/// - ITaskInvokerRegistry (Execution layer)
/// </summary>
[Obsolete("Superseded by the ITaskInvocationRouter / ITaskInvocationDispatcher seam (see docs/runtime/task-invocation-routing.md). This type was never implemented or consumed and is kept only so the published package stays source- and binary-compatible; it will be removed in a later release — see vnext-meta/deprecations.json.")]
public interface ITaskInvokerRegistry
{
    /// <summary>
    /// Gets an invoker for the specified task type.
    /// Returns the first matching invoker regardless of execution mode.
    /// </summary>
    /// <param name="taskType">The task type to get the invoker for.</param>
    /// <returns>A Result containing the invoker or an error if not found.</returns>
    Result<ITaskInvoker> GetInvoker(string taskType);
    
    /// <summary>
    /// Gets an invoker with a specific execution mode.
    /// </summary>
    /// <param name="taskType">The task type to get the invoker for.</param>
    /// <param name="mode">The required execution mode.</param>
    /// <returns>A Result containing the invoker or an error if not found.</returns>
    Result<ITaskInvoker> GetInvoker(string taskType, ExecutionMode mode);
    
    /// <summary>
    /// Checks if an invoker is registered for the task type.
    /// </summary>
    /// <param name="taskType">The task type to check.</param>
    /// <returns>True if an invoker is registered for this task type.</returns>
    bool HasInvoker(string taskType);
    
    /// <summary>
    /// Checks if an invoker with a specific execution mode is registered.
    /// </summary>
    /// <param name="taskType">The task type to check.</param>
    /// <param name="mode">The required execution mode.</param>
    /// <returns>True if a matching invoker is registered.</returns>
    bool HasInvoker(string taskType, ExecutionMode mode);
    
    /// <summary>
    /// Gets all registered invokers.
    /// </summary>
    /// <returns>Collection of all registered invokers.</returns>
    IEnumerable<ITaskInvoker> GetAllInvokers();
    
    /// <summary>
    /// Gets all task types that have registered invokers.
    /// </summary>
    /// <returns>Collection of supported task types.</returns>
    IEnumerable<string> GetSupportedTaskTypes();
    
    /// <summary>
    /// Invokes a task using automatic invoker resolution.
    /// The registry resolves the appropriate invoker and delegates execution.
    /// </summary>
    /// <param name="context">The task invocation context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A Result containing the invocation result or an error.</returns>
    Task<Result<TaskInvocationResult>> InvokeAsync(
        TaskInvocationContext context,
        CancellationToken cancellationToken = default);
}

