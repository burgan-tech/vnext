namespace BBT.Workflow.Execution.Cancellation;

/// <summary>
/// Supplies the cancellation token an HTTP-entered run (start, transition) executes on instead of the
/// request token (vnext#1258). Once a run has committed its Busy reservation it must reach a rest point:
/// a client that disconnects (closed tab, client-side timeout) cancels the request token, and running the
/// pipeline, the inline auto-chain and the post-commit work on that token left the instance Busy forever.
/// The run token is the background-job contract brought to the HTTP path: only host shutdown or the
/// <c>WorkflowExecution:TransitionJobTimeoutSeconds</c> budget cancels it.
/// </summary>
public interface IRunCancellation
{
    /// <summary>
    /// Starts a run that the caller's token cannot cancel; only host shutdown or the
    /// TransitionJobTimeoutSeconds budget can. Inside an open lease (an in-process nested entry, such as
    /// a co-hosted subflow start) it returns a lease on that outer run's token, so a nested entry never
    /// grants itself a fresh budget.
    /// </summary>
    /// <param name="callerToken">
    /// The caller's (request) token. It never cancels the run; it is only read to report
    /// <see cref="RunCancellationLease.CallerAborted"/>.
    /// </param>
    /// <returns>A lease the caller must dispose when the run ends.</returns>
    RunCancellationLease Begin(CancellationToken callerToken);
}

/// <summary>
/// One run's cancellation scope, returned by <see cref="IRunCancellation.Begin"/>. The outermost lease
/// owns the timeout and linked sources and disposes them; a nested lease only shares the outer token and
/// its disposal touches nothing the outer lease owns.
/// </summary>
public sealed class RunCancellationLease : IDisposable
{
    private readonly CancellationToken callerToken;
    private readonly CancellationTokenSource? timeoutSource;
    private readonly CancellationTokenSource? linkedSource;
    private readonly Action<RunCancellationLease>? onOwnerDisposed;
    private int disposed;

    /// <summary>Creates a lease that owns its sources (the outermost run).</summary>
    internal RunCancellationLease(
        CancellationToken callerToken,
        CancellationTokenSource timeoutSource,
        CancellationTokenSource linkedSource,
        Action<RunCancellationLease> onOwnerDisposed)
    {
        this.callerToken = callerToken;
        this.timeoutSource = timeoutSource;
        this.linkedSource = linkedSource;
        this.onOwnerDisposed = onOwnerDisposed;
        Token = linkedSource.Token;
    }

    /// <summary>Creates a non-owning lease that shares an open outer run's token.</summary>
    internal RunCancellationLease(CancellationToken callerToken, CancellationToken sharedToken)
    {
        this.callerToken = callerToken;
        Token = sharedToken;
    }

    /// <summary>The token the run executes on. Cancelled only by host shutdown or the run budget.</summary>
    public CancellationToken Token { get; }

    /// <summary>
    /// <c>true</c> when the caller's token has been cancelled (the client disconnected); read at the
    /// time of the call, so a check after the run reports a disconnect that happened during it.
    /// </summary>
    public bool CallerAborted => callerToken.IsCancellationRequested;

    /// <summary>Whether this lease owns (and disposes) the run's sources.</summary>
    internal bool IsOwner => linkedSource is not null;

    /// <summary>Whether this lease has been disposed.</summary>
    internal bool IsDisposed => Volatile.Read(ref disposed) != 0;

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0 || !IsOwner)
            return;

        onOwnerDisposed?.Invoke(this);
        linkedSource!.Dispose();
        timeoutSource!.Dispose();
    }
}
