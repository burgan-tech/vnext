using BBT.Workflow.Definitions;

namespace BBT.Workflow.Tasks.Coordinator;

/// <summary>
/// Per-call execution options for <see cref="ITaskExecutionEngine"/>. Introduced for FanOutTask:
/// items run through the full engine lifecycle (retry, boundary, journal, metrics) but must not
/// each write instance data, and need distinct journal identities.
/// </summary>
/// <remarks>
/// <see cref="Default"/> reproduces the engine's historical behavior in every respect; the
/// options-less <c>ExecuteAsync</c> overload forwards with it, so existing callers are unaffected.
/// </remarks>
public sealed record TaskEngineExecutionOptions
{
    /// <summary>
    /// The default options: no suppression, no journal-key override, no prepared task, no capture.
    /// Equivalent to the behavior of the options-less overload.
    /// </summary>
    public static readonly TaskEngineExecutionOptions Default = new();

    /// <summary>
    /// Preset for tasks running under a FRESHLY INSERTED transition record: identical to
    /// <see cref="Default"/> except the per-task journal idempotency probe is skipped — a new
    /// record id cannot have <c>InstanceTask</c> rows, so the lookup can never find one.
    /// </summary>
    public static readonly TaskEngineExecutionOptions FreshTransitionRecord = new() { SkipJournalProbe = true };

    /// <summary>
    /// When true, task-journal creation skips the <c>FindByTransitionAndTaskAsync</c> idempotency
    /// probe and inserts directly. Only safe when the caller KNOWS no journal row can exist for
    /// this transition record — i.e. the record was inserted by this very pipeline run
    /// (<c>CreateTransitionRecordStep</c> sets the signal). On the retry path the probe must stay:
    /// it is what finds and reuses the previous attempt's rows, including legacy rows without an
    /// <c>ExecutionKey</c>.
    /// </summary>
    public bool SkipJournalProbe { get; init; }

    /// <summary>
    /// When true, the task output is NOT appended to instance data (collect-only execution).
    /// FanOut items use this so the batch has a single write point at the end instead of N racing writes.
    /// </summary>
    public bool SuppressDataApply { get; init; }

    /// <summary>
    /// Overrides the <c>InstanceTask</c> journal key (e.g. <c>"fan-out-docs#3"</c>).
    /// Null means the executed task's own key is used.
    /// </summary>
    public string? JournalTaskKey { get; init; }

    /// <summary>
    /// Pre-built task instance to execute; bypasses the task factory load.
    /// Used when the caller already cloned and bound the task (FanOut binds the inner task per item).
    /// </summary>
    /// <remarks>
    /// IMPORTANT — retry lifetime. The engine's error-aware retry loop re-invokes the core execution
    /// once per attempt. On the factory path that yields a FRESH task instance per attempt; with a
    /// prepared task the very SAME instance is re-executed on every attempt, so any mutation an
    /// executor or mapping applies to it is carried into the next attempt. Callers must ensure the
    /// instance they supply is safe to re-execute — either free of execution-time mutation, or
    /// bound such that re-running it reproduces the same input (the FanOut item semantics: a retry
    /// of the same item with the same input).
    /// </remarks>
    public WorkflowTask? PreparedTask { get; init; }

    /// <summary>
    /// When true, the final <c>StandardTaskResponse</c> is exposed on
    /// <see cref="TasksExecutionResult.Response"/>. Only populated on paths where a response
    /// actually exists; infrastructure failures leave it null.
    /// </summary>
    public bool CaptureResponse { get; init; }
}
