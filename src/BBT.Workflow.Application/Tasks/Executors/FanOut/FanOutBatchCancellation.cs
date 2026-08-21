using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Owns everything that can cut a fan-out batch short, and is the single place that decides which
/// of those causes explains a given cancelled item.
/// </summary>
/// <remarks>
/// <para>
/// Three sources are kept DISTINCT on purpose — the caller's token, the batch deadline, and the
/// early-stop signal — because an item's outcome has to say WHY it stopped. Folding them into one
/// linked source would make "the batch timed out" indistinguishable from "the join policy stopped
/// early", and the batch's <c>TimedOut</c> flag is derived from exactly that distinction.
/// </para>
/// <para>
/// They live behind one type rather than being threaded through the executor as four parameters so
/// that the priority order among them is stated once, here, by <see cref="Classify"/>. Adding a
/// fourth cause should mean editing this file and nothing else.
/// </para>
/// </remarks>
internal sealed class FanOutBatchCancellation : IDisposable
{
    private readonly FanOutTask _task;
    private readonly CancellationToken _callerToken;
    private readonly CancellationTokenSource _batchDeadline;
    private readonly CancellationTokenSource _earlyStop;
    private readonly CancellationTokenSource _batchCts;

    private FanOutBatchCancellation(FanOutTask task, CancellationToken callerToken)
    {
        _task = task;
        _callerToken = callerToken;
        _batchDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(task.BatchTimeoutSeconds));
        _earlyStop = new CancellationTokenSource();
        _batchCts = CancellationTokenSource.CreateLinkedTokenSource(
            callerToken, _batchDeadline.Token, _earlyStop.Token);
    }

    /// <summary>Arms the batch deadline and starts listening for the caller's cancellation.</summary>
    public static FanOutBatchCancellation Start(FanOutTask task, CancellationToken callerToken) =>
        new(task, callerToken);

    /// <summary>The token every item's queueing and execution observes.</summary>
    public CancellationToken Token => _batchCts.Token;

    /// <summary>
    /// Whether the CALLER cancelled — the one cause that must propagate as an exception instead of
    /// being recorded as a failed item, because it means the transition itself is being torn down.
    /// </summary>
    public bool CallerCancelled => _callerToken.IsCancellationRequested;

    /// <summary>
    /// Opens one item's execution window: its own deadline source plus the token that combines it
    /// with the batch's. Call this once the item has ACQUIRED its concurrency slots — the per-item
    /// deadline measures execution, not time spent queueing behind other items.
    /// </summary>
    public ItemWindow OpenItemWindow() => new(_task.ItemTimeoutSeconds, Token);

    /// <summary>
    /// Names the reason an item's execution was cancelled, narrowest cause first.
    /// </summary>
    /// <param name="item">The item whose execution was cancelled.</param>
    /// <param name="window">
    /// The item's window, or null when it was cancelled before one was opened (i.e. while still
    /// queueing for a slot).
    /// </param>
    /// <remarks>
    /// The item's own deadline wins: a slow item that blew its <c>itemTimeoutSeconds</c> at the same
    /// moment a sibling triggered the early stop was stopped by its own deadline, and reporting it
    /// as "cancelled by early stop" would hide the item that is actually misbehaving. The batch
    /// deadline is checked before early stop for the same reason — it cancels the early-stop-linked
    /// token too, so order is what keeps the two apart.
    /// </remarks>
    public (string Code, string Message) Classify(FanOutItem item, ItemWindow? window)
    {
        if (window?.DeadlineExpired == true)
        {
            return (FanOutErrorCodes.ItemTimeout,
                $"FanOut item {item.ItemKey} exceeded the item timeout ({_task.ItemTimeoutSeconds}s).");
        }

        if (_batchDeadline.IsCancellationRequested)
        {
            return (FanOutErrorCodes.BatchTimeout,
                $"FanOut item {item.ItemKey} was cut short by the batch timeout ({_task.BatchTimeoutSeconds}s).");
        }

        if (_earlyStop.IsCancellationRequested)
        {
            return (FanOutErrorCodes.ItemCancelled,
                $"FanOut item {item.ItemKey} was cancelled by the '{_task.JoinPolicy}' join policy's early stop.");
        }

        return (FanOutErrorCodes.ItemNotStarted,
            $"FanOut item {item.ItemKey} was cancelled before it started executing.");
    }

    /// <summary>
    /// Cancels the remaining items once the join policy's verdict can no longer change:
    /// <c>firstSuccess</c> on the first success, <c>all</c> on the first failure. The other
    /// policies need every item's outcome and never stop early.
    /// </summary>
    public void SignalEarlyStop(bool itemSucceeded)
    {
        var decided = _task.JoinPolicy switch
        {
            FanOutJoinPolicy.FirstSuccess => itemSucceeded,
            FanOutJoinPolicy.All => !itemSucceeded,
            _ => false
        };

        if (decided && !_earlyStop.IsCancellationRequested)
        {
            _earlyStop.Cancel();
        }
    }

    public void Dispose()
    {
        _batchCts.Dispose();
        _earlyStop.Dispose();
        _batchDeadline.Dispose();
    }

    /// <summary>
    /// One item's execution window: its private deadline, and the token that combines that deadline
    /// with the batch's causes.
    /// </summary>
    /// <remarks>
    /// The deadline lives in its OWN source rather than as a <c>CancelAfter</c> on the linked token
    /// for the same reason the batch deadline does — only a source nothing else can cancel lets
    /// <see cref="Classify"/> say that THIS item's deadline is what stopped it, rather than
    /// inheriting the batch's or a sibling's explanation.
    /// </remarks>
    internal sealed class ItemWindow : IDisposable
    {
        private readonly CancellationTokenSource _deadline;
        private readonly CancellationTokenSource _linked;

        internal ItemWindow(int itemTimeoutSeconds, CancellationToken batchToken)
        {
            _deadline = new CancellationTokenSource(TimeSpan.FromSeconds(itemTimeoutSeconds));
            _linked = CancellationTokenSource.CreateLinkedTokenSource(batchToken, _deadline.Token);
        }

        /// <summary>The token the item's inner task execution observes.</summary>
        public CancellationToken Token => _linked.Token;

        /// <summary>Whether this item's own deadline is what fired.</summary>
        public bool DeadlineExpired => _deadline.IsCancellationRequested;

        public void Dispose()
        {
            _linked.Dispose();
            _deadline.Dispose();
        }
    }
}
