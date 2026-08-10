namespace BBT.Workflow.Execution.Services;

/// <inheritdoc />
public sealed class TransitionCommitLeaseManager : ITransitionCommitLeaseManager
{
    private IAsyncDisposable? _lease;

    /// <inheritdoc />
    public void Hold(IAsyncDisposable lease)
    {
        ArgumentNullException.ThrowIfNull(lease);

        if (Interlocked.CompareExchange(ref _lease, lease, null) is not null)
            throw new InvalidOperationException("A transition commit lease is already held for this stage.");
    }

    /// <inheritdoc />
    public async ValueTask ReleaseAsync()
    {
        var lease = Interlocked.Exchange(ref _lease, null);
        if (lease is not null)
            await lease.DisposeAsync();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ReleaseAsync();
}
