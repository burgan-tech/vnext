using System.Diagnostics;
using BBT.Aether.Aspects;
using BBT.Aether.Results;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step that forwards transitions to active subflow instances.
/// Enqueues a post-commit job for the actual forward operation to avoid
/// holding the distributed lock during the remote call.
/// </summary>
public class ForwardToActiveSubflowStep : ITransitionStep
{
    /// <inheritdoc />
    public int Order => LifecycleOrder.ForwardToActiveSubflow;

    /// <inheritdoc />
    [Trace]
    public Task<Result<StepOutcome>> ExecuteAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        Activity.Current?.SetDisplayName($"[{Order}] {nameof(ForwardToActiveSubflowStep)}");

        // Skip if no active subflow - early return for non-applicable case
        if (!HasActiveSubflow(context))
        {
            return Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Continue()));
        }

        // Parent shared transition available in current state: execute on parent, do not forward
        if (IsParentSharedTransition(context))
        {
            return Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Continue()));
        }

        // Enqueue post-commit job - actual forward happens after lock release
        context.Directives.EnqueuePostCommit(new ForwardToSubflowJob(
            context.Instance.Subflow!.SubFlowInstanceId,
            context.InstanceId,
            context.TransitionKey,
            context.Instance.Subflow.SubFlowDomain,
            context.Instance.Subflow.SubFlowName,
            context.Instance.Subflow.SubFlowVersion,
            context.InstanceKey,
            context.Stage,
            context.Tags,
            context.DataElement,
            context.Headers.ToDictionary(),
            context.RouteValues.ToDictionary()));

        // Set initial client response (will be updated by handler if forward succeeds)
        context.ClientResponse = new ClientResponse
        {
            Id = context.InstanceId,
            Status = context.Instance.Status
        };

        // Skip to Finalize to ensure commit runs before lock release
        // This is critical: Stop() would skip commit, causing data loss
        var outcome = new StepOutcome
        {
            MutateDirectives = d =>
            {
                d.RequestEpilogue(EpilogueMode.Skip);
                d.MarkTerminal();
            },
            SkipToOrder = LifecycleOrder.Finalize
        };

        return Task.FromResult(Result<StepOutcome>.Ok(outcome));
    }

    /// <summary>
    /// Checks if context has an active subflow.
    /// </summary>
    private static bool HasActiveSubflow(TransitionExecutionContext context)
        => context.Instance.HasActiveSubFlow || context.Instance.Subflow != null;

    /// <summary>
    /// Returns true if the requested transition is a parent shared transition (and was validated, so available in current state).
    /// When true, the transition runs on the parent; we do not forward to the active subflow.
    /// </summary>
    private static bool IsParentSharedTransition(TransitionExecutionContext context)
        => context.Transition != null &&
           context.Workflow.FindSharedTransition(context.TransitionKey) != null;
}
