using BBT.Workflow.Definitions;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Mapping contract for <c>FanOutTask</c>. Domain teams author the implementation as a C# script
/// and ship it in the task's <c>mapping</c> field; the runtime compiles it and drives one item
/// handler invocation per item of the resolved collection, in parallel, then a single output handler
/// for the whole batch.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="IMapping"/> conventions: input binding mutates the (cloned) inner task and the
/// returned <see cref="ScriptResponse"/> is audit data only; the output handler's data is what gets
/// merged into instance data.
/// </para>
/// <para>
/// <strong>The single write point.</strong> <see cref="ItemInputHandler"/> runs once per item, in
/// parallel with every other item's invocation, on that item's own isolated branch
/// <see cref="ScriptContext"/> which is discarded once the item settles. It MUST be pure with respect
/// to instance data — no writes, no shared mutable state across items. <see cref="OutputHandler"/> is
/// the only place in the whole batch where data is merged into the instance; this is precisely what
/// makes N parallel item handlers safe instead of a data race.
/// </para>
/// </remarks>
public interface IFanOutMapping
{
    /// <summary>
    /// Produces the fan-out item collection when the task defines no <c>itemsPath</c>.
    /// </summary>
    /// <param name="context">The script context for the batch, before any item branch is created.</param>
    /// <returns>
    /// The default implementation returns <c>null</c>, meaning "use <c>itemsPath</c>" instead.
    /// A mapping that relies on <c>itemsPath</c> never needs to implement this member.
    /// Returning non-null while the task also configures <c>itemsPath</c> is an execution error —
    /// the two are alternative, not combinable, item sources.
    /// </returns>
    Task<IEnumerable<dynamic>?> ItemSelector(ScriptContext context)
        => Task.FromResult<IEnumerable<dynamic>?>(null);

    /// <summary>
    /// Binds one item's input by mutating the cloned inner task (endpoint, body, headers, …).
    /// Called once per item, on that item's isolated branch context — never touches instance data.
    /// </summary>
    /// <param name="task">The cloned inner task instance for this item; mutate it directly.</param>
    /// <param name="context">The isolated per-item branch script context.</param>
    /// <param name="item">The item being bound: its index, value and stable key.</param>
    /// <returns>Audit data for this item's input binding; not merged into instance data.</returns>
    Task<ScriptResponse> ItemInputHandler(WorkflowTask task, ScriptContext context, FanOutItem item);

    /// <summary>
    /// Called exactly once per batch, after every item has settled (succeeded, failed, or timed out).
    /// This is the batch's single write point: the returned <see cref="ScriptResponse.Data"/> becomes
    /// the FanOutTask's output and is merged into instance data as one patch.
    /// </summary>
    /// <param name="context">The batch-level script context (not an item branch).</param>
    /// <param name="result">The aggregated outcome of every item in the batch.</param>
    Task<ScriptResponse> OutputHandler(ScriptContext context, FanOutResult result);
}

/// <summary>A single fan-out item handed to <see cref="IFanOutMapping.ItemInputHandler"/>: its position, value and stable key.</summary>
/// <param name="Index">Zero-based position of the item in the resolved collection.</param>
/// <param name="Value">The item's own value, as resolved from <c>itemsPath</c> or <see cref="IFanOutMapping.ItemSelector"/>.</param>
/// <param name="ItemKey">Stable per-item key used for correlation, logging and result ordering.</param>
public sealed record FanOutItem(int Index, dynamic? Value, string ItemKey);

/// <summary>Aggregate outcome of a fan-out batch, handed to <see cref="IFanOutMapping.OutputHandler"/>.</summary>
/// <param name="Total">Total number of items in the batch.</param>
/// <param name="Succeeded">Number of items that completed successfully.</param>
/// <param name="Failed">Number of items that failed or timed out.</param>
/// <param name="TimedOut">Whether the batch as a whole hit <c>batchTimeoutSeconds</c> before every item settled.</param>
/// <param name="Items">Per-item outcomes.</param>
public sealed record FanOutResult(
    int Total,
    int Succeeded,
    int Failed,
    bool TimedOut,
    IReadOnlyList<FanOutItemResult> Items);

/// <summary>Outcome of a single item's execution within a fan-out batch.</summary>
/// <param name="Index">Zero-based position of the item in the resolved collection.</param>
/// <param name="ItemKey">Stable per-item key, matching the one passed to <see cref="IFanOutMapping.ItemInputHandler"/>.</param>
/// <param name="IsSuccess">Whether the item's inner task completed successfully.</param>
/// <param name="Data">The item's output data when successful; <c>null</c> otherwise.</param>
/// <param name="ErrorCode">The failure's error code when unsuccessful; <c>null</c> otherwise.</param>
/// <param name="ErrorMessage">The failure's message when unsuccessful; <c>null</c> otherwise.</param>
/// <param name="Duration">How long the item's execution took.</param>
public sealed record FanOutItemResult(
    int Index,
    string ItemKey,
    bool IsSuccess,
    dynamic? Data,
    string? ErrorCode,
    string? ErrorMessage,
    TimeSpan Duration);
