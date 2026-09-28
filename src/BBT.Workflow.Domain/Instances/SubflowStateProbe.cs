using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances;

/// <summary>
/// No-tracking, single-row read of what a <c>sub:state-changed</c> delivery would write: the parent's
/// effective projection and the ordering watermark of the child's OPEN correlation. Read before the
/// per-sub-item lock by a backup delivery to recognise that the post-commit relay already applied
/// the same notification. A plain READ COMMITTED select — no row lock, no write — so it cannot take
/// part in the lock-order (Instances before InstancesCorrelations) the writers depend on.
/// </summary>
/// <param name="EffectiveState">Parent's effective state; null or blank when never projected.</param>
/// <param name="EffectiveStateType">Parent's effective state type.</param>
/// <param name="EffectiveStateSubType">Parent's effective state subtype.</param>
/// <param name="EffectiveStatus">Parent's raw effective status column.</param>
/// <param name="HasOpenCorrelation">False when the child's correlation is closed or absent.</param>
/// <param name="SubFlowCurrentState">The state the correlation last recorded for the child.</param>
/// <param name="SubFlowNotificationSeq">The correlation's notification-sequence watermark.</param>
/// <param name="SubFlowStateChangedAt">The correlation's last change timestamp.</param>
public sealed record SubflowStateProbe(
    string? EffectiveState,
    StateType? EffectiveStateType,
    StateSubType? EffectiveStateSubType,
    InstanceStatus EffectiveStatus,
    bool HasOpenCorrelation,
    string? SubFlowCurrentState,
    long SubFlowNotificationSeq,
    DateTime? SubFlowStateChangedAt);
