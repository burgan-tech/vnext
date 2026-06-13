using BBT.Aether.Results;

namespace BBT.Workflow.Execution.Continuations;

/// <summary>
/// Selects and invokes the <see cref="IContinuationStrategy"/> for a given
/// <see cref="ContinuationMode"/>. This is the seam that lets sync (Inline) and
/// async (Enqueue) execution share one single-transition executor and differ only
/// in how the next transition is realized.
/// </summary>
public sealed class ContinuationDispatcher
{
    private readonly IReadOnlyDictionary<ContinuationMode, IContinuationStrategy> _strategies;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContinuationDispatcher"/>.
    /// </summary>
    public ContinuationDispatcher(IEnumerable<IContinuationStrategy> strategies)
    {
        _strategies = strategies.ToDictionary(s => s.Mode);
    }

    /// <summary>
    /// Realizes the continuation for the just-completed transition using the strategy
    /// registered for <paramref name="mode"/>.
    /// </summary>
    /// <returns>The next workflow context to execute in-loop, or Ok(null) when there is no further in-process work.</returns>
    public Task<Result<WorkflowExecutionContext?>> DispatchAsync(
        ContinuationMode mode,
        TransitionExecutionContext current,
        CancellationToken cancellationToken)
    {
        if (!_strategies.TryGetValue(mode, out var strategy))
        {
            return Task.FromResult(Result<WorkflowExecutionContext?>.Fail(
                Error.Failure(
                    "Continuation:NoStrategy",
                    $"No continuation strategy registered for mode '{mode}'.")));
        }

        return strategy.DispatchAsync(current, cancellationToken);
    }
}
