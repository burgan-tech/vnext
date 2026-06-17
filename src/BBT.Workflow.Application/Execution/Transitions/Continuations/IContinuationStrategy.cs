using BBT.Aether.Results;

namespace BBT.Workflow.Execution.Continuations;

/// <summary>
/// Identifies how continuation work (the next transition in an auto-chain) is realized.
/// </summary>
public enum ContinuationMode
{
    /// <summary>Realize the next transition in-process within the current request/lock (sync auto-chain).</summary>
    Inline,

    /// <summary>Persist the next transition durably (transactional outbox) so a separate job resumes the chain (async / transition-per-job).</summary>
    Enqueue
}

/// <summary>
/// Strategy that realizes the continuation of a transition chain after one transition
/// has completed its steps and post-commit work.
/// </summary>
/// <remarks>
/// The single behavioral difference between sync and async execution is which strategy
/// realizes the continuation:
/// <list type="bullet">
/// <item><see cref="ContinuationMode.Inline"/> returns the next <see cref="WorkflowExecutionContext"/>
/// for the pipeline loop to execute in-process (null when the chain is complete).</item>
/// <item><see cref="ContinuationMode.Enqueue"/> (later spec) persists the continuation to the
/// outbox and returns null, ending the in-process loop; a separate job resumes the chain.</item>
/// </list>
/// </remarks>
public interface IContinuationStrategy
{
    /// <summary>Gets the mode this strategy handles.</summary>
    ContinuationMode Mode { get; }

    /// <summary>
    /// Realizes the continuation for the just-completed transition.
    /// Consumes the next-transition directive from <paramref name="current"/>.
    /// </summary>
    /// <param name="current">The context of the transition that just completed.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// Ok with the next <see cref="WorkflowExecutionContext"/> to execute in-loop,
    /// or Ok(null) when there is no further work (chain complete or enqueued).
    /// </returns>
    Task<Result<WorkflowExecutionContext?>> DispatchAsync(
        TransitionExecutionContext current,
        CancellationToken cancellationToken);
}
