using BBT.Aether;

namespace BBT.Workflow.ExceptionHandling;

/// <summary>
/// Exception thrown when the transition chain depth exceeds the maximum allowed limit.
/// This prevents infinite loops and excessive recursion in automatic transition chains.
/// </summary>
public sealed class TransitionChainDepthExceededException : UserFriendlyException
{
    /// <summary>
    /// Gets the maximum allowed chain depth.
    /// </summary>
    public int MaxChainDepth { get; }

    /// <summary>
    /// Gets the current chain depth that exceeded the limit.
    /// </summary>
    public int CurrentChainDepth { get; }

    /// <summary>
    /// Gets the transition key that caused the exception.
    /// </summary>
    public string? TransitionKey { get; }

    /// <summary>
    /// Initializes a new instance of the TransitionChainDepthExceededException class.
    /// </summary>
    /// <param name="currentChainDepth">The current chain depth that exceeded the limit</param>
    /// <param name="maxChainDepth">The maximum allowed chain depth</param>
    /// <param name="transitionKey">The transition key that caused the exception</param>
    public TransitionChainDepthExceededException(
        int currentChainDepth,
        int maxChainDepth,
        string? transitionKey = null)
        : base(
            code: WorkflowErrorCodes.TransitionChainDepthExceeded,
            message: $"Transition chain depth limit exceeded ({currentChainDepth}/{maxChainDepth})" +
                     (string.IsNullOrEmpty(transitionKey) ? "" : $" for transition '{transitionKey}'"))
    {
        CurrentChainDepth = currentChainDepth;
        MaxChainDepth = maxChainDepth;
        TransitionKey = transitionKey;
    }
}