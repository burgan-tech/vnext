using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;

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
