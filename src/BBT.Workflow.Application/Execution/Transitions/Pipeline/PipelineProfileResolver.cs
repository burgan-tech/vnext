using BBT.Workflow.Definitions;

namespace BBT.Workflow.Execution.Pipeline;

/// <summary>
/// Default implementation of <see cref="IPipelineProfileResolver"/> mapping <see cref="WorkflowExecutionContext"/>
/// trigger metadata to <see cref="PipelineExecutionProfile"/> factory instances.
/// </summary>
public sealed class PipelineProfileResolver : IPipelineProfileResolver
{
    /// <inheritdoc />
    public PipelineExecutionProfile Resolve(WorkflowExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return ResolveBase(context);
    }

    /// <inheritdoc />
    public PipelineExecutionProfile Resolve(
        WorkflowExecutionContext context,
        TransitionExecutionContext transitionContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(transitionContext);

        var baseProfile = ResolveBase(context);

        // The self-target variant is a POLICY applied to updateData alone, not to every $self
        // target. A $self shared transition runs the full state lifecycle — see
        // TransitionExecutionContextExtensions.SkipsStateLifecycle.
        return transitionContext.SkipsStateLifecycle()
            ? PipelineExecutionProfile.ForSelfTarget(baseProfile)
            : baseProfile;
    }

    /// <summary>
    /// Maps trigger metadata to the base profile. Kept as the single source of the base mapping so
    /// both overloads stay in step — the self-target composition only ever layers on top of this.
    /// </summary>
    private static PipelineExecutionProfile ResolveBase(WorkflowExecutionContext context)
    {
        if (context.IsErrorBoundaryTransition)
            return PipelineExecutionProfile.ForErrorBoundary();

        return context.TriggerType switch
        {
            TriggerType.Manual => PipelineExecutionProfile.ForManual(),
            TriggerType.Automatic => PipelineExecutionProfile.ForAutoChain(),
            TriggerType.Scheduled => PipelineExecutionProfile.ForScheduled(),
            TriggerType.Event => PipelineExecutionProfile.ForEvent(),
            _ => PipelineExecutionProfile.ForManual(),
        };
    }
}
