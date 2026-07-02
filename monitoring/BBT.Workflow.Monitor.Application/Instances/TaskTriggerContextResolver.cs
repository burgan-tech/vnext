using BBT.Workflow.Monitor.Instances.DTOs;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Monitor.Instances;

/// <summary>
/// Best-effort resolver that maps a task definition key back to the lifecycle slot
/// (OnExecute / OnExit / OnEntry) it was placed in within a workflow definition.
/// </summary>
internal static class TaskTriggerContextResolver
{
    /// <summary>
    /// Searches the workflow definition for the given task key in the context of the
    /// specified transition.  Search order: OnExecute of the matching transition →
    /// OnExit of fromState → OnEntry of toState.
    /// </summary>
    /// <param name="flow">The workflow definition to search.</param>
    /// <param name="transitionKey">The definition key of the transition that ran.</param>
    /// <param name="fromState">The state the instance was in before the transition.</param>
    /// <param name="toState">The state the instance moved to (null if still in progress).</param>
    /// <param name="taskDefinitionKey">The task definition key to locate.</param>
    /// <returns>Trigger context if found; <c>null</c> when the key is not in the definition.</returns>
    public static MonitorTaskTriggerContext? Resolve(
        WorkflowDefinition flow,
        string transitionKey,
        string fromState,
        string? toState,
        string taskDefinitionKey)
    {
        // 1. OnExecute — check all transitions across all states and shared transitions
        var allTransitions = flow.States
            .SelectMany(s => s.Transitions)
            .Concat(flow.SharedTransitions)
            .Where(t => t.Key == transitionKey);

        foreach (var transition in allTransitions)
        {
            var onExecuteEntry = transition.OnExecutionTasks
                .FirstOrDefault(t => t.Task.Key == taskDefinitionKey);

            if (onExecuteEntry is not null)
            {
                return new MonitorTaskTriggerContext
                {
                    TriggerLocation = "OnExecute",
                    ContextType = "Transition",
                    ContextKey = transitionKey,
                    Order = onExecuteEntry.Order,
                    MappingScript = NullIfEmpty(onExecuteEntry.Mapping?.Code)
                };
            }
        }

        // 2. OnExit — fromState
        var fromStateObj = flow.FindState(fromState);
        if (fromStateObj is not null)
        {
            var onExitEntry = fromStateObj.OnExits
                .FirstOrDefault(t => t.Task.Key == taskDefinitionKey);

            if (onExitEntry is not null)
            {
                return new MonitorTaskTriggerContext
                {
                    TriggerLocation = "OnExit",
                    ContextType = "State",
                    ContextKey = fromState,
                    Order = onExitEntry.Order,
                    MappingScript = NullIfEmpty(onExitEntry.Mapping?.Code)
                };
            }
        }

        // 3. OnEntry — toState
        if (toState is not null)
        {
            var toStateObj = flow.FindState(toState);
            if (toStateObj is not null)
            {
                var onEntryEntry = toStateObj.OnEntries
                    .FirstOrDefault(t => t.Task.Key == taskDefinitionKey);

                if (onEntryEntry is not null)
                {
                    return new MonitorTaskTriggerContext
                    {
                        TriggerLocation = "OnEntry",
                        ContextType = "State",
                        ContextKey = toState,
                        Order = onEntryEntry.Order,
                        MappingScript = NullIfEmpty(onEntryEntry.Mapping?.Code)
                    };
                }
            }
        }

        return null;
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
