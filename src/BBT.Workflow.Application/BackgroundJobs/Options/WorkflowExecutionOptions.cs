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
    /// <c>IDistributedLockService</c> and work on both the Postgres and Dapr lock providers
    /// (both grant exactly one winner). Default: 120.
    /// </summary>
    public int ChainReaperLeaderLeaseSeconds { get; set; } = 120;

    /// <summary>
    /// Selects how transition-scoped distributed domain events (deferred during the pipeline via
    /// <c>ExtractAndDeferInstanceEvents</c>) are published.
    /// <para>
    /// <see cref="WorkflowEventPublishingMode.Legacy"/> (default): events are published inside the
    /// non-transactional business UoW <b>before</b> its commit — the historical behavior. This is
    /// the safe fallback and keeps existing semantics unchanged.
    /// </para>
    /// <para>
    /// <see cref="WorkflowEventPublishingMode.TransactionalOutbox"/>: the business state is committed
    /// first, then the deferred events are written to the outbox in a dedicated
    /// <c>RequiresNew, IsTransactional=true</c> UoW that commits them atomically as one durable
    /// envelope. This restores at-least-once distributed delivery (outbox worker → broker → inbox
    /// handler + retry) independently of the pipeline's per-step commits. State and events are two
    /// durable writes rather than one transaction (the per-step durable-progress design precludes a
    /// single enclosing transaction); a crash between them is recovered by the idempotent
    /// retry/reaper path.
    /// </para>
    /// <para>
    /// <see cref="WorkflowEventPublishingMode.SinkDriven"/>: the pipeline stops extracting events
    /// off the aggregate (<c>ExtractAndDeferInstanceEvents</c> becomes a no-op), so the events stay
    /// on the instance and flow through the standard Aether domain-event sink → the unit of work
    /// dispatches them to the outbox on commit. This retires the bespoke defer path and makes the
    /// aggregate the single source of truth. It REQUIRES the runtime to actually dispatch events on
    /// a non-transactional commit — i.e. the Aether option
    /// <c>AetherDomainEventOptions.DispatchNonTransactionalEventsToOutbox</c> must be enabled and
    /// the Npgsql schema-switching mode must allow non-transactional units of work
    /// (<c>SessionSearchPath</c>). Enable this mode only together with those; otherwise events
    /// buffered by the sink are dropped at commit (worse than the defer path).
    /// </para>
    /// Canary rollout — enable per environment, compare baseline metrics, then flip the default.
    /// </summary>
    public WorkflowEventPublishingMode EventPublishingMode { get; set; } = WorkflowEventPublishingMode.Legacy;

    /// <summary>
    /// When enabled, a successful event hook no longer suppresses publishing to the inner event bus:
    /// the hook (synchronous, local side-effect) runs <b>and</b> the event is still written to the
    /// outbox for distributed delivery (inbox handler). This restores the documented dual-processing
    /// pattern (hook + handler both run). When false (default), the historical short-circuit is kept
    /// — a successful hook marks the event handled and it is not published distributed.
    /// <para>
    /// Enable only after confirming the corresponding inbox handlers are idempotent, since both the
    /// local hook and the distributed handler will then process the event. Canary rollout.
    /// </para>
    /// </summary>
    public bool AdditiveEventHooks { get; set; }

    /// <summary>
    /// When enabled, same-domain subflow forwarding/resume runs in-process through the canonical
    /// TransitionRunner entry (child scope, RequiresNew, reload-by-id, ambient context re-established)
    /// instead of over Dapr. Cross-domain always uses Dapr. Default: false (S9). The full in-process
    /// routing of ForwardToSubflowJob is pending compiler-in-the-loop work; the reload-by-id isolation
    /// fix in the resume/revert path is already applied.
    /// </summary>
    public bool InProcessSameDomainForwarding { get; set; }
}

/// <summary>
/// Publishing strategy for transition-scoped distributed domain events.
/// See <see cref="WorkflowExecutionOptions.EventPublishingMode"/>.
/// </summary>
public enum WorkflowEventPublishingMode
{
    /// <summary>Publish inside the non-transactional business UoW before its commit (historical behavior).</summary>
    Legacy = 0,

    /// <summary>Commit business state first, then write deferred events to the outbox in a dedicated transactional UoW.</summary>
    TransactionalOutbox = 1,

    /// <summary>
    /// Do not extract events off the aggregate; let them flow through the Aether domain-event sink
    /// and be dispatched by the unit of work on commit. Requires the runtime to dispatch events on
    /// a non-transactional commit (see <see cref="WorkflowExecutionOptions.EventPublishingMode"/>).
    /// </summary>
    SinkDriven = 2
}

public sealed class TransitionJobFailurePolicyOptions
{
    public int MaxRetries { get; set; } = 5;
    public int IntervalSeconds { get; set; } = 30;
}
