using BBT.Workflow.BackgroundJobs.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Execution.Cancellation;

/// <summary>
/// Default <see cref="IRunCancellation"/>: a singleton that builds each outermost run's token from
/// <see cref="IHostApplicationLifetime.ApplicationStopping"/> linked with a
/// <see cref="WorkflowExecutionOptions.TransitionJobTimeoutSeconds"/> timer — the same budget the
/// background transition job runs on, so the HTTP and job paths share one contract. The open lease is
/// kept in an <see cref="AsyncLocal{T}"/>: it flows into the callees of the method that opened it (a
/// nested in-process entry reuses it) but never back to that method's caller.
/// </summary>
public sealed class RunCancellation(
    IHostApplicationLifetime lifetime,
    IOptions<WorkflowExecutionOptions> options,
    TimeProvider timeProvider) : IRunCancellation
{
    private static readonly AsyncLocal<RunCancellationLease?> Current = new();

    /// <inheritdoc />
    public RunCancellationLease Begin(CancellationToken callerToken)
    {
        if (Current.Value is { IsDisposed: false } open)
            return new RunCancellationLease(callerToken, open.Token);

        var timeoutSource = new CancellationTokenSource(
            TimeSpan.FromSeconds(options.Value.TransitionJobTimeoutSeconds), timeProvider);
        var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.ApplicationStopping, timeoutSource.Token);

        var previous = Current.Value;
        var lease = new RunCancellationLease(callerToken, timeoutSource, linkedSource, disposedLease =>
        {
            // Only clear the slot this lease filled; restore what was there before it opened.
            if (ReferenceEquals(Current.Value, disposedLease))
                Current.Value = previous;
        });
        Current.Value = lease;
        return lease;
    }
}
