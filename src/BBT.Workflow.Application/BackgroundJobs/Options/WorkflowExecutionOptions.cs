using BBT.Workflow.Execution.Pipeline;

namespace BBT.Workflow.BackgroundJobs.Options;

public sealed class WorkflowExecutionOptions
{
    public const string SectionName = "WorkflowExecution";

    public int TransitionJobTimeoutSeconds { get; set; } = 300;

    public TransitionJobFailurePolicyOptions FailurePolicy { get; set; } = new();

    /// <summary>
    /// How an ASYNC transition realizes a chained continuation — the next transition an already
    /// committed hop asks for. Default: <see cref="Options.AutoTransitionMode.Inline"/>.
    /// <para>
    /// Sync transitions are unaffected: they have always continued in-process, and
    /// <c>SyncTransitionStrategy</c> does not read this setting.
    /// </para>
    /// <para>
    /// This has NOTHING to do with authored <c>triggerType: 2</c> scheduled transitions. Those are
    /// armed by <c>ScheduleTransitionsStep</c> (order 80) and are always real scheduler jobs,
    /// whatever this is set to.
    /// </para>
    /// </summary>
    public AutoTransitionMode AutoTransitionMode { get; set; } = AutoTransitionMode.Inline;

    /// <summary>
    /// Lease duration in seconds for the auxiliary transition lock scopes that are NOT the
    /// short status lock — the per-subInstance terminal locks in the SubFlow completion /
    /// fault / cancellation services and the async accept (enqueue) lock. When 0 (default),
    /// the effective lease is derived as <see cref="TransitionJobTimeoutSeconds"/> + 30.
    /// </summary>
    public int TransitionLockLeaseSeconds { get; set; }

    /// <summary>
    /// Resolves the effective auxiliary lock lease: the configured
    /// <see cref="TransitionLockLeaseSeconds"/> when positive, otherwise
    /// <see cref="TransitionJobTimeoutSeconds"/> + 30.
    /// </summary>
    public int GetEffectiveLockLeaseSeconds() =>
        TransitionLockLeaseSeconds > 0
            ? TransitionLockLeaseSeconds
            : TransitionJobTimeoutSeconds + 30;

    /// <summary>
    /// Lease duration in seconds for the short status lock guarding instance status transitions
    /// (Active→Busy reserve/takeover and Busy→Active/Faulted settlement). The critical section
    /// is a single-row check-and-set committed in a RequiresNew UoW — real hold time is
    /// milliseconds; the lease only needs to outlive transient DB latency as a crash-safety
    /// net, not an execution budget. Default: 5.
    /// </summary>
    public int StatusLockLeaseSeconds { get; set; } = 5;

    /// <summary>
    /// PostgreSQL timeouts for the InstanceData write funnel: every SaveChanges that inserts
    /// InstanceData rows takes a per-instance <c>FOR UPDATE</c> row lock and runs with these
    /// transaction-scoped (<c>SET LOCAL</c>) limits.
    /// </summary>
    public InstanceDataWriteOptions InstanceDataWrite { get; set; } = new();

    /// <summary>
    /// When enabled, same-domain subflow forwarding/resume runs in-process through the canonical
    /// TransitionRunner entry (child scope, RequiresNew, reload-by-id, ambient context re-established)
    /// instead of over Dapr. Cross-domain always uses Dapr. Default: false (S9). The full in-process
    /// routing of ForwardToSubflowJob is pending compiler-in-the-loop work; the reload-by-id isolation
    /// fix in the resume/revert path is already applied.
    /// </summary>
    public bool InProcessSameDomainForwarding { get; set; }

    /// <summary>
    /// In-handler retry policy for transient instance-lock conflicts inside transition jobs.
    /// The Dapr job can fire while a competing holder (e.g. the enqueue accept lock or a
    /// finishing chain) still holds the instance execution lock for a few milliseconds;
    /// a short bounded retry absorbs that instead of losing the transition.
    /// </summary>
    public LockConflictRetryOptions LockConflictRetry { get; set; } = new();

    /// <summary>
    /// Bounded wait applied when acquiring the per-subInstance terminal lock in the SubFlow
    /// completion / fault / cancellation services.
    /// <para>
    /// Terminal sub-item signals are delivered at least twice by design (DurablePostCommit hook
    /// plus the Inbox worker). A duplicate that arrives while the original is still inside its
    /// transaction cannot see the pending write, so it must wait the short critical section out
    /// rather than fail fast into a full broker re-delivery cycle.
    /// </para>
    /// </summary>
    public LockConflictRetryOptions SubItemTerminalLockRetry { get; set; } = new()
    {
        MaxAttempts = 4,
        BaseDelayMilliseconds = 120
    };
}

/// <summary>
/// How an async transition's chained continuation (the next transition a committed hop requested)
/// is realized.
/// <para>
/// "Chained continuation" covers every <c>NextTransition</c> directive, not only the ones
/// <c>RunAutomaticTransitionsStep</c> produces: an error-boundary rule's replacement transition and
/// an <c>updateData</c> handoff travel the same seam. There is deliberately ONE decision point.
/// </para>
/// </summary>
public enum AutoTransitionMode
{
    /// <summary>
    /// Run the next transition IN-PROCESS, inside the job that is already executing (default).
    /// The chain advances at memory speed instead of paying a scheduler round trip per hop, which
    /// is what a UI client polling the state function actually observes as screen latency.
    /// <para>
    /// Trade-off: no durable per-hop checkpoint. A process loss mid-chain leaves the instance Busy
    /// under the accept's single job row rather than resuming from the last committed hop, and the
    /// whole chain shares one <c>TransitionJobTimeoutSeconds</c> budget.
    /// </para>
    /// </summary>
    Inline = 0,

    /// <summary>
    /// Enqueue the next transition as its own scheduler job, so each hop is a separate job, unit of
    /// work and durable checkpoint. Costs one scheduler round trip per hop — that latency is the gap
    /// <see cref="Inline"/> exists to remove. Choose this when per-hop durability is worth the gap.
    /// </summary>
    Scheduled = 1
}

public sealed class TransitionJobFailurePolicyOptions
{
    public int MaxRetries { get; set; } = 5;
    public int IntervalSeconds { get; set; } = 30;
}

/// <summary>
/// Timeouts for the InstanceData write funnel (<c>WorkflowDbContext</c> SaveChanges path).
/// Both are applied with <c>SET LOCAL</c>, i.e. scoped to the writing transaction —
/// PgBouncer-transaction-mode safe. <c>StatementTimeoutMs</c> applies to every statement for
/// the remainder of that transaction (single-statement cap, not a whole-transaction budget).
/// </summary>
public sealed class InstanceDataWriteOptions
{
    /// <summary>
    /// Maximum time a writer waits on the per-instance <c>FOR UPDATE</c> row lock before the
    /// write fails with <c>Instance:100035</c> (409). Default: 5000ms (POC parity).
    /// </summary>
    public int LockTimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Maximum runtime for any single statement in the writing transaction; exceeding it
    /// cancels the statement and fails the write with <c>Instance:100036</c> (503).
    /// Default: 10000ms (POC parity).
    /// </summary>
    public int StatementTimeoutMs { get; set; } = 10000;

    /// <summary>
    /// Kill-switch: true ⇒ append uses the old multi-pass path (<c>JsonData.Merge</c> →
    /// <c>NormalizedJson</c> → <c>ComputeDataHash</c>). The new default path merges, canonicalizes,
    /// and hashes in a single <c>Utf8JsonWriter</c> pass via <c>JsonCanonicalizer</c>, byte-parity
    /// proven against the old path. Flip to true as a rollback safety net if the single-pass path
    /// ever misbehaves in production. Default: false. Removal is a future version's work.
    /// </summary>
    public bool LegacyAppendPipeline { get; set; }

    /// <summary>
    /// Opt-in: true ⇒ append canonicalizes numbers losslessly (integers that fit <c>int64</c>
    /// round-trip exactly; decimals that fit <c>decimal</c> are written in plain, trailing-zero-free
    /// form). Default (false) preserves historical behavior across three affected classes:
    /// precision loss beyond <c>int32</c>/15-16 significant digits, exponent notation for small
    /// magnitudes (e.g. <c>0.00001</c> stays exponential instead of the plain form), and fractional
    /// negative zero (<c>-0.0</c> stays <c>-0.0</c> instead of normalizing to <c>0</c>). Values
    /// beyond <c>decimal</c>'s ~28-29 significant digits still fall back to <c>double</c> and are
    /// still rounded under either setting — this flag narrows, but does not eliminate, that case.
    /// Runtime cost is nil — the merge runs through
    /// <see cref="BBT.Workflow.Shared.Merging.JsonCanonicalizer"/> either way and only the
    /// number-formatting policy handed to it changes. What flipping this DOES cost is a one-time
    /// content-hash change, per instance, on that instance's next append: an instance holding a
    /// value in any of the three classes above hashes differently than it did, which yields one
    /// extra version row and one phantom diff in Monitor for it. No data is lost and no stored row
    /// is rewritten; turning the flag back off restores the previous hashes. Default: false.
    /// <para>
    /// <see cref="LegacyAppendPipeline"/> true ⇒ this flag is IGNORED: the kill-switch path
    /// restores historical behavior verbatim and is deliberately untouched by it.
    /// </para>
    /// </summary>
    public bool PreserveNumericPrecision { get; set; }
}

/// <summary>
/// Bounded retry settings for lock contention.
/// <para>
/// Used by <c>TransitionJobHandler</c> for instance-lock conflicts (exponential backoff; worst
/// case with its defaults: 100 + 200 + 400 + 800 = 1.5s across 5 attempts), and by the SubFlow
/// terminal services for the per-subInstance lock (linear backoff plus jitter; worst case with
/// its defaults: roughly 120 + 240 + 360 ms plus jitter across 4 attempts).
/// </para>
/// </summary>
public sealed class LockConflictRetryOptions
{
    /// <summary>Maximum pipeline execution attempts (first try included). Default: 5.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Base delay before the first retry; doubles per attempt. Default: 100ms.</summary>
    public int BaseDelayMilliseconds { get; set; } = 100;
}

/// <summary>
/// Conversions from configured retry settings to the lock-acquisition contract.
/// </summary>
public static class LockConflictRetryOptionsExtensions
{
    /// <summary>
    /// Projects the configured retry settings onto a <see cref="LockAcquireWait"/>, clamping
    /// misconfigured values so a bad setting can never produce a negative delay or zero attempts.
    /// </summary>
    public static LockAcquireWait ToLockAcquireWait(this LockConflictRetryOptions options)
        => new(
            Math.Max(1, options.MaxAttempts),
            TimeSpan.FromMilliseconds(Math.Max(0, options.BaseDelayMilliseconds)));
}
