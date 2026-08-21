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
/// <strong>Empty batch:</strong> succeeds for <c>All</c> (vacuously — every item succeeded because
/// there were none) and <c>AllSettled</c> (always succeeds). It FAILS for the threshold policies,
/// <c>Quorum</c> and <c>FirstSuccess</c> — neither is a special case, both fall straight out of
/// <c>succeeded &gt;= threshold</c>: with zero items <c>succeeded</c> is 0, and a threshold of at
/// least 1 (guaranteed for <c>Quorum</c> by <c>FanOutTask.Configure</c>; definitionally 1 for
/// <c>FirstSuccess</c>) can never be met by zero. <c>FirstSuccess</c> is exactly <c>Quorum</c> with
/// <c>minSuccess = 1</c> — the two must never diverge on the same input, empty batch included.
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
            // independent weight for them, unlike for All. No empty-batch special case: with zero
            // items succeeded is 0, which never clears a threshold >= 1, so both policies fail an
            // empty batch as a direct consequence of the same comparison FirstSuccess uses below.
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
