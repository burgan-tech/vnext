using System.Threading.Channels;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Functions;

/// <summary>
/// The captured facts of one completed domain-function invocation, as
/// <c>FunctionAppService.ExecuteFunctionAsync</c> observed it. Carries the caller identity
/// (<see cref="InvokedBy"/>/<see cref="InvokedByBehalfOf"/>) captured at invocation time, because the
/// row is persisted asynchronously — off the request scope — where the ambient user is gone.
/// </summary>
public sealed record FunctionExecutionRecord(
    string Domain,
    string FunctionKey,
    string FunctionVersion,
    TaskScope Scope,
    string? Workflow,
    Guid? InstanceId,
    DateTime InvokedAt,
    double DurationMs,
    bool Succeeded,
    int? StatusCode,
    string? ErrorCode,
    bool FromCache,
    string? InvokedBy = null,
    string? InvokedByBehalfOf = null,
    string? TraceId = null);

/// <summary>
/// Producer-facing port of the function-execution journal (vnext-client-sdk-core#60). A completed
/// invocation is <b>enqueued</b> here and persisted later by a background writer — the function's own
/// execution path never waits for, and is never affected by, the journal write.
/// </summary>
/// <remarks>
/// Journaling is opt-in per function (<c>executionLog: ENABLED</c>) and best-effort: under sustained
/// load the bounded queue sheds records rather than slowing the functions it records.
/// </remarks>
public interface IFunctionExecutionJournal
{
    /// <summary>
    /// Enqueues one execution record for background persistence. Non-blocking and never throws; returns
    /// <c>false</c> when the record was dropped (the bounded queue was full, or the writer has stopped).
    /// </summary>
    bool Record(FunctionExecutionRecord record);
}

/// <summary>
/// Bounded in-process queue behind <see cref="IFunctionExecutionJournal"/>. A singleton: the producer
/// side (<see cref="Record"/>, called from the function path) and the consumer side (the
/// <c>FunctionExecutionJournalWriter</c> background service, which reads <see cref="Reader"/>) share one
/// channel.
/// </summary>
/// <remarks>
/// <para>
/// The channel is bounded and the producer writes with <see cref="ChannelWriter{T}.TryWrite"/> under
/// <see cref="BoundedChannelFullMode.Wait"/>: <c>TryWrite</c> returns immediately — never blocking a
/// function — and returns <c>false</c> when the queue is full, which is counted as a drop. This is
/// functionally the DropWrite policy (the newest record is discarded when full) but with an observable
/// return, so drops can be reported. Dropping is deliberate: 100% journal completeness is explicitly
/// not required, and a full queue must never become backpressure on function execution. Using Dapr's
/// scheduler for this was a deliberate non-choice: journaling stays in-process to keep high-volume
/// telemetry off the scheduler, with no external dependency in the write path.
/// </para>
/// </remarks>
public sealed class FunctionExecutionJournal : IFunctionExecutionJournal
{
    /// <summary>Default bounded capacity of the in-memory queue.</summary>
    public const int DefaultCapacity = 10_000;

    private readonly Channel<FunctionExecutionRecord> _channel;
    private long _droppedCount;

    /// <param name="capacity">
    /// Bounded queue capacity. Beyond this the queue sheds the newest records (see class remarks).
    /// </param>
    public FunctionExecutionJournal(int capacity = DefaultCapacity)
    {
        _channel = Channel.CreateBounded<FunctionExecutionRecord>(
            new BoundedChannelOptions(capacity < 1 ? DefaultCapacity : capacity)
            {
                // TryWrite never blocks under Wait; it returns false when full (a drop). See class remarks.
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,   // exactly one background writer consumes the queue
                SingleWriter = false   // many concurrent function invocations produce
            });
    }

    /// <inheritdoc />
    public bool Record(FunctionExecutionRecord record)
    {
        if (_channel.Writer.TryWrite(record))
        {
            return true;
        }

        Interlocked.Increment(ref _droppedCount);
        return false;
    }

    /// <summary>The consumer side, read by the background writer.</summary>
    internal ChannelReader<FunctionExecutionRecord> Reader => _channel.Reader;

    /// <summary>Running total of records dropped because the queue was full.</summary>
    internal long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <summary>
    /// Completes the queue so the writer drains what remains and exits. Called on host shutdown; after
    /// it, <see cref="Record"/> returns <c>false</c> (records are dropped) rather than throwing.
    /// </summary>
    internal void Complete() => _channel.Writer.TryComplete();
}
