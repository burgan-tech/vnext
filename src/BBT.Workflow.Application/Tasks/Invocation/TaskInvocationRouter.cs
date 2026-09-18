using BBT.Workflow.Definitions;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Tasks.Invocation;

/// <summary>
/// Resolution order, first match wins:
/// <list type="number">
/// <item>the task definition's own override (phase 2 hook — see <see cref="TryGetTaskOverride"/>);</item>
/// <item>the per-type host configuration entry;</item>
/// <item>the configured default;</item>
/// <item>the capability gate: no local invoker registered ⇒ Remote.</item>
/// </list>
/// The capability gate is last and unconditional: the router never promises a local path it
/// cannot perform, so a configuration naming a type with no in-process invoker degrades to the
/// remote path instead of failing the task.
/// </summary>
public sealed class TaskInvocationRouter(
    IOptions<TaskInvocationOptions> options,
    ILocalTaskInvokerRegistry localInvokers) : ITaskInvocationRouter
{
    private readonly TaskInvocationOptions _options = options.Value;

    /// <inheritdoc />
    public TaskInvocationDecision Resolve(WorkflowTask task, string wireTaskType)
    {
        var (mode, reason) = ResolveRequested(task, wireTaskType);

        if (mode == ExecutionMode.Local && !localInvokers.Has(wireTaskType))
            return new TaskInvocationDecision(ExecutionMode.Remote, "no-local-invoker");

        return new TaskInvocationDecision(mode, reason);
    }

    private (ExecutionMode Mode, string Reason) ResolveRequested(WorkflowTask task, string wireTaskType)
    {
        if (TryGetTaskOverride(task) is { } taskMode)
            return (taskMode, "task-override");

        if (_options.Modes.TryGetValue(wireTaskType, out var typeMode))
            return (typeMode, "type-config");

        return (_options.DefaultMode, "default");
    }

    /// <summary>
    /// Phase 2 hook: the per-task-definition override (<c>config.executionMode</c>) lands here
    /// once the matching vnext-schema release ships. Kept as an explicit seam rather than an
    /// inline TODO so adding the field never has to reshape the resolution order. Always null
    /// today.
    /// </summary>
    private static ExecutionMode? TryGetTaskOverride(WorkflowTask task) => null;
}
