using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Tasks.Executors.FanOut;

/// <summary>Join outcome of a fan-out batch: whether the batch as a whole succeeded.</summary>
/// <param name="IsSuccess">Whether the join policy considers the batch successful.</param>
/// <param name="ErrorMessage">Actionable failure detail when <paramref name="IsSuccess"/> is <c>false</c>; <c>null</c> otherwise.</param>
public sealed record FanOutJoinOutcome(bool IsSuccess, string? ErrorMessage);

/// <summary>
/// Pure policy evaluation over settled fan-out item results — no executor machinery, no I/O.
/// </summary>
/// <remarks>
/// <para><c>All</c> — every item must succeed AND the batch must not have timed out. Atomic
/// requirement (e.g. all documents in a batch must be signed).</para>
/// <para><c>AllSettled</c> — ALWAYS succeeds. Partial failure is data, not an error; the flow
/// designer branches on the result summary via auto-transitions. This is the expected common case,
/// and it succeeds even when the batch timed out.</para>
/// <para><c>Quorum</c> — succeeds when <c>succeeded &gt;= minSuccess</c>. Used for scoring and
/// multi-source lookups; judges purely on the success count, regardless of <c>timedOut</c>.</para>
/// <para><c>FirstSuccess</c> — succeeds when at least one item succeeded (the executor cancels the
/// rest once one does). Used for redundant sources / failover lookups; judges purely on the success
/// count, regardless of <c>timedOut</c>.</para>
/// <para>
/// <strong>Empty batch:</strong> succeeds for <c>All</c>, <c>AllSettled</c> and <c>Quorum</c> — a
/// no-op batch is not a failure. For <c>Quorum</c> this is an explicit carve-out, not a fallout of
/// the threshold comparison: a validly configured quorum always has <c>minSuccess &gt;= 1</c>
/// (enforced by <c>FanOutTask.Configure</c>), so <c>succeeded (0) &gt;= minSuccess</c> would be
/// false on an empty batch and the naive rule would fail it. <c>FirstSuccess</c> is the one policy
/// that does NOT get the carve-out: it is defined as "at least one success", and zero items cannot
/// produce one, so it FAILS on an empty batch.
/// </para>
/// </remarks>
public static class FanOutJoinEvaluator
{
    /// <summary>
    /// Evaluates whether a fan-out batch succeeded, given its join policy and settled item results.
    /// </summary>
    /// <param name="policy">The configured join policy.</param>
    /// <param name="minSuccess">
    /// Minimum successful items required for <see cref="FanOutJoinPolicy.Quorum"/>. Guaranteed
    /// non-null and &gt;= 1 by <c>FanOutTask.Configure</c> validation for that policy; the
    /// <c>?? 1</c> fallback below is defensive for callers that bypass that validation.
    /// </param>
    /// <param name="items">Every item's settled outcome.</param>
    /// <param name="timedOut">Whether the batch hit <c>batchTimeoutSeconds</c> before every item settled.</param>
    /// <returns>The join outcome: success flag plus an actionable message when it failed.</returns>
    public static FanOutJoinOutcome Evaluate(
        FanOutJoinPolicy policy,
        int? minSuccess,
        IReadOnlyList<FanOutItemResult> items,
        bool timedOut)
    {
        var succeeded = items.Count(i => i.IsSuccess);
        var failed = items.Count - succeeded;

        return policy switch
        {
            // AllSettled always succeeds, timedOut included - that is its whole point.
            FanOutJoinPolicy.AllSettled => new FanOutJoinOutcome(true, null),

            FanOutJoinPolicy.All when timedOut => new FanOutJoinOutcome(false,
                $"FanOut batch timed out with join policy 'all' ({succeeded}/{items.Count} succeeded)."),
            FanOutJoinPolicy.All when failed > 0 => new FanOutJoinOutcome(false,
                $"FanOut join policy 'all' failed: {failed}/{items.Count} item(s) failed."),
            FanOutJoinPolicy.All => new FanOutJoinOutcome(true, null),

            // Quorum/FirstSuccess judge purely on the success count - timedOut carries no
            // independent weight for them, unlike for All.
            // Empty-batch carve-out: a validly configured quorum has minSuccess >= 1, so the
            // threshold comparison alone would fail an empty batch. The table treats "no items to
            // judge" as a no-op success for quorum (unlike firstSuccess) - special-case it here.
            FanOutJoinPolicy.Quorum when items.Count == 0 => new FanOutJoinOutcome(true, null),
            FanOutJoinPolicy.Quorum when succeeded >= (minSuccess ?? 1) => new FanOutJoinOutcome(true, null),
            FanOutJoinPolicy.Quorum => new FanOutJoinOutcome(false,
                $"FanOut join policy 'quorum' not met: {succeeded}/{items.Count} succeeded, minSuccess={minSuccess?.ToString() ?? "1 (default)"}."),

            FanOutJoinPolicy.FirstSuccess when succeeded >= 1 => new FanOutJoinOutcome(true, null),
            FanOutJoinPolicy.FirstSuccess => new FanOutJoinOutcome(false,
                $"FanOut join policy 'firstSuccess' failed: no item succeeded ({items.Count} attempted)."),

            _ => new FanOutJoinOutcome(false, $"Unknown join policy '{policy}'.")
        };
    }
}
