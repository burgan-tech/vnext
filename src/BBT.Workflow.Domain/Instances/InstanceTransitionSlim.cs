using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances;

/// <summary>
/// Lightweight projection of <see cref="InstanceTransition"/> that excludes the
/// Body and Header JSON columns. Used exclusively by monitoring read queries.
/// </summary>
public sealed record InstanceTransitionSlim(
    Guid Id,
    Guid InstanceId,
    string TransitionId,
    string FromState,
    string? ToState,
    DateTime StartedAt,
    DateTime? FinishedAt,
    TimeSpan? Duration,
    TriggerType TriggerType,
    DateTime CreatedAt,
    string? CreatedBy,
    string? CreatedByBehalfOf);
