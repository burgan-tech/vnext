using BBT.Workflow.Execution.PostCommit;

namespace BBT.Workflow.Execution;

/// <summary>
/// Represents a request for the next transition in sync dispatch chain.
/// Contains only identity information - full context is rebuilt by TransitionContextFactory.
/// </summary>
/// <param name="TransitionKey">The key of the next transition to execute.</param>
/// <param name="Reason">Optional reason for the transition request (e.g., "auto", "schedule").</param>
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
    /// Gets the error transition key requested by the error boundary system.
    /// When set, the pipeline should trigger this transition instead of continuing normally.
    /// </summary>
    public string? ErrorTransitionKey { get; private set; }

    /// <summary>
    /// Gets a value indicating whether an error transition has been requested.
    /// </summary>
    public bool HasErrorTransition => !string.IsNullOrEmpty(ErrorTransitionKey);

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
    public void MarkAsSubFlowResume() => IsSubFlowResume = true;

    /// <summary>
    /// Requests an error-triggered transition.
    /// The pipeline will stop current execution and trigger the specified transition.
    /// </summary>
    /// <param name="transitionKey">The key of the transition to trigger.</param>
    public void RequestErrorTransition(string transitionKey)
    {
        if (!string.IsNullOrEmpty(transitionKey))
        {
            ErrorTransitionKey = transitionKey;
        }
    }

    /// <summary>
    /// Clears the error transition request.
    /// </summary>
    public void ClearErrorTransition() => ErrorTransitionKey = null;
    
    /// <summary>
    /// Creates a snapshot of the current directives state for post-commit processing.
    /// The snapshot captures the inline auto queue for execution after UoW commit.
    /// </summary>
    /// <returns>A snapshot containing the queued re-entry commands.</returns>
    public DirectivesSnapshot CreateSnapshot() => new(InlineAutoQueue.ToArray(), ErrorTransitionKey);
}

/// <summary>
/// Immutable snapshot of pipeline directives for post-commit processing.
/// Used to transfer inline auto queue state across UoW boundaries.
/// </summary>
/// <param name="InlineAutoQueue">Array of re-entry commands to process after commit.</param>
/// <param name="ErrorTransitionKey">Optional error-triggered transition key.</param>
public sealed record DirectivesSnapshot(ReentryCommand[] InlineAutoQueue, string? ErrorTransitionKey = null)
{

    /// <summary>
    /// Enqueues a post-commit job to be executed after the distributed lock is released.
    /// Post-commit jobs are used for side effects like remote calls that shouldn't block the lock.
    /// For idempotent jobs, duplicate enqueueing within the same transition is prevented.
    /// </summary>
    public bool HasQueuedTransitions => InlineAutoQueue.Length > 0;

    /// <summary>
    /// Gets a value indicating whether an error transition was requested.
    /// </summary>
    public bool HasErrorTransition => !string.IsNullOrEmpty(ErrorTransitionKey);
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
    /// Consumes and clears all post-commit jobs.
    /// Called by the pipeline after transition completes to get jobs for execution outside the lock.
    /// </summary>
    /// <returns>A read-only list of post-commit jobs.</returns>
    public IReadOnlyList<IPostCommitJob> ConsumePostCommitJobs()
    {
        var copy = _postCommitJobs.ToArray();
        _postCommitJobs.Clear();
        _postCommitJobKeys.Clear();
        return copy;
    }
}