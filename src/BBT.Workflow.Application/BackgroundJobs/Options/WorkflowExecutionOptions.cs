namespace BBT.Workflow.BackgroundJobs.Options;

public sealed class WorkflowExecutionOptions
{
    public const string SectionName = "WorkflowExecution";

    public int TransitionJobTimeoutSeconds { get; set; } = 300;

    public TransitionJobFailurePolicyOptions FailurePolicy { get; set; } = new();

    /// <summary>
    /// Selects the async continuation/enqueue atomicity model.
    /// <para>
    /// ON: continuations are enqueued through the transactional outbox — a
    /// <c>TransitionContinuationRequested</c> event commits in the same UoW as the durable job
    /// intent, and the Inbox forwards it to Orchestration which performs the Dapr enqueue. Fully
    /// transactional, at the cost of the outbox/inbox poll hop.
    /// </para>
    /// <para>
    /// OFF (default): the durable <c>InstanceJob</c> intent is committed in its own UoW FIRST,
    /// then the Dapr enqueue happens — so a Dapr job can never exist without a tracking intent
    /// (closes the dual-write gap). A crash between the intent commit and the enqueue is recovered
    /// by the ChainReaper. Faster (no poll hop), recommended for low-latency transitions.
    /// </para>
    /// </summary>
    public bool UseOutboxContinuations { get; set; }

    /// <summary>
    /// When enabled, async transitions execute one transition per background job
    /// (transition-per-job) rather than running the entire auto-chain inside a single job.
    /// Requires <see cref="UseOutboxContinuations"/> so each committed transition enqueues the
    /// next via the outbox. Default: false.
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
