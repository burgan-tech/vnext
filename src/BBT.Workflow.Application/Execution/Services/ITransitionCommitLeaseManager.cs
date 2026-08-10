namespace BBT.Workflow.Execution.Services;

/// <summary>
/// Owns a transition-stage lease that must remain held until the ambient unit of work commits.
/// </summary>
public interface ITransitionCommitLeaseManager : IAsyncDisposable
{
    /// <summary>
    /// Transfers ownership of <paramref name="lease"/> to the current transition stage.
    /// </summary>
    void Hold(IAsyncDisposable lease);

    /// <summary>
    /// Releases the held lease, if any. Safe to call more than once.
    /// </summary>
    ValueTask ReleaseAsync();
}
