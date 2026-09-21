using System;
using BBT.Aether.Results;

namespace BBT.Workflow.Tasks;

/// <summary>
/// Unified task invoker interface for both local and remote execution.
/// Follows Strategy Pattern - implementations decide HOW to execute.
/// 
/// This interface unifies:
/// - Local invokers (script, trigger, subprocess tasks in Orchestration)
/// - Remote invokers (HTTP, Dapr tasks in Execution service)
/// - Custom invokers (user-defined execution strategies)
/// </summary>
[Obsolete("Superseded by the ITaskInvocationRouter / ITaskInvocationDispatcher seam (see docs/runtime/task-invocation-routing.md). This type was never implemented or consumed and is kept only so the published package stays source- and binary-compatible; it will be removed in a later release — see vnext-meta/deprecations.json.")]
public interface ITaskInvoker
{
    /// <summary>
    /// Task types this invoker can handle.
    /// Examples: "Script", "Http", "DaprService", "StartTrigger"
    /// </summary>
    IReadOnlySet<string> SupportedTaskTypes { get; }
    
    /// <summary>
    /// Determines if this invoker can handle the given task type.
    /// </summary>
    /// <param name="taskType">The task type to check.</param>
    /// <returns>True if this invoker can handle the task type.</returns>
    bool CanHandle(string taskType);
    
    /// <summary>
    /// Execution mode of this invoker.
    /// Determines where the task execution happens.
    /// </summary>
    ExecutionMode ExecutionMode { get; }
    
    /// <summary>
    /// Invokes the task with the given context.
    /// The context provides all necessary information based on execution mode.
    /// </summary>
    /// <param name="context">The task invocation context.</param>
    /// <param name="cancellationToken">Cancellation token for async operation control.</param>
    /// <returns>A Result containing the invocation result or an error.</returns>
    Task<Result<TaskInvocationResult>> InvokeAsync(
        TaskInvocationContext context,
        CancellationToken cancellationToken = default);
}

