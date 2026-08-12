namespace BBT.Workflow.Instances;

/// <summary>
/// An entry of the state function's <c>scheduledTransitions</c> list: a transition the runtime has
/// already armed to fire automatically for this instance, with the UTC instant it will execute at.
/// Built from the persisted job state (<see cref="InstanceJob"/> rows of type
/// <see cref="JobType.ScheduledTransition"/> that are active and carry an execution time) — never
/// from re-evaluating timer scripts, so the response always reflects what the scheduler was
/// actually armed with. Not caller-dependent: scheduled transitions fire regardless of roles, so
/// the list is a fact about the instance and is not role-filtered.
/// </summary>
public sealed class ScheduledTransitionItem
{
    /// <summary>
    /// <see cref="Kind"/> value for state-scoped scheduled transitions (trigger type Scheduled).
    /// A job-kind vocabulary, distinct from <see cref="Shared.TransitionItem.Kind"/>'s
    /// transition-kind values; the workflow-level timeout is a candidate future kind.
    /// </summary>
    public const string ScheduledKind = "scheduled";

    /// <summary>
    /// The transition key that will execute.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The kind of scheduled work; currently always <see cref="ScheduledKind"/>.
    /// </summary>
    public string Kind { get; set; } = ScheduledKind;

    /// <summary>
    /// The UTC instant the scheduler is armed to execute the transition at. Always
    /// <see cref="DateTimeKind.Utc"/>, so it serializes with the <c>Z</c> designator.
    /// </summary>
    public DateTime ExecuteAtUtc { get; set; }
}
