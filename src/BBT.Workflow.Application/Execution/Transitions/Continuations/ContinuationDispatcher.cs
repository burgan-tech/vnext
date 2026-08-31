using System.Diagnostics;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Logging;
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
    public async Task<Result<WorkflowExecutionContext?>> DispatchAsync(
        ContinuationMode mode,
        TransitionExecutionContext current,
        CancellationToken cancellationToken)
    {
        if (!_strategies.TryGetValue(mode, out var strategy))
        {
            return Result<WorkflowExecutionContext?>.Fail(
                Error.Failure(
                    "Continuation:NoStrategy",
                    $"No continuation strategy registered for mode '{mode}'."));
        }

        // What happens between one hop finishing and the next one starting: an Enqueue writes the
        // job row and arms the scheduler, an Inline hands the next context back to the loop. It was
        // the largest unattributed stretch inside the pipeline — a trace showed the steps finishing
        // and then time passing with nothing to name it.
        using var activity = PipelineStepActivityHelper.StartOperationActivity($"Transition.Continuation/{mode}");
        activity?.SetTag(TelemetryConstants.TagNames.ContinuationMode, mode.ToString());

        var result = await strategy.DispatchAsync(current, cancellationToken);
        if (!result.IsSuccess)
            activity?.SetStatus(ActivityStatusCode.Error, result.Error.Message);
        else
            activity?.SetTag(TelemetryConstants.TagNames.ContinuationHasNext, result.Value is not null);

        return result;
    }
}
