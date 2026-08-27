namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Error codes a fan-out batch stamps on a failed item, surfaced in the batch output under
/// <c>errorCode</c>.
/// </summary>
/// <remarks>
/// These strings are part of the task's PUBLIC contract: workflow authors branch on them in
/// output handlers, auto-transition conditions and error-boundary rules, so they are constants
/// in one place rather than literals scattered across the executor and its tests. Codes produced
/// by the inner task itself pass through unchanged — only failures the fan-out machinery itself
/// decides on are named here.
/// </remarks>
public static class FanOutErrorCodes
{
    /// <summary>The item exceeded the task's <c>itemTimeoutSeconds</c>.</summary>
    public const string ItemTimeout = "FanOut:ItemTimeout";

    /// <summary>The item was cut short because the batch hit <c>batchTimeoutSeconds</c>.</summary>
    public const string BatchTimeout = "FanOut:BatchTimeout";

    /// <summary>
    /// The item was cancelled by the join policy's early stop — a sibling already decided the
    /// batch's verdict (<c>firstSuccess</c> succeeded, or <c>all</c> failed).
    /// </summary>
    public const string ItemCancelled = "FanOut:ItemCancelled";

    /// <summary>
    /// The item was cancelled while still queueing for a concurrency slot, with no batch deadline
    /// or early stop to explain it — the cause is genuinely unknown, so it is not attributed to
    /// one of the named causes above.
    /// </summary>
    public const string ItemNotStarted = "FanOut:ItemNotStarted";

    /// <summary>
    /// The item failed and the failure carried no more specific code — an inner task that threw,
    /// or an engine outcome with no normalized error code of its own.
    /// </summary>
    public const string ItemFailed = "FanOut:ItemFailed";
}
