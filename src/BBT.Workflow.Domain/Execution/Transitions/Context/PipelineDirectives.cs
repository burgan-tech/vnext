using BBT.Aether.Events;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Execution;

/// <summary>
/// Represents a request for the next transition in sync dispatch chain.
/// Contains only identity information - full context is rebuilt by TransitionContextFactory.
/// </summary>
/// <param name="TransitionKey">The key of the next transition to execute.</param>
/// <param name="Reason">Optional reason for the transition request (e.g. <see cref="TransitionRequestReasons.ErrorBoundary"/>). Use constants from <see cref="TransitionRequestReasons"/> when applicable.</param>
public sealed record NextTransitionRequest(
    string TransitionKey,
    string? Reason = null);

/// <summary>
/// Controls pipeline execution flow and behavior through directives.
/// Manages resume points, epilogue modes, next transition requests, post-commit jobs, and terminal state tracking.
/// </summary>
public sealed class PipelineDirectives
{
    private readonly List<IPostCommitJob> _postCommitJobs = new();
    private readonly HashSet<string> _postCommitJobKeys = new(StringComparer.Ordinal);
    private readonly List<DomainEventEnvelope> _deferredEvents = new();

    /// <summary>
    /// Gets the order number from which to resume pipeline execution.
    /// Used for scenarios like subflow completion or re-planning.
    /// </summary>
    public int? ResumeFromOrder { get; private set; }

    /// <summary>
    /// Gets the epilogue execution mode.
    /// Determines whether epilogue steps (Schedule/Auto) should run or be skipped.
    /// </summary>
    public EpilogueMode Epilogue { get; private set; } = EpilogueMode.Run;

    /// <summary>
    /// Gets the next transition request for sync dispatch chain.
    /// Set by auto/scheduled steps when a chained transition should execute.
    /// </summary>
    public NextTransitionRequest? NextTransition { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the pipeline has reached a terminal state.
    /// </summary>
    public bool TerminalReached { get; private set; }
    
    /// <summary>
    /// Gets a value indicating whether this execution is resuming from a subflow.
    /// </summary>
    public bool IsSubFlowResume { get; private set; }

    /// <summary>
    /// Gets the completing SubFlow instance id for a subflow-resume execution.
    /// Used to build a per-sub-instance resume lock key so a nested sync resume
    /// (triggered inside an outer resume chain's post-commit) does not collide
    /// with the outer chain's resume lock. Null falls back to the legacy shared key.
    /// </summary>
    public Guid? SubFlowResumeInstanceId { get; private set; }

    /// <summary>
    /// Gets a value indicating whether this execution is resuming from a long-poll acknowledge
    /// (declarative long-poll termination on state entry — client acknowledged or fallback fired).
    /// </summary>
    public bool IsLongPollAckResume { get; private set; }

    /// <summary>
    /// Gets a value indicating whether this is an internal, system-triggered pipeline resume of an
    /// already-Busy instance (subflow completion or long-poll acknowledge). These share lock-key,
    /// validation-bypass and busy-confirmation behavior.
    /// </summary>
    public bool IsInternalResume => IsSubFlowResume || IsLongPollAckResume;

    /// <summary>
    /// Gets a value indicating whether this execution is triggered by a workflow timeout.
    /// </summary>
    public bool IsTimeoutTransition { get; private set; }

    /// <summary>
    /// Gets the error transition key to be triggered by error boundary.
    /// </summary>
    private string? _errorTransitionKey;

    /// <summary>
    /// Gets a value indicating whether an error transition has been set.
    /// </summary>
    public bool HasErrorTransition => _errorTransitionKey != null;

    /// <summary>
    /// Requests the pipeline to resume from a specific order.
    /// </summary>
    /// <param name="order">The lifecycle order to resume from.</param>
    public void RequestResumeFrom(int order) => ResumeFromOrder = order;

    /// <summary>
    /// Consumes and clears the resume-from order.
    /// </summary>
    /// <returns>The previously set resume-from order, or null if none was set.</returns>
    public int? ConsumeResumeFrom()
    {
        var t = ResumeFromOrder;
        ResumeFromOrder = null;
        return t;
    }

    /// <summary>
    /// Requests a specific epilogue execution mode.
    /// </summary>
    /// <param name="mode">The epilogue mode to apply.</param>
    public void RequestEpilogue(EpilogueMode mode) => Epilogue = mode;
    
    /// <summary>
    /// Marks the pipeline as having reached a terminal state.
    /// </summary>
    public void MarkTerminal() => TerminalReached = true;
    
    /// <summary>
    /// Requests a next transition to be executed in the sync dispatch chain.
    /// The current transition will complete (including Finalize), then the next transition starts.
    /// </summary>
    /// <param name="request">The next transition request containing transition key and reason.</param>
    public void RequestNextTransition(NextTransitionRequest request) => NextTransition = request;
    
    /// <summary>
    /// Consumes and clears the next transition request.
    /// Called by the pipeline after current transition completes to check for chained execution.
    /// </summary>
    /// <returns>The next transition request, or null if none was set.</returns>
    public NextTransitionRequest? ConsumeNextTransition()
    {
        var t = NextTransition;
        NextTransition = null;
        return t;
    }
    
    /// <summary>
    /// Marks this execution as a subflow resume scenario.
    /// </summary>
    /// <param name="subInstanceId">The completing SubFlow instance id; scopes the resume lock per sub-instance.</param>
    public void MarkAsSubFlowResume(Guid? subInstanceId = null)
    {
        IsSubFlowResume = true;
        SubFlowResumeInstanceId = subInstanceId;
    }

    /// <summary>
    /// Marks this execution as a long-poll acknowledge resume scenario.
    /// </summary>
    public void MarkAsLongPollAckResume() => IsLongPollAckResume = true;

    /// <summary>
    /// Marks this execution as a workflow timeout transition.
    /// </summary>
    public void MarkAsTimeoutTransition() => IsTimeoutTransition = true;

    /// <summary>
    /// Sets the error transition key to be triggered by error boundary.
    /// The pipeline will trigger this transition after error handling completes.
    /// </summary>
    /// <param name="transitionKey">The transition key to trigger.</param>
    public void SetErrorTransition(string transitionKey) => _errorTransitionKey = transitionKey;

    /// <summary>
    /// Consumes and clears the error transition key.
    /// Called by the pipeline when handling error boundary transition.
    /// </summary>
    /// <returns>The error transition key, or null if none was set.</returns>
    public string? ConsumeErrorTransition()
    {
        var key = _errorTransitionKey;
        _errorTransitionKey = null;
        return key;
    }

    /// <summary>
    /// Gets the deferred instance status for chain settlement. When no post-commit jobs are
    /// pending, the pipeline settles it at the in-lock rest point. When a post-commit barrier is
    /// present, it remains in the preserved directives snapshot for runner-owned settlement after
    /// the handoff.
    /// </summary>
    public InstanceStatus? ResolvedStatus { get; private set; }

    /// <summary>
    /// Sets the deferred resolved status.
    /// It is settled by the pipeline on the no-post-commit path, or by runner-owned orchestration
    /// from the preserved snapshot after a post-commit barrier.
    /// </summary>
    /// <param name="status">The status to defer.</param>
    public void SetResolvedStatus(InstanceStatus status) => ResolvedStatus = status;

    /// <summary>
    /// Consumes and clears the resolved status.
    /// Called by the pipeline only on the no-post-commit path; after a post-commit barrier, the
    /// runner consumes the preserved snapshot during post-handoff settlement.
    /// </summary>
    /// <returns>The deferred status, or null if none was set.</returns>
    public InstanceStatus? ConsumeResolvedStatus()
    {
        var s = ResolvedStatus;
        ResolvedStatus = null;
        return s;
    }

    /// <summary>
    /// The settlement's verdict that this hop closed the activation episode (see
    /// <see cref="ActivationVerdict"/>), or null when the episode continues (a continuation was
    /// enqueued, or this execution does not own the instance's status). Recorded by
    /// <c>TransitionSettlement</c>; read by the runner after commit, which emits the span.
    /// </summary>
    public ActivationVerdict? Activation { get; private set; }

    /// <summary>Records the settlement's activation verdict.</summary>
    public void RecordActivation(ActivationVerdict verdict) => Activation = verdict;

    /// <summary>
    /// True once the Enqueue continuation strategy has durably handed the chain to a follow-up job.
    /// The settlement reads it to know the episode has NOT reached its rest point even though this
    /// hop has nothing left to run in-process.
    /// </summary>
    public bool ContinuationEnqueued { get; private set; }

    /// <summary>Marks that the next hop was enqueued as a separate job.</summary>
    public void MarkContinuationEnqueued() => ContinuationEnqueued = true;

    /// <summary>
    /// Enqueues a post-commit job to be executed after the distributed lock is released.
    /// Post-commit jobs are returned intact across the pipeline barrier for runner-owned execution
    /// after the distributed lock is released. They are used for side effects like remote calls
    /// that should not block the lock.
    /// For idempotent jobs, duplicate enqueueing within the same transition is prevented.
    /// </summary>
    /// <param name="job">The post-commit job to enqueue.</param>
    public void EnqueuePostCommit(IPostCommitJob job)
    {
        // Enqueue-level idempotency: prevent duplicate jobs in the same transition
        if (job is IIdempotentPostCommitJob idempotentJob &&
            !_postCommitJobKeys.Add(idempotentJob.IdempotencyKey))
        {
            return; // Same job already queued in this transition
        }

        _postCommitJobs.Add(job);
    }

    /// <summary>
    /// Gets a non-consuming, read-only view of the currently accumulated post-commit jobs.
    /// Unlike <see cref="ConsumePostCommitJobs"/>, this does not clear the queue.
    /// Used by <see cref="ToContinuations"/> to project the directives state.
    /// </summary>
    public IReadOnlyList<IPostCommitJob> PostCommitJobs => _postCommitJobs;

    /// <summary>
    /// Consumes and clears all post-commit jobs.
    /// Called by runner-owned post-commit orchestration after the pipeline barrier. The pipeline
    /// itself only exposes these jobs as part of the handoff snapshot and never consumes them.
    /// </summary>
    /// <returns>A read-only list of post-commit jobs.</returns>
    public IReadOnlyList<IPostCommitJob> ConsumePostCommitJobs()
    {
        var copy = _postCommitJobs.ToArray();
        _postCommitJobs.Clear();
        _postCommitJobKeys.Clear();
        return copy;
    }

    /// <summary>
    /// Projects the current directive state into an immutable <see cref="ContinuationSet"/>
    /// snapshot. This is a pure read: it does NOT consume or clear any directive
    /// (next transition, post-commit jobs, resolved status, resume order remain intact), so the
    /// directives remain available to runner-owned orchestration across the handoff.
    /// Post-commit jobs are snapshotted into a stable array so the returned value is
    /// unaffected by later mutations.
    /// </summary>
    /// <returns>An immutable snapshot of the pending continuation work.</returns>
    public ContinuationSet ToContinuations() =>
        new(
            NextTransition,
            _postCommitJobs.ToArray(),
            ResolvedStatus,
            ResumeFromOrder,
            TerminalReached,
            Epilogue,
            ContinuationEnqueued);

    /// <summary>
    /// Gets a value indicating whether there are deferred events waiting to be published.
    /// </summary>
    public bool HasDeferredEvents => _deferredEvents.Count > 0;

    /// <summary>
    /// Defers a domain event envelope for explicit publishing after UoW commit.
    /// Events deferred here are NOT dispatched via the IDomainEventSink/SaveChanges path.
    /// </summary>
    /// <param name="envelope">The domain event envelope containing the event and its metadata.</param>
    public void DeferEvent(DomainEventEnvelope envelope)
    {
        _deferredEvents.Add(envelope);
    }

    /// <summary>
    /// Defers multiple domain event envelopes for explicit publishing after UoW commit.
    /// </summary>
    /// <param name="envelopes">The domain event envelopes to defer.</param>
    public void DeferEvents(IEnumerable<DomainEventEnvelope> envelopes)
    {
        _deferredEvents.AddRange(envelopes);
    }

    /// <summary>
    /// Consumes and clears all deferred events.
    /// Called by TransitionRunner after UoW commit to publish events via IDistributedEventBus.
    /// </summary>
    /// <returns>A read-only list of deferred domain event envelopes.</returns>
    public IReadOnlyList<DomainEventEnvelope> ConsumeDeferredEvents()
    {
        var copy = _deferredEvents.ToArray();
        _deferredEvents.Clear();
        return copy;
    }
}
