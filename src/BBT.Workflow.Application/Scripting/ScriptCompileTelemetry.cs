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
/// <see cref="Record"/>. <see cref="FindTargetActivity"/> resolves the target span as: the first
/// activity from <see cref="Activity.Current"/> up the parent chain that carries
/// <see cref="TelemetryConstants.TagNames.TaskKey"/> (the <c>Task.Execute.*</c> span, or a
/// verbose-mode phase span which carries the same tag), falling back to
/// <see cref="Activity.Current"/> itself for non-task compiles (view rules, event mappings).
/// </para>
/// <para>
/// <b>Capture-before-span ordering.</b> <c>CompileCoreAsync</c> MUST call
/// <see cref="FindTargetActivity"/> and capture the result BEFORE starting the
/// <c>Script.Compile</c> span (<see cref="ScriptActivityHelper.StartCompileActivity"/>), then pass
/// that captured activity to <see cref="Record"/> via the <c>target</c> parameter. The reason is
/// the walk in <see cref="FindTargetActivity"/>: it climbs <see cref="Activity.Parent"/>, and any
/// span started with an EXPLICIT parent context (<c>Activity.Current?.Context</c>) has a
/// <see cref="Activity.Parent"/> of <c>null</c> even though its ParentSpanId is set. Let such a
/// span become current before the target is resolved and the walk terminates on its very first
/// step, silently relocating every accumulator tag and <c>script.compile</c> event onto it instead
/// of the task span. <c>Script.Compile</c> itself is no longer one of those spans —
/// <see cref="ScriptActivityHelper.StartCompileActivity"/> uses the implicit-parent overload so
/// baggage survives — but the capture-first contract is kept deliberately: it makes this class's
/// resolution independent of whatever becomes <see cref="Activity.Current"/> afterward, so a future
/// span inserted on this path cannot reintroduce the bug silently.
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
    /// <param name="target">
    /// The span to accumulate onto, when the caller already resolved it via
    /// <see cref="FindTargetActivity"/> BEFORE starting a child span that would otherwise shadow
    /// the walk (see the capture-before-span remarks on this class). When <c>null</c> (the
    /// default), the target is resolved lazily from <see cref="Activity.Current"/> as before —
    /// correct only when no such child span is current yet.
    /// </param>
    public static void Record(bool cacheMiss, double durationMs, string status, Activity? target = null)
    {
        target ??= FindTargetActivity();
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
    /// else <see cref="Activity.Current"/>. The walk climbs <see cref="Activity.Parent"/>, so a span
    /// started with an explicit parent context — <see cref="Activity.Parent"/> == null despite a set
    /// ParentSpanId — ends it on the very first step, landing on that span's own tag check (a miss)
    /// and then the <c>current</c> fallback, i.e. the span itself. That is why callers about to
    /// start a child span call this method and capture the result BEFORE doing so (see the class
    /// remarks) rather than relying on it to walk past afterward. Internal (not private) so
    /// <c>ScriptEngine</c> can capture the target ahead of
    /// <c>ScriptActivityHelper.StartCompileActivity</c>, and so tests can pin the
    /// capture-before-span contract directly.
    /// </summary>
    internal static Activity? FindTargetActivity()
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
