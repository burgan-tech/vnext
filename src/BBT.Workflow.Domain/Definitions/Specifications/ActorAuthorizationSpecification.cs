using BBT.Aether.Results;
using BBT.Workflow.Execution;
using BBT.Workflow.Logging;
using BBT.Workflow.Shared;

namespace BBT.Workflow.Definitions.Specifications;

/// <summary>
/// Specification for validating actor authorization based on trigger type.
/// Ensures that transitions are executed by the appropriate actor (User or System).
/// Business Rules:
/// - Manual transitions: User actor (external API/UI requests)
/// - Event transitions: User actor (external webhooks/events)
/// - Automatic transitions: System actor (pipeline-managed)
/// - Scheduled transitions: System actor (background jobs)
/// </summary>
public sealed class ActorAuthorizationSpecification : ITransitionSpecification
{
    /// <inheritdoc />
    /// <summary>
    /// Medium-high priority - executes after bypass/well-known specs, before state checks.
    /// </summary>
    public int Priority => 40;
    
    /// <inheritdoc />
    /// <summary>
    /// Always applicable - all transitions must have actor authorization.
    /// </summary>
    public bool IsApplicable(TransitionExecutionContext context) => true;
    
    /// <inheritdoc />
    /// <summary>
    /// Validates actor based on trigger type.
    /// </summary>
    public Result IsSatisfiedBy(TransitionExecutionContext context)
    {
        return context.Trigger switch
        {
            TriggerType.Manual => ValidateManualActor(context),
            TriggerType.Automatic => ValidateAutomaticActor(context),
            TriggerType.Scheduled => ValidateScheduledActor(context),
            TriggerType.Event => ValidateEventActor(context),
            _ => Result.Fail(Error.NotSupported(
                "UnsupportedTriggerType",
                $"Trigger type {context.Trigger} is not supported"))
        };
    }
    
    /// <summary>
    /// Validates manual trigger execution rules.
    /// Manual transitions must be initiated by User actors (external API/UI requests).
    /// </summary>
    private static Result ValidateManualActor(TransitionExecutionContext context)
    {
        if (context.Actor != ExecutionActor.User)
        {
            return Result.Fail(WorkflowErrors.InvalidActorForManualTransition(
                context.InstanceId,
                context.Actor));
        }

        return Result.Ok();
    }

    /// <summary>
    /// Validates automatic trigger execution rules.
    /// Automatic transitions must be initiated by System actors (pipeline-managed).
    /// Also validates chain depth to prevent infinite loops.
    /// </summary>
    private static Result ValidateAutomaticActor(TransitionExecutionContext context)
    {
        if (context.Actor != ExecutionActor.System)
        {
            return Result.Fail(WorkflowErrors.InvalidActorForAutomaticTransition(
                context.InstanceId,
                context.Actor));
        }

        // Chain depth check (defense in depth - also checked in pipeline)
        const int maxChainDepth = 50;
        if (context.ChainDepth > maxChainDepth)
        {
            return Result.Fail(WorkflowErrors.TransitionChainDepthExceeded(
                context.ChainDepth,
                maxChainDepth,
                context.TransitionKey));
        }

        return Result.Ok();
    }

    /// <summary>
    /// Validates scheduled trigger execution rules.
    /// Scheduled transitions must be initiated by System actors (background jobs).
    /// </summary>
    private static Result ValidateScheduledActor(TransitionExecutionContext context)
    {
        if (context.Actor != ExecutionActor.System)
        {
            return Result.Fail(WorkflowErrors.InvalidActorForScheduledTransition(
                context.InstanceId,
                context.Actor));
        }

        return Result.Ok();
    }

    /// <summary>
    /// Validates event trigger execution rules.
    /// Event transitions must be initiated by User actors (external webhooks/events).
    /// This allows event-driven transitions from external systems.
    /// </summary>
    private static Result ValidateEventActor(TransitionExecutionContext context)
    {
        if (context.Actor != ExecutionActor.User)
        {
            return Result.Fail(WorkflowErrors.InvalidActorForEventTransition(
                context.InstanceId,
                context.Actor));
        }

        return Result.Ok();
    }
}
