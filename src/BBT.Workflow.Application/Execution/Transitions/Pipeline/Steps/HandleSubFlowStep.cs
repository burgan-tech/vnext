using System.Diagnostics;
using BBT.Aether.Aspects;
using BBT.Aether.Guids;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step that handles SubFlow operations.
/// Creates correlation and enqueues a post-commit job for subflow start.
/// The actual subflow start happens after the distributed lock is released.
/// </summary>
public sealed class HandleSubFlowStep(
    IInstanceRepository instanceRepository,
    IGuidGenerator guidGenerator,
    ILogger<HandleSubFlowStep> logger) : ITransitionStep
{
    /// <inheritdoc />
    public int Order => LifecycleOrder.SubFlow;

    /// <inheritdoc />
    [Trace]
    public async Task<Result<StepOutcome>> ExecuteAsync(TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        Activity.Current?.SetDisplayName($"[{Order}] {nameof(HandleSubFlowStep)}");

        // Early return if not applicable
        if (!IsApplicable(context))
        {
            return Result<StepOutcome>.Ok(StepOutcome.Continue());
        }

        // Railway chain: Validate config -> Execute operations -> Create outcome
        return await Result.Ok(context)
            .Ensure(
                ctx => ctx.Target!.SubFlow != null,
                CreateConfigInvalidError(context))
            .TapAsync(ctx => ExecuteSubFlowOperationsAsync(ctx, cancellationToken))
            .Map(CreateStepOutcome);
    }

    /// <summary>
    /// Checks if this step is applicable for the given context.
    /// </summary>
    private static bool IsApplicable(TransitionExecutionContext context)
    {
        return context is { Target.StateType: StateType.SubFlow, Transition: not null };
    }

    /// <summary>
    /// Creates configuration invalid error.
    /// </summary>
    private Error CreateConfigInvalidError(TransitionExecutionContext context)
    {
        logger.SubFlowConfigInvalid(context.Target!.Key, context.InstanceId);
        return WorkflowErrors.ConfigInvalid(context.InstanceId, context.Target.Key);
    }

    /// <summary>
    /// Creates the appropriate StepOutcome based on SubFlow type.
    /// </summary>
    private static StepOutcome CreateStepOutcome(TransitionExecutionContext context)
    {
        if (context.Target!.SubFlow!.Type.Equals(SubFlowType.SubFlow))
        {
            return new StepOutcome
            {
                MutateDirectives = d =>
                {
                    d.RequestEpilogue(EpilogueMode.Skip);
                    d.MarkTerminal();
                },
                SkipToOrder = LifecycleOrder.Finalize
            };
        }

        return StepOutcome.Continue();
    }

    /// <summary>
    /// Executes the SubFlow operations: creates correlation, updates instance, and enqueues post-commit job.
    /// The actual subflow start happens after the lock is released via the post-commit handler.
    /// </summary>
    private async Task ExecuteSubFlowOperationsAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var correlation = CreateCorrelation(context);
        context.Instance.AddCorrelation(correlation);

        await instanceRepository.UpdateAsync(context.Instance, true, cancellationToken);

        // Enqueue post-commit job - actual subflow start happens after lock release
        context.Directives.EnqueuePostCommit(new StartSubflowJob(correlation.Id, context.Target!.Key));

        logger.LogDebug(
            "Subflow job enqueued for instance {InstanceId}, correlation {CorrelationId}, target {TargetStateKey}",
            context.InstanceId,
            correlation.Id,
            context.Target.Key);
    }

    /// <summary>
    /// Creates correlation for SubFlow tracking.
    /// </summary>
    private InstanceCorrelation CreateCorrelation(TransitionExecutionContext context)
    {
        return InstanceCorrelation.Create(
            guidGenerator.Create(),
            context.InstanceId,
            context.Target!.Key,
            guidGenerator.Create(),
            context.Target!.SubFlow!.Type.Code,
            context.Target.SubFlow.Process.Domain,
            context.Target.SubFlow.Process.Key,
            context.Target.SubFlow.Process.Version);
    }
}
