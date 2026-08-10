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
    /// When enabled, the chain-token gate rejects foreign transitions that arrive while an
    /// instance is Busy with an active chain token, unless they carry the matching token or are
    /// reserved (cancel/timeout). Provides auto-chain atomicity without a chain-spanning lock.
    /// Default: false (legacy "Busy blocks all except reserved").
    /// </summary>
    public bool StrictChainTokenGate { get; set; }

    /// <summary>
    /// Enables the stuck-Busy chain reaper (S7) sweep. Default: true — without it, a crash on
    /// the sync execution path (no durable job row) leaves the instance Busy forever and the
    /// Retry endpoint (Faulted-only) is unreachable. Staleness is measured from the chain
    /// heartbeat, which the step executor refreshes on every committed step, so legitimately
    /// long-running chains are not falsely reaped.
    /// </summary>
    public bool EnableChainReaper { get; set; } = true;

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
    /// Lease duration in seconds for the transition chain lock (L1). The lease must cover the
    /// whole auto-chain budget because the Dapr lock building block has no working TTL
    /// extension (its Redis component uses SET NX, which rejects re-acquire attempts even
    /// from the same owner). When 0 (default), the effective lease is derived as
    /// <see cref="TransitionJobTimeoutSeconds"/> + 30 so the lock always outlives the job
    /// execution budget and the timeout-recovery path.
    /// </summary>
    public int TransitionLockLeaseSeconds { get; set; }

    /// <summary>
    /// Enables per-hop lock lease extension between chained transitions. Only enable this with
    /// a lock provider that supports atomic TTL extension (e.g. the Redis provider); the Dapr
    /// lock provider always fails extension, which would stop every chain after its first hop.
    /// When enabled, a failed extension stops the chain instead of continuing without a held
    /// lease. Default: false — the budget-aligned lease
    /// (<see cref="TransitionLockLeaseSeconds"/>) carries the chain instead.
    /// </summary>
    public bool EnableLockLeaseExtension { get; set; }

    /// <summary>
    /// Resolves the effective L1 lock lease: the configured
    /// <see cref="TransitionLockLeaseSeconds"/> when positive, otherwise
    /// <see cref="TransitionJobTimeoutSeconds"/> + 30.
    /// </summary>
    public int GetEffectiveLockLeaseSeconds() =>
        TransitionLockLeaseSeconds > 0
            ? TransitionLockLeaseSeconds
            : TransitionJobTimeoutSeconds + 30;

    /// <summary>
    /// Enables the Busy-as-mutex execution model: transition admission rejects a Busy instance
    /// with 409 (<c>Instance:100031</c>) up front, the distributed lock shrinks to a short
    /// status-flip check-and-set (<see cref="StatusLockLeaseSeconds"/>), and the pipeline body
    /// plus auto-chain run without a held lease — the Busy flag and the chain token carry
    /// mutual exclusion instead. Default: false (legacy whole-chain lock).
    /// </summary>
    public bool UseBusyAsMutex { get; set; }

    /// <summary>
    /// Lease duration in seconds for the short status lock guarding instance status transitions
    /// (Active→Busy reserve and Busy→Active/Completed/Faulted settlement). The critical section
    /// is a single-row check-and-set committed in a RequiresNew UoW, so the lease only needs to
    /// outlive transient DB latency — it is a crash-safety net, not an execution budget.
    /// Default: 15.
    /// </summary>
    public int StatusLockLeaseSeconds { get; set; } = 15;

    /// <summary>
    /// Bounded wait applied when acquiring the status lock. Status flips resolve in
    /// milliseconds, so a short linear backoff absorbs contention (e.g. settlement racing a
    /// cancel) instead of failing the caller into a full retry cycle.
    /// </summary>
    public LockConflictRetryOptions StatusLockRetry { get; set; } = new()
    {
        MaxAttempts = 4,
        BaseDelayMilliseconds = 50
    };

    /// <summary>
    /// Maximum number of flow schemas swept concurrently by the chain reaper.
    /// Higher values reduce sweep wall-clock time at the cost of more concurrent DB connections.
    /// Default: 4.
    /// </summary>
    public int ChainReaperMaxConcurrentSweeps { get; set; } = 4;

    /// <summary>
    /// Per-flow sweep timeout in seconds. A flow that exceeds this limit is skipped with a warning
    /// so a single slow or broken schema cannot stall the whole sweep cycle.
    /// Default: 30.
    /// </summary>
    public int ChainReaperFlowTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Leader-lease duration in seconds for the chain reaper. Every orchestration replica runs
    /// the reaper hosted service, but only the one that holds the <c>chain-reaper-leader</c>
    /// lease sweeps in a given cycle — the others skip it. This removes the redundant
    /// per-replica <c>sys_flows</c> discovery and per-flow-schema polling that would otherwise
    /// scale with the replica count. The lease is acquired at the start of a cycle and released
    /// when the sweep completes; the TTL is only a crash-safety net, so it must comfortably
    /// exceed a typical sweep. Any rare mid-sweep expiry is harmless because the reaper's
    /// re-drive is idempotent (chain-token gate). Acquire/release use the platform
    /// <c>IPostgreSqlDistributedLockService</c>, backed only by the PostgreSQL lease store.
    /// Default: 120.
    /// </summary>
    public int ChainReaperLeaderLeaseSeconds { get; set; } = 120;

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
