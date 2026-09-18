using BBT.Workflow.Definitions;

namespace BBT.Workflow.Tasks.Invocation;

/// <summary>Where a prepared task binding will be executed, and why.</summary>
/// <param name="Mode">Local (in-process) or Remote (Execution service).</param>
/// <param name="Reason">
/// Which rule decided: "task-override", "type-config", "default" or "no-local-invoker".
/// Emitted as a span tag and a debug log so the path a task took is answerable from a trace
/// instead of from configuration archaeology.
/// </param>
public readonly record struct TaskInvocationDecision(ExecutionMode Mode, string Reason);

/// <summary>
/// The single decision point for local-versus-remote task execution. Every executor and the
/// function-response-cache gateway ask this; nothing else decides.
/// </summary>
public interface ITaskInvocationRouter
{
    TaskInvocationDecision Resolve(WorkflowTask task, string wireTaskType);
}
