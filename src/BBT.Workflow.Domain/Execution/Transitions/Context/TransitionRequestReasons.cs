namespace BBT.Workflow.Execution;

/// <summary>
/// Well-known reasons for requesting a next transition in the sync dispatch chain.
/// Used by <see cref="NextTransitionRequest"/> and for observability / state policy bypass logic.
/// </summary>
public static class TransitionRequestReasons
{
    /// <summary>
    /// Transition was requested by an error boundary (e.g. Rollback or Notify action).
    /// When this reason is set, state policy checks are bypassed so the transition can run from any state.
    /// </summary>
    public const string ErrorBoundary = "error_boundary";
}
