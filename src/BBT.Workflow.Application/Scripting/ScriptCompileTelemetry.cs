using System.Diagnostics;
using System.Runtime.CompilerServices;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Accumulates script-compilation cost onto the nearest task span so the compiler's share of a
/// task's duration is visible in traces even without inspecting the dedicated <c>Script.Compile</c>
/// span (see <see cref="ScriptActivityHelper"/>) — the accumulator stays for span-tag-level queries
/// and back-compat with dashboards built before that span existed.
/// <para>
/// Every compilation funnels through <c>ScriptEngine.CompileCoreAsync</c>, which calls
/// <see cref="Record"/>. The target span is resolved as: the first activity from
/// <see cref="Activity.Current"/> up the parent chain that carries
/// <see cref="TelemetryConstants.TagNames.TaskKey"/> (the <c>Task.Execute.*</c> span, or a
/// verbose-mode phase span which carries the same tag), falling back to
/// <see cref="Activity.Current"/> itself for non-task compiles (view rules, event mappings).
/// </para>
/// <para>
/// Tags written (cumulative per span): <c>vnext.script.compile.count</c>,
/// <c>vnext.script.compile.miss.count</c>, <c>vnext.script.compile.total_ms</c>.
/// A cache miss (a compile that actually ran) or any failure additionally emits a
/// <c>script.compile</c> event so the compile window is visible on the span timeline.
/// Cache hits only accumulate tags — an event per hit would flood spans that execute
/// dozens of already-warm scripts.
/// </para>
/// </summary>
public static class ScriptCompileTelemetry
{
    /// <summary>Event name for a compilation that actually ran (cache miss) or failed.</summary>
    public const string CompileEventName = "script.compile";

    private sealed class Counters
    {
        public int Count;
        public int MissCount;
        public double TotalMs;
    }

    // Per-activity accumulator. ConditionalWeakTable so finished/collected activities never leak;
    // the per-Counters lock makes concurrent compiles under one task (FanOut items) race-free.
    private static readonly ConditionalWeakTable<Activity, Counters> State = new();

    /// <summary>
    /// Records one compilation call onto the nearest task span (see class remarks).
    /// </summary>
    /// <param name="cacheMiss">True when the evaluator actually compiled (cache miss).</param>
    /// <param name="durationMs">Wall-clock duration of the compile call in milliseconds.</param>
    /// <param name="status">Outcome status (<c>success</c>, <c>compilation_error</c>, …).</param>
    public static void Record(bool cacheMiss, double durationMs, string status)
    {
        var target = FindTargetActivity();
        if (target is null)
            return;

        var counters = State.GetOrCreateValue(target);
        int count, missCount;
        double totalMs;
        lock (counters)
        {
            counters.Count++;
            if (cacheMiss)
                counters.MissCount++;
            counters.TotalMs += durationMs;
            count = counters.Count;
            missCount = counters.MissCount;
            totalMs = counters.TotalMs;
        }

        // SetTag overwrites, so concurrent writers may interleave — each write is a consistent
        // snapshot taken under the lock, and the final write is the final total.
        target.SetTag(TelemetryConstants.TagNames.ScriptCompileCount, count);
        target.SetTag(TelemetryConstants.TagNames.ScriptCompileMissCount, missCount);
        target.SetTag(
            TelemetryConstants.TagNames.ScriptCompileTotalMs,
            Math.Round(totalMs, 1));

        if (cacheMiss || !string.Equals(status, "success", StringComparison.Ordinal))
        {
            target.AddEvent(new ActivityEvent(CompileEventName, tags: new ActivityTagsCollection
            {
                { "cache", cacheMiss ? "miss" : "hit" },
                { "status", status },
                { "duration_ms", Math.Round(durationMs, 1) }
            }));
        }
    }

    /// <summary>
    /// Resolves the span the compile cost belongs to: self-or-ancestor carrying the task key tag,
    /// else <see cref="Activity.Current"/>. Spans started with an explicit parent context have
    /// <see cref="Activity.Parent"/> == null, which simply ends the walk early — those spans
    /// (verbose phase spans) carry the task key themselves, so the self check still lands.
    /// </summary>
    private static Activity? FindTargetActivity()
    {
        var current = Activity.Current;
        for (var a = current; a is not null; a = a.Parent)
        {
            if (a.GetTagItem(TelemetryConstants.TagNames.TaskKey) is not null)
                return a;
        }

        return current;
    }
}
