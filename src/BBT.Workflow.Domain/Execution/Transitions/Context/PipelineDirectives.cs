using BBT.Workflow.Execution.ReEntry;

namespace BBT.Workflow.Execution;

/// <summary>
/// Controls pipeline execution flow and behavior through directives.
/// Manages resume points, epilogue modes, inline automatic transitions, and terminal state tracking.
/// </summary>
public sealed class PipelineDirectives
{
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
    /// Gets the queue of re-entry commands for inline automatic transition chain execution.
    /// </summary>
    public Queue<ReentryCommand> InlineAutoQueue { get; } = new();

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
    /// Enqueues a re-entry command for inline automatic transition execution.
    /// </summary>
    /// <param name="command">The re-entry command to enqueue.</param>
    public void EnqueueInlineAuto(ReentryCommand command) => InlineAutoQueue.Enqueue(command);
    
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
    /// Gets a value indicating whether there are any queued inline auto transitions.
    /// </summary>
    public bool HasQueuedTransitions => InlineAutoQueue.Length > 0;

    /// <summary>
    /// Gets a value indicating whether an error transition was requested.
    /// </summary>
    public bool HasErrorTransition => !string.IsNullOrEmpty(ErrorTransitionKey);
}