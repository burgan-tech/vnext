using System.Diagnostics;

namespace BBT.Workflow.Logging;

/// <summary>
/// Counter tags for work that was AVOIDED — the cases a span cannot represent because nothing ran.
/// </summary>
public static class ActivityCounterExtensions
{
    /// <summary>
    /// Increments an integer tag on <paramref name="activity"/>, starting at 1.
    /// </summary>
    /// <remarks>
    /// Read-modify-write via <see cref="Activity.GetTagItem"/> + <see cref="Activity.SetTag"/>:
    /// SetTag replaces an existing key rather than appending, so repeated calls accumulate instead
    /// of piling up duplicate tags. No synchronization — an Activity belongs to the logical
    /// operation that started it, and these call sites run on that operation's own flow. If a
    /// future call site increments from genuinely parallel branches, it must count locally and set
    /// the tag once at the join instead of calling this per branch.
    /// <para>
    /// A null activity is a no-op: with no listener attached there is nothing to tag, and every
    /// call site is a hot path that must not branch on telemetry being enabled.
    /// </para>
    /// </remarks>
    public static void IncrementCounterTag(this Activity? activity, string tagName)
    {
        if (activity is null)
            return;

        var current = activity.GetTagItem(tagName) as int? ?? 0;
        activity.SetTag(tagName, current + 1);
    }
}
