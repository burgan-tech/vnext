using System;
using System.Collections.Generic;
using System.Linq;
using BBT.Workflow.Tasks.Coordinator;

namespace BBT.Workflow.Execution.Pipeline;

/// <summary>
/// Carries non-blocking task failures (business-level failures when no ErrorBoundary exists)
/// through the pipeline so epilogue (AutoTransitions) can decide whether they were handled.
/// </summary>
public static class NonBlockingTaskFailures
{
    /// <summary>
    /// The <see cref="TransitionExecutionContext.Items"/> key used to store the failures list.
    /// </summary>
    public const string ItemsKey = "NonBlockingTaskFailures";

    /// <summary>
    /// Adds the non-blocking failure information to the transition context.
    /// </summary>
    public static void Add(
        TransitionExecutionContext context,
        string trigger,
        TasksExecutionResult tasksResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tasksResult);

        if (!tasksResult.HasFailedTasks)
        {
            return;
        }

        var firstErrorMessage = tasksResult.ExecutedTasks
            .FirstOrDefault(t => !t.IsSuccess)
            ?.ErrorMessage;

        var failure = new NonBlockingTaskFailure(
            trigger,
            tasksResult.FailedTaskKeys,
            firstErrorMessage);

        if (context.Items.TryGetValue(ItemsKey, out var existing) &&
            existing is List<NonBlockingTaskFailure> list)
        {
            list.Add(failure);
            return;
        }

        context.Items[ItemsKey] = new List<NonBlockingTaskFailure> { failure };
    }

    /// <summary>
    /// Gets the collected non-blocking failures, if any.
    /// </summary>
    public static IReadOnlyList<NonBlockingTaskFailure> Get(TransitionExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Items.TryGetValue(ItemsKey, out var existing) &&
            existing is List<NonBlockingTaskFailure> list)
        {
            return list;
        }

        return Array.Empty<NonBlockingTaskFailure>();
    }
}

/// <summary>
/// Represents a single non-blocking task failure batch produced by a lifecycle trigger.
/// </summary>
public sealed record NonBlockingTaskFailure(
    string Trigger,
    IReadOnlyList<string> FailedTaskKeys,
    string? FirstErrorMessage);
