namespace BBT.Workflow.BackgroundJobs.Options;

public sealed class WorkflowExecutionOptions
{
    public const string SectionName = "WorkflowExecution";

    public int TransitionJobTimeoutSeconds { get; set; } = 300;

    public TransitionJobFailurePolicyOptions FailurePolicy { get; set; } = new();

    /// <summary>
    /// When enabled, async transition continuations are enqueued through the transactional
    /// outbox (a <c>TransitionContinuationRequested</c> event committed in the same UoW as the
    /// durable job intent) instead of a pre-commit Dapr enqueue. Closes the dual-write gap
    /// (Dapr-succeeds-then-UoW-rolls-back); the Inbox handler performs the actual Dapr enqueue.
    /// Default: false (legacy dual-write path retained).
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
}

public sealed class TransitionJobFailurePolicyOptions
{
    public int MaxRetries { get; set; } = 5;
    public int IntervalSeconds { get; set; } = 30;
}
