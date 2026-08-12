namespace BBT.Workflow.Instances;

/// <summary>
/// Lightweight projection of the instance columns that determine whether a cached
/// state-function response is still valid. Loaded via a single-row projection query
/// (no includes) so long-poll validation does not materialize the full aggregate.
/// </summary>
/// <param name="Id">Identifier of the instance row actually resolved for the requested identifier.
/// Compared against the cached entry so a newer row reusing the same key invalidates the cache.</param>
/// <param name="Key">Instance business key.</param>
/// <param name="EffectiveState">Externally exposed state column (includes subflow propagation).</param>
/// <param name="Status">Instance status.</param>
/// <param name="FlowVersion">Bound flow version; a version migration can change transitions/views
/// without a state or status change.</param>
/// <param name="HasActiveSubFlow">True when an open SubFlow-type correlation exists. The state
/// response is then built from a live subflow call, so it must not be served from cache.</param>
/// <param name="CorrelationCount">Total child correlations, active and completed. The state response
/// carries the full correlation set, so a newly started sub item must invalidate the cached body even
/// when state and status are unchanged.</param>
/// <param name="CompletedCorrelationCount">Completed child correlations. Moves when a sub item
/// terminates and moves back when a completion is reverted — neither changes the total count.</param>
/// <param name="LastCorrelationCompletedAt">Newest completion timestamp across child correlations,
/// or null when none has completed. Distinguishes a revert-and-recomplete that leaves both counts at
/// their original values.</param>
/// <param name="LastSubFlowStateChangedAt">Newest sub-item state-change timestamp across child
/// correlations, or null when none has reported a state. A sub item advancing its own state changes
/// neither count.</param>
/// <param name="ActiveScheduledTransitionJobCount">Active scheduled-transition jobs
/// (<see cref="JobType.ScheduledTransition"/> rows with <c>IsActive</c>). The state response carries
/// the <c>scheduledTransitions</c> list, so a job being cancelled or firing must invalidate the
/// cached body even when state and status are unchanged.</param>
/// <param name="LastScheduledTransitionJobCreatedAt">Newest creation timestamp across the active
/// scheduled-transition jobs, or null when there are none. Distinguishes a cancel-and-reschedule —
/// a <c>$self</c> re-entry that re-arms the same transition with a new execution time — which leaves
/// the count unchanged.</param>
/// <remarks>
/// The four correlation members must be computed over the <em>full</em> correlation set — active and
/// completed. See <see cref="FromInstance"/>: the aggregate's own collection is loaded with an
/// active-only filtered include, so it must never be the source, or the full-build ETag would never
/// match the one the projection query computes and the cache would invalidate on every poll.
/// The two job members are the opposite: they run over <em>active scheduled-transition jobs only</em>,
/// the same set the response body exposes.
/// Each member is expressed so that LINQ-to-Objects and SQL agree exactly: <c>Count</c> maps to
/// <c>COUNT</c>, and <c>Max</c> over a nullable projection ignores nulls and yields null for an
/// all-null (or empty) set in both.
/// </remarks>
public sealed record InstanceStateFingerprint(
    Guid Id,
    string? Key,
    string? EffectiveState,
    InstanceStatus Status,
    string? FlowVersion,
    bool HasActiveSubFlow,
    int CorrelationCount,
    int CompletedCorrelationCount,
    DateTime? LastCorrelationCompletedAt,
    DateTime? LastSubFlowStateChangedAt,
    int ActiveScheduledTransitionJobCount,
    DateTime? LastScheduledTransitionJobCreatedAt)
{
    /// <summary>
    /// Builds the fingerprint from an already-loaded aggregate — the full-build path uses this
    /// so its ETag is computed from exactly the same values the projection query would return.
    /// </summary>
    /// <param name="instance">The loaded instance aggregate.</param>
    /// <param name="allCorrelations">The instance's child correlations, active <em>and</em> completed,
    /// read separately. Required as an explicit argument precisely because
    /// <c>instance.ChildCorrelations</c> is loaded active-only on the state path.</param>
    /// <param name="activeScheduledTransitionJobs">The instance's <em>active</em> jobs of type
    /// <see cref="JobType.ScheduledTransition"/>, pre-filtered by the caller. The same list feeds the
    /// response body's <c>scheduledTransitions</c>, so body and ETag can never disagree on the set.</param>
    public static InstanceStateFingerprint FromInstance(
        Instance instance,
        IReadOnlyCollection<InstanceCorrelation> allCorrelations,
        IReadOnlyCollection<InstanceJob> activeScheduledTransitionJobs) =>
        new(instance.Id,
            instance.Key,
            instance.EffectiveState,
            instance.Status,
            instance.FlowVersion,
            instance.HasActiveSubFlow,
            allCorrelations.Count,
            allCorrelations.Count(c => c.IsCompleted),
            allCorrelations.Select(c => c.CompletedAt).Max(),
            allCorrelations.Select(c => c.SubFlowStateChangedAt).Max(),
            activeScheduledTransitionJobs.Count,
            activeScheduledTransitionJobs.Select(j => (DateTime?)j.CreatedAt).Max());
}
