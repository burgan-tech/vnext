using BBT.Aether.Results;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// When a <c>sys-version-registry</c> instance (key = target flow key) runs a transition — the start
/// transition or <c>set-default</c> — the flow's default-version pointer may have changed. Enqueues a
/// post-commit job that bumps the target flow's cache generation. Bumps are idempotent, so running on
/// every registry transition is harmless.
/// </summary>
public sealed class VersionRegistryInvalidationStep : ITransitionStep
{
    /// <inheritdoc />
    public int Order => LifecycleOrder.VersionRegistryInvalidation;

    /// <inheritdoc />
    public Task<Result<StepOutcome>> ExecuteAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var flowKey = context.Instance.Key;
        if (string.Equals(context.WorkflowKey, RuntimeSysSchemaInfo.VersionRegistry, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(flowKey))
        {
            context.Directives.EnqueuePostCommit(new InvalidateVersionPolicyJob(context.Domain, flowKey));
        }

        return Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.ContinueNoWork()));
    }
}
