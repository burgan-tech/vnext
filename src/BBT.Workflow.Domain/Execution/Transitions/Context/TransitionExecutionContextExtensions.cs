namespace BBT.Workflow.Execution;

/// <summary>
/// Extension methods for TransitionExecutionContext to enhance workflow operations.
/// </summary>
public static class TransitionExecutionContextExtensions
{
    /// <summary>
    /// Determines whether the current transition is a cancel transition.
    /// Matches both the workflow's configured cancel key and the well-known reserved key.
    /// </summary>
    public static bool IsCancelTransition(this TransitionExecutionContext ctx)
    {
        var key = ctx.Transition?.Key;
        if (key is null) return false;

        return key.Equals(Definitions.WellKnownTransitionKeys.Cancel, StringComparison.OrdinalIgnoreCase)
            || ctx.Workflow.Cancel?.Key.Equals(key, StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Determines whether the current transition is an updateData transition.
    /// Matches both the workflow's configured updateData key and the well-known reserved key.
    /// </summary>
    public static bool IsUpdateDataTransition(this TransitionExecutionContext ctx)
    {
        var key = ctx.Transition?.Key;
        if (key is null) return false;

        return key.Equals(Definitions.WellKnownTransitionKeys.UpdateData, StringComparison.OrdinalIgnoreCase)
            || ctx.Workflow.UpdateData?.Key.Equals(key, StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Determines whether the current transition is an exit transition.
    /// Matches both the workflow's configured exit key and the well-known reserved key.
    /// </summary>
    public static bool IsExitTransition(this TransitionExecutionContext ctx)
    {
        var key = ctx.Transition?.Key;
        if (key is null) return false;

        return key.Equals(Definitions.WellKnownTransitionKeys.Exit, StringComparison.OrdinalIgnoreCase)
            || ctx.Workflow.Exit?.Key.Equals(key, StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Determines whether the current transition's target is the authored <c>$self</c> keyword, so
    /// the transition performs no state change.
    /// <para>
    /// This is a statement of FACT about the target, not a policy. It does not by itself mean the
    /// state's lifecycle is skipped — only <c>updateData</c> gets that treatment. See
    /// <see cref="SkipsStateLifecycle"/>, which composes this predicate with the updateData check.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only the authored <c>$self</c> keyword counts — NOT a literal target that happens to
    /// equal the current state.</b> Those are not the same claim. <c>$self</c> is a declaration of
    /// intent that cannot arise by accident; <c>target == currentState</c> is a coincidence
    /// produced by at least three unrelated mechanisms, and only one of them means "no state
    /// change":
    /// </para>
    /// <list type="bullet">
    /// <item><b>Start.</b> <c>InstanceCommandAppService</c> pre-positions a new instance into the
    /// initial state at creation (<c>instance.ChangeState(initialState)</c>) BEFORE dispatching the
    /// start transition. The state has not been entered yet — entering it is this transition's job
    /// — yet the comparison already holds.</item>
    /// <item><b>Retry after a partial commit.</b> <c>ChangeStateStep</c> persists with
    /// <c>saveChanges</c>, so a transition that faults in OnEntry leaves the instance committed in
    /// the target state. The retry re-runs the same transition to redo exactly the step that
    /// failed (with already-succeeded tasks bypassed per transition record), and the comparison
    /// holds there too.</item>
    /// <item><b>A genuine self-loop</b> (<c>from: A, target: A</c>) — the only case where the
    /// comparison does mean the state is unchanged. Authors who want the no-state-change semantics
    /// have <c>$self</c> for it; naming a state reads as "enter that state".</item>
    /// </list>
    /// <para>
    /// Guarding each incidental case one at a time was tried and is unsound — the comparison simply
    /// carries no information outside a fresh forward execution. Do not reintroduce it.
    /// </para>
    /// <para>
    /// Timeout and subflow-resume executions are excluded even for <c>$self</c>: their target comes
    /// from <c>ApplyTimeoutStateStep</c> / <c>ClearBusyOnResumeStep</c> rather than from
    /// <c>Transition.Target</c>, so the declared target does not describe where they land.
    /// </para>
    /// </remarks>
    public static bool IsSelfTargetTransition(this TransitionExecutionContext ctx)
    {
        var target = ctx.Transition?.Target;
        if (target is null) return false;

        if (ctx.Directives.IsTimeoutTransition || ctx.Directives.IsSubFlowResume)
            return false;

        return Definitions.WellKnownStateKeys.ReservedTargetKeys.Contains(target);
    }

    /// <summary>
    /// Determines whether this execution skips the state's lifecycle — the steps that only make
    /// sense when a state is actually left and another entered: <c>CancelScheduledJobs (39)</c>,
    /// <c>OnExit (40)</c>, <c>OnEntry (60)</c> and <c>Schedule (80)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only <c>updateData</c> qualifies</b> — the one transition whose target is fixed to
    /// <c>$self</c> by definition (<c>WorkflowValidator</c> enforces it), and whose whole purpose is
    /// to write data without moving the instance. Every OTHER <c>$self</c> transition — a
    /// <c>$self</c> shared transition in particular — runs the FULL lifecycle: its state's OnExit
    /// and OnEntry hooks fire and its scheduled transitions are torn down and re-armed. Authors who
    /// declare <c>target: $self</c> on a shared transition are saying "do not move the instance",
    /// not "skip the state's hooks".
    /// </para>
    /// <para>
    /// The <see cref="IsSelfTargetTransition"/> half of the conjunction looks redundant given the
    /// validator, but it carries the timeout / subflow-resume exclusions documented on that method,
    /// and those apply here too. Keep it.
    /// </para>
    /// </remarks>
    public static bool SkipsStateLifecycle(this TransitionExecutionContext ctx) =>
        ctx.IsSelfTargetTransition() && ctx.IsUpdateDataTransition();

    /// <summary>
    /// Determines whether the current transition is a shared transition.
    /// Shared transitions are triggered against the parent (main) flow — e.g. from an
    /// active subflow — and are reserved relative to instance locking so they can proceed
    /// even while the main flow holds the base instance lock.
    /// </summary>
    public static bool IsSharedTransition(this TransitionExecutionContext ctx)
    {
        var key = ctx.Transition?.Key;
        if (key is null) return false;

        return ctx.Workflow.FindSharedTransition(key) != null;
    }
}

