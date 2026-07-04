using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances;

/// <summary>
/// Cheap change-detection signal for the state (long-poll) function. Every input that can
/// alter the state representation maps to one of these fields: instance-row progress
/// (state / sub-type / status / audit timestamp), the latest data ETag, and the open subflow
/// correlations (child state changes drive the completion-window view without touching the
/// parent row). Combined with the caller identity it forms the conditional-GET change token,
/// so an unchanged poll is answered 304 from a single indexed projection query instead of a
/// full aggregate load + authorization pass + DTO build + canonical-JSON hash.
/// </summary>
public sealed record InstanceStateSignal(
    Guid InstanceId,
    string? CurrentState,
    StateSubType? CurrentStateSubType,
    InstanceStatus Status,
    DateTime? ModifiedAt,
    string? LatestDataEtag,
    IReadOnlyList<InstanceCorrelationSignal> OpenCorrelations);

/// <summary>
/// Open-correlation slice of <see cref="InstanceStateSignal"/> — value-based (child state +
/// change timestamp), so the token changes exactly when the mirrored subflow state does.
/// </summary>
public sealed record InstanceCorrelationSignal(
    Guid Id,
    string? SubFlowCurrentState,
    DateTime? SubFlowStateChangedAt);
