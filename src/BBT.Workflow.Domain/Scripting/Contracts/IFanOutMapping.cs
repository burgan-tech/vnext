using BBT.Workflow.Definitions;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Mapping contract for <c>FanOutTask</c>. Domain teams author the implementation as a C# script;
/// the runtime compiles it and drives one item handler invocation per item of the resolved
/// collection, in parallel, then a single output handler for the whole batch.
/// </summary>
/// <remarks>
/// WHERE THE SCRIPT IS ATTACHED — not on the task component. A type-22 task component carries only
/// <c>type</c> and <c>config</c>; the mapping rides the WORKFLOW's task binding, the same
/// <c>{ order, task, mapping: { location, code } }</c> slot every other task type uses
/// (<c>OnExecuteTask.Mapping</c>, which the executor reads as
/// <c>context.OnExecuteTask.Mapping</c>). One task component can therefore be bound with different
/// mappings at different call sites. Stated explicitly because the natural assumption — that a
/// task-level contract lives in the task's own JSON — is wrong here and sends authors looking for
/// a <c>mapping</c> field on the component that does not exist.
/// </remarks>
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
/// <para>
/// <strong>Override only what you need.</strong> <see cref="ItemSelector"/> and
/// <see cref="OutputHandler"/> both carry a default implementation whose <c>null</c> return means
/// "I did not override this — use the runtime's behaviour" (<c>itemsPath</c> and the default output
/// packaging respectively). Only <see cref="ItemInputHandler"/> is abstract, because that is the one
/// decision the runtime cannot make for a mapping that exists: a mapping is authored precisely
/// because the flat <c>SetBody(item.Value)</c> binding is not what the inner task needs.
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
    /// <remarks>
    /// Deliberately the ONLY abstract member. Unlike its two siblings, input binding has no return
    /// channel that could signal "not overridden" — the returned response is audit data the executor
    /// discards — so a default would have to silently perform the flat <c>SetBody(item.Value)</c>
    /// binding. An author who mistypes this member's name or signature in a <c>.csx</c> would then get
    /// a batch that compiles, runs, and sends N identical unbound requests to the inner task's
    /// authored endpoint. Keeping it abstract turns that mistake into a compile error.
    /// </remarks>
    Task<ScriptResponse> ItemInputHandler(WorkflowTask task, ScriptContext context, FanOutItem item);

    /// <summary>
    /// Called exactly once per batch, after every item has settled (succeeded, failed, or timed out).
    /// This is the batch's single write point: the returned <see cref="ScriptResponse.Data"/> becomes
    /// the FanOutTask's output and is merged into instance data as one patch.
    /// </summary>
    /// <param name="context">The batch-level script context (not an item branch).</param>
    /// <param name="result">The aggregated outcome of every item in the batch.</param>
    /// <returns>
    /// The default implementation returns <c>null</c>, meaning "use the runtime's default output
    /// packaging" — the same shape a task shipping no mapping at all produces (item results under
    /// <c>join.resultKey</c> plus a <c>{resultKey}Summary</c> of
    /// <c>{total, succeeded, failed, timedOut}</c>). Mirrors <see cref="ItemSelector"/>: a mapping
    /// authored only to bind per-item input never has to reimplement the default output shape to keep
    /// it. Note the signal is a <c>null</c> RESPONSE, not a null <see cref="ScriptResponse.Data"/> —
    /// an overriding handler that deliberately produces no data still replaces the default.
    /// </returns>
    Task<ScriptResponse?> OutputHandler(ScriptContext context, FanOutResult result)
        => Task.FromResult<ScriptResponse?>(null);
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
