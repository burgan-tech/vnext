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
    /// Enables the stuck-Busy chain reaper (S7) sweep. Default: false.
    /// </summary>
    public bool EnableChainReaper { get; set; }

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
    /// When enabled, same-domain subflow forwarding/resume runs in-process through the canonical
    /// TransitionRunner entry (child scope, RequiresNew, reload-by-id, ambient context re-established)
    /// instead of over Dapr. Cross-domain always uses Dapr. Default: false (S9). The full in-process
    /// routing of ForwardToSubflowJob is pending compiler-in-the-loop work; the reload-by-id isolation
    /// fix in the resume/revert path is already applied.
    /// </summary>
    public bool InProcessSameDomainForwarding { get; set; }
}

public sealed class TransitionJobFailurePolicyOptions
{
    public int MaxRetries { get; set; } = 5;
    public int IntervalSeconds { get; set; } = 30;
}
