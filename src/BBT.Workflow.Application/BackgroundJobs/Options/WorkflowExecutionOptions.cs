using BBT.Workflow.Execution.Pipeline;

namespace BBT.Workflow.BackgroundJobs.Options;

public sealed class WorkflowExecutionOptions
{
    public const string SectionName = "WorkflowExecution";

    public int TransitionJobTimeoutSeconds { get; set; } = 300;

    public TransitionJobFailurePolicyOptions FailurePolicy { get; set; } = new();

    /// <summary>
    /// When enabled, async transitions execute one transition per background job
    /// (transition-per-job) rather than running the entire auto-chain inside a single job.
    /// Each committed transition enqueues the next via <c>ITransitionEnqueueGateway</c>.
    /// Default: false.
    /// </summary>
    public bool TransitionPerJob { get; set; }

    /// <summary>
    /// Governs how <c>EnqueueContinuationStrategy</c> realizes a chained continuation.
    /// <para>
    /// ON (default): enqueue the Dapr job DIRECTLY (no outbox/inbox poll hop) for lower latency;
    /// if the direct Dapr enqueue fails, fall back to publishing a <c>TransitionContinuationRequested</c>
    /// event through the transactional outbox so durability is preserved.
    /// </para>
    /// <para>
    /// OFF: always publish the continuation via the transactional outbox (legacy behavior) — fully
    /// transactional at the cost of the outbox/inbox poll hop.
    /// </para>
    /// The durable <c>InstanceJob</c> intent is inserted in the ambient transition UoW in both modes.
    /// </summary>
    public bool DirectEnqueueContinuations { get; set; } = true;

    /// <summary>
    /// When enabled, aggregate instance loads include only the IsLatest data row instead of the
    /// full version history — the full-merge model makes the latest row self-sufficient for
    /// pipeline merges, script context and polling, turning O(history) IO per load into O(1).
    /// History-dependent operations must use the repository full-history APIs; the aggregate
    /// fails fast otherwise. Default: false (canary rollout — enable per environment, compare
    /// baseline metrics, then flip the default).
    /// </summary>
    public bool LatestOnlyInstanceLoading { get; set; }

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
