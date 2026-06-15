using BBT.Aether;

namespace BBT.Workflow.Instances;

/// <summary>
/// Accumulates controlled mutations for <see cref="Instance"/> properties
/// that are allowed to be changed from script execution contexts.
/// <para>
/// Only a whitelisted subset of Instance properties is exposed here.
/// Platform-managed fields (State, Status, CompletedAt, etc.) are intentionally excluded.
/// </para>
/// </summary>
public sealed class InstanceMutations
{
    /// <summary>Pending Stage value (null means "clear stage").</summary>
    public string? Stage { get; private set; }

    /// <summary>Whether <see cref="SetStage"/> was called (distinguishes "set to null" from "not changed").</summary>
    public bool HasStageChange { get; private set; }

    /// <summary>
    /// Records a Stage mutation. Validated eagerly so scripts get immediate feedback.
    /// </summary>
    public void SetStage(string? stage)
    {
        Stage = Check.Length(stage, nameof(stage), InstanceConstants.MaxStageLength);
        HasStageChange = true;
    }

    /// <summary>
    /// Returns true if any mutation was recorded.
    /// </summary>
    public bool HasChanges => HasStageChange;

    /// <summary>
    /// Applies all accumulated mutations to the target <see cref="Instance"/> aggregate
    /// by delegating to the aggregate's own encapsulated methods.
    /// </summary>
    public void ApplyTo(Instance instance)
    {
        if (HasStageChange)
            instance.SetStage(Stage);
    }
}
