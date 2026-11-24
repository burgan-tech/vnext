using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Tasks.Coordinator;

namespace BBT.Workflow.Tasks.Mapping;

/// <summary>
/// Hands a transition hop's caller to the declarative mappings of the tasks the hop runs (their <c>user</c> variable):
/// the per-task options refiner sets <see cref="TaskEngineExecutionOptions.Caller"/> to
/// <see cref="ICallerAccessorFactory.ForHop"/> of the hop's actor and request headers, which the engine copies onto the
/// <c>TaskExecutorContext</c>. Script mappings never see it (their <c>ScriptContext.Caller</c> is not touched).
/// </summary>
public static class TaskMappingCaller
{
    /// <summary>
    /// The options refiner for <paramref name="bindings"/> run in <paramref name="context"/>'s hop, or null when there is
    /// nothing to carry: no factory, a System hop (no caller), or no binding with a declarative block. The accessor
    /// resolves roles lazily, so building it costs no provider call until an expression reads <c>user</c>.
    /// </summary>
    public static Func<OnExecuteTask, TaskEngineExecutionOptions, TaskEngineExecutionOptions>? Refiner(
        ICallerAccessorFactory? callerAccessorFactory,
        TransitionExecutionContext context,
        IEnumerable<OnExecuteTask> bindings,
        CancellationToken cancellationToken)
    {
        if (callerAccessorFactory is null || !bindings.Any(b => b.HasDeclarativeMapping))
            return null;

        var caller = callerAccessorFactory.ForHop(context.Actor, context.Headers, cancellationToken);
        if (!caller.IsAvailable)
            return null;

        return (binding, options) => binding.HasDeclarativeMapping ? options with { Caller = caller } : options;
    }
}
