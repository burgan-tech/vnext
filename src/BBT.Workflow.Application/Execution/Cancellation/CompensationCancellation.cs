namespace BBT.Workflow.Execution.Cancellation;

/// <summary>
/// The cancellation token compensation paths run on. A compensation (releasing a reserved Busy
/// flag, undoing a stamp) exists precisely because the caller's work was abandoned — often because
/// the request token itself was cancelled (client disconnect). Running the compensation on that
/// same token makes it throw immediately and strands the instance; it gets its own bounded token
/// instead, so it is neither tied to the caller nor able to hang forever.
/// </summary>
public static class CompensationCancellation
{
    /// <summary>The time a compensation may take before it is abandoned.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    /// <summary>Creates a source that cancels after <see cref="Budget"/>, independent of any caller token.</summary>
    /// <returns>A new source the caller must dispose.</returns>
    public static CancellationTokenSource Create() => new(Budget);
}
