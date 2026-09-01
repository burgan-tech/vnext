# Script Compile Observability & Auto-Transition Evaluation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make script compilation readable in the trace tree — which script compiled, whether it was reused, and how often reuse avoided the work — then decide from that evidence whether auto-transition evaluation is worth parallelizing.

**Architecture:** `ScriptCode` already carries a human-readable identity that is lost at the `CompileCoreAsync(string code, …)` boundary; Task 1 threads it down and puts it in the span NAME. Two memos (script-context, mapping-factory) emit nothing on a hit; Task 2 gives their enclosing span a counter tag rather than a span per hit. Task 3 turns the resulting traces into an answer about whether the observed compiles are cold-start or per-request. Task 4 is fully specified but its execution is gated on Task 3.

**Tech Stack:** .NET 10, `System.Diagnostics.Activity` / OpenTelemetry, xUnit + Shouldly + NSubstitute, Elastic (local, `http://localhost:9200`) for trace queries.

**Spec:** `docs/superpowers/specs/2026-08-27-script-compile-observability-spec.md`

## Global Constraints

- **Local commits only on branch `feature/trace-span-tree`. NEVER `git push`.** No branch/merge/rebase.
- **The working tree has uncommitted changes that are NOT yours** — several `launchSettings.json` / `appsettings.json` files are the user's local environment tweaks. **Stage only the files you modify.** Never `git add -A` or `git commit -a`.
- **The cache-hit path must stay allocation-free where it already is.** `vnext.script.key` remains miss-only; do not compute hashes on the hit path. The new identity is different: `ScriptCode.Location` is an already-materialized string, so using it costs nothing.
- **Do not change `vnext.script.key`.** Name readability and tag precision are complementary, not alternatives.
- New tag constants go in `src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs` under `TagNames`, following the existing `vnext.<area>.<thing>` convention, each with an XML `<summary>` saying what it means and when it is set.
- Logging, if any is added, goes through `WorkflowLogs.cs` LoggerMessage source-generated extensions — never raw `logger.Log*`.
- Public types and members get XML `<summary>` docs; comments explain WHY. Match the voice of the file you are editing.
- Regression gate for every task: `dotnet build vnext.sln -v q --nologo` → 0 errors, and `dotnet test test/BBT.Workflow.Application.Tests --nologo -v q` with no NEW failing test name versus `/private/tmp/claude-502/-Users-U0B006-Documents-repos-burgan-tech-vnext/771178c9-bba8-4b0b-8de9-3e512a61e4ae/scratchpad/master-failures.txt`. That baseline carries many pre-existing failures; also ignore the known `CacheSetL1Tests.Second_latest_read_costs_only_the_generation_read`.

---

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `src/BBT.Workflow.Domain/Definitions/ScriptCode.cs` | Owns the readable trace identity of a script (new `TraceIdentity` property) | 1 |
| `src/BBT.Workflow.Application/Scripting/ScriptActivityHelper.cs` | Starts the compile span; now names it after the identity | 1 |
| `src/BBT.Workflow.Application/Scripting/ScriptEngine.cs` | Threads the identity from the `ScriptCode` overloads down to `CompileCoreAsync` | 1 |
| `src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs` | Tag name constants | 1, 2 |
| `src/BBT.Workflow.Domain/Execution/Transitions/Context/TransitionExecutionContext.cs` | Counts script-context memo hits | 2 |
| `src/BBT.Workflow.Application/Tasks/Executors/Core/TaskExecutorBase.cs` | Counts mapping-factory memo hits | 2 |
| `src/BBT.Workflow.Domain/Logging/ActivityCounterExtensions.cs` (new) | The read-modify-write counter helper both memo sites use | 2 |
| `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/Steps/RunAutomaticTransitionsStep.cs` | Auto-transition evaluation loop | 4 |
| `docs/runtime/trace-span-tree.md` | Span reference table | 1, 2 |

---

### Task 1: Give `Script.Compile` a readable identity

**Files:**
- Modify: `src/BBT.Workflow.Domain/Definitions/ScriptCode.cs` (add `TraceIdentity`)
- Modify: `src/BBT.Workflow.Application/Scripting/ScriptActivityHelper.cs:21` (`StartCompileActivity`)
- Modify: `src/BBT.Workflow.Application/Scripting/ScriptEngine.cs` (four `CompileCoreAsync` call sites at ~373, ~377, ~464, ~468; the method itself at ~570; the raw-string path at ~323)
- Modify: `docs/runtime/trace-span-tree.md` (the `Script.Compile` row)
- Test: `test/BBT.Workflow.Domain.Tests/Definitions/ScriptCodeTraceIdentityTests.cs` (create)

**Interfaces:**
- Produces: `ScriptCode.TraceIdentity` → `string` (never null, never empty).
- Produces: `ScriptActivityHelper.StartCompileActivity(string? identity)` → `Activity?`; DisplayName is `Script.Compile/{identity}` when identity is non-empty, else `Script.Compile`.
- Consumes: existing `ScriptCode.Location`, `ScriptCode.DefaultLocation`, `ScriptCode.IsReference`, `ScriptCode.CodeReference`, `ScriptCode.ContentHash`, and `Reference.ToString()` (which renders `{Domain}/{Flow}/{Key}/{Version}`).

- [ ] **Step 1: Write the failing test**

Create `test/BBT.Workflow.Domain.Tests/Definitions/ScriptCodeTraceIdentityTests.cs`:

```csharp
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Definitions;

/// <summary>
/// Pins the identity that names the <c>Script.Compile</c> span.
/// <para>
/// Without it, a transition evaluating three auto-transition rules produced three spans all called
/// <c>Script.Compile</c> — one of them costing 1.5 s — with no way to tell which rule was the
/// expensive one. The identity has to be readable (a path, not a hash) whenever the definition
/// gives us one, which in practice is nearly always: 208 of 209 script blocks in vnext-example
/// carry a real <c>location</c>.
/// </para>
/// </summary>
public sealed class ScriptCodeTraceIdentityTests
{
    [Fact]
    public void Location_WhenAuthored_IsTheIdentity()
    {
        var script = ScriptCode.FromNative("return true;", location: "./src/AlwaysTrueRule.csx");

        script.TraceIdentity.ShouldBe("./src/AlwaysTrueRule.csx");
    }

    [Fact]
    public void InlineLocation_FallsBackToAContentHashPrefix()
    {
        // "inline" is the DEFAULT, i.e. the author gave us nothing — it identifies no script at
        // all, so it must not become the span name. A hash prefix at least separates two different
        // inline scripts from each other.
        var script = ScriptCode.FromNative("return true;");

        script.Location.ShouldBe(ScriptCode.DefaultLocation);
        script.TraceIdentity.ShouldStartWith("inline:");
        script.TraceIdentity.Length.ShouldBeGreaterThan("inline:".Length);
    }

    [Fact]
    public void TwoDifferentInlineScripts_GetDifferentIdentities()
    {
        var a = ScriptCode.FromNative("return true;");
        var b = ScriptCode.FromNative("return false;");

        a.TraceIdentity.ShouldNotBe(b.TraceIdentity);
    }

    [Fact]
    public void ReferenceEncoded_WithoutLocation_UsesTheReference()
    {
        // A reference-encoded script's DecodedCode is empty, so EVERY such script shares the
        // empty-string ContentHash. The reference is the only thing that identifies it.
        var reference = new Reference("shared-rule", "sys-mappings", "core", "1.0.0");
        var script = ScriptCode.FromReference(reference);

        script.TraceIdentity.ShouldBe("core/sys-mappings/shared-rule/1.0.0");
    }

    [Fact]
    public void ReferenceEncoded_WithLocation_PrefersTheLocation()
    {
        var reference = new Reference("shared-rule", "sys-mappings", "core", "1.0.0");
        var script = ScriptCode.FromReference(reference, location: "./src/SharedRule.csx");

        script.TraceIdentity.ShouldBe("./src/SharedRule.csx");
    }
}
```

> NOTE for the implementer: the `Reference` constructor argument ORDER above is a guess. Open
> `src/BBT.Workflow.Domain/Shared/IReference.cs` and use the real one — `ToString()` there renders
> `{Domain}/{Flow}/{Key}/{Version}`, and the assertion above is written against that rendering.
> The ASSERTIONS are the contract; the construction is scaffolding to correct.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~ScriptCodeTraceIdentityTests" --nologo -v q`
Expected: FAIL — `TraceIdentity` does not exist (compile error).

- [ ] **Step 3: Add `TraceIdentity` to `ScriptCode`**

Place it next to `ContentHash` in `src/BBT.Workflow.Domain/Definitions/ScriptCode.cs`:

```csharp
/// <summary>
/// Short, human-readable identity used to NAME this script's <c>Script.Compile</c> span.
/// </summary>
/// <remarks>
/// Precedence is authored-first: an explicit <see cref="Location"/> is what a person recognizes,
/// so it wins. A reference-encoded script without one falls back to the reference, because its
/// <see cref="DecodedCode"/> is empty and every such script would otherwise share the
/// empty-string <see cref="ContentHash"/>. Only a truly anonymous inline script falls through to
/// a hash prefix, which at least tells two of them apart.
/// <para>
/// Cheap on purpose: <see cref="Location"/> is already a materialized string, so the common path
/// allocates nothing. This is why the span NAME can carry identity on every compile while
/// <c>vnext.script.key</c> stays miss-only — that tag hashes, this does not.
/// </para>
/// </remarks>
[JsonIgnore]
public string TraceIdentity =>
    !string.IsNullOrWhiteSpace(Location) && !Location.Equals(DefaultLocation, StringComparison.Ordinal)
        ? Location
        : IsReference && CodeReference is not null
            ? CodeReference.ToString()
            : $"inline:{ContentHash[..Math.Min(8, ContentHash.Length)]}";
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~ScriptCodeTraceIdentityTests" --nologo -v q`
Expected: PASS (5 tests).

- [ ] **Step 5: Name the compile span after the identity**

In `src/BBT.Workflow.Application/Scripting/ScriptActivityHelper.cs`, change `StartCompileActivity` to take an optional identity:

```csharp
/// <summary>
/// Starts the span covering one compile call, named <c>Script.Compile/{identity}</c> so the tree
/// says WHICH script compiled without the reader opening the span.
/// </summary>
/// <param name="identity">
/// <see cref="BBT.Workflow.Definitions.ScriptCode.TraceIdentity"/> when the caller has a
/// <c>ScriptCode</c>. The raw-string compile overloads have none, and fall back to the bare
/// <c>Script.Compile</c> name.
/// </param>
public static Activity? StartCompileActivity(string? identity = null)
{
    var activity = ActivitySource.StartActivity(
        string.IsNullOrEmpty(identity) ? "Script.Compile" : $"Script.Compile/{identity}",
        ActivityKind.Internal,
        Activity.Current?.Context ?? default);

    activity?.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
    return activity;
}
```

> Keep the existing `ActivityKind` and parent-context argument exactly as the current
> implementation has them — read the file and preserve them; only the name and the parameter change.

- [ ] **Step 6: Thread the identity down to `CompileCoreAsync`**

In `src/BBT.Workflow.Application/Scripting/ScriptEngine.cs`:

1. Add a parameter to the private `CompileCoreAsync<T>` (declared around line 570), after `precomputedCacheKey`:

```csharp
string? scriptIdentity = null)
```

2. Use it where the span starts (around line 587):

```csharp
using var compileActivity = ScriptActivityHelper.StartCompileActivity(scriptIdentity);
```

3. At the four call sites that HAVE a `scriptCode` in scope (around lines 373, 377, 464, 468), pass `scriptIdentity: scriptCode.TraceIdentity`. The raw-string path (around line 323) passes nothing and keeps the bare name.

**Do not move the `telemetryTarget` capture.** It must stay immediately BEFORE `StartCompileActivity` — the comment above it explains why (the span is started with an explicit parent context, so a later re-resolve from `Activity.Current` would never walk past it). Your change adds an argument; it must not reorder those two lines.

- [ ] **Step 7: Build and run the regression gate**

Run: `dotnet build vnext.sln -v q --nologo` → 0 errors.
Run: `dotnet test test/BBT.Workflow.Application.Tests --nologo -v q` and `dotnet test test/BBT.Workflow.Domain.Tests --nologo -v q` → no NEW failing test name versus the baseline named in Global Constraints.

- [ ] **Step 8: Update the span reference table**

In `docs/runtime/trace-span-tree.md`, update the `Script.Compile` row: the span is now named
`Script.Compile/{identity}`, identity resolution is location → reference → `inline:{hash8}`, and
`vnext.script.key` is unchanged (still miss-only, still the precise cache key). State why the name
can carry identity on every compile while the tag cannot: `Location` is already materialized, the
tag hashes.

- [ ] **Step 9: Commit**

```bash
git add src/BBT.Workflow.Domain/Definitions/ScriptCode.cs \
        src/BBT.Workflow.Application/Scripting/ScriptActivityHelper.cs \
        src/BBT.Workflow.Application/Scripting/ScriptEngine.cs \
        test/BBT.Workflow.Domain.Tests/Definitions/ScriptCodeTraceIdentityTests.cs \
        docs/runtime/trace-span-tree.md
git commit -m "feat(telemetry): name Script.Compile after the script it compiles"
```

---

### Task 2: Count memo hits on the enclosing span

**Files:**
- Create: `src/BBT.Workflow.Domain/Logging/ActivityCounterExtensions.cs`
- Modify: `src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs` (two new tag constants)
- Modify: `src/BBT.Workflow.Domain/Execution/Transitions/Context/TransitionExecutionContext.cs:202-212` (`GetOrBuildScriptContextAsync`)
- Modify: `src/BBT.Workflow.Application/Tasks/Executors/Core/TaskExecutorBase.cs:299-313` (`GetOrCompileMappingAsync`)
- Modify: `docs/runtime/trace-span-tree.md`
- Test: `test/BBT.Workflow.Domain.Tests/Logging/ActivityCounterExtensionsTests.cs` (create)

**Interfaces:**
- Produces: `public static void IncrementCounterTag(this Activity? activity, string tagName)` in namespace `BBT.Workflow.Logging` — increments an int tag on the activity, starting at 1, no-op when the activity is null.
- Produces: `TelemetryConstants.TagNames.ScriptContextMemoHits = "vnext.script.context.memo.hits"` and `TelemetryConstants.TagNames.MappingFactoryMemoHits = "vnext.script.mapping.memo.hits"`.
- Consumes: nothing from Task 1.

**Why a counter and not a span:** a span per hit would drown the tree — a 100-item FanOut batch
would add 100 of them. The question a reader has is "how often did we avoid the work?", which a
single number answers.

- [ ] **Step 1: Write the failing test**

Create `test/BBT.Workflow.Domain.Tests/Logging/ActivityCounterExtensionsTests.cs`:

```csharp
using System.Collections.Generic;
using System.Diagnostics;
using BBT.Workflow.Logging;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Logging;

/// <summary>
/// Pins the counter tag used to make cache HITS visible.
/// <para>
/// Two memos — the per-transition <c>ScriptContext</c> and the per-execution mapping-factory
/// dictionary — emit nothing at all when they hit, so a trace cannot distinguish "this work was
/// skipped" from "this work never happened". A span per hit would drown the tree (a 100-item
/// FanOut batch would add 100), so the enclosing span carries a count instead.
/// </para>
/// </summary>
public sealed class ActivityCounterExtensionsTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly ActivitySource _source = new("Test.ActivityCounter");

    public ActivityCounterExtensionsTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Test.ActivityCounter",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _source.Dispose();
    }

    [Fact]
    public void FirstIncrement_SetsTheTagToOne()
    {
        using var activity = _source.StartActivity("probe")!;

        activity.IncrementCounterTag("vnext.test.hits");

        activity.GetTagItem("vnext.test.hits").ShouldBe(1);
    }

    [Fact]
    public void RepeatedIncrements_Accumulate()
    {
        using var activity = _source.StartActivity("probe")!;

        activity.IncrementCounterTag("vnext.test.hits");
        activity.IncrementCounterTag("vnext.test.hits");
        activity.IncrementCounterTag("vnext.test.hits");

        activity.GetTagItem("vnext.test.hits").ShouldBe(3);
    }

    [Fact]
    public void NullActivity_IsANoOp()
    {
        Activity? none = null;

        // Must not throw: every call site is on a hot path where no listener may be attached.
        Should.NotThrow(() => none.IncrementCounterTag("vnext.test.hits"));
    }

    [Fact]
    public void SeparateTags_CountIndependently()
    {
        using var activity = _source.StartActivity("probe")!;

        activity.IncrementCounterTag("vnext.test.a");
        activity.IncrementCounterTag("vnext.test.b");
        activity.IncrementCounterTag("vnext.test.a");

        activity.GetTagItem("vnext.test.a").ShouldBe(2);
        activity.GetTagItem("vnext.test.b").ShouldBe(1);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~ActivityCounterExtensionsTests" --nologo -v q`
Expected: FAIL — `IncrementCounterTag` does not exist (compile error).

- [ ] **Step 3: Implement the counter helper**

Create `src/BBT.Workflow.Domain/Logging/ActivityCounterExtensions.cs`:

```csharp
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
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~ActivityCounterExtensionsTests" --nologo -v q`
Expected: PASS (4 tests).

- [ ] **Step 5: Add the two tag constants**

In `src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs`, inside `TagNames`, next to the other `vnext.script.*` constants:

```csharp
/// <summary>
/// How many times a transition reused its already-built <c>ScriptContext</c> instead of building
/// one. Set on the enclosing span. A miss produces the <c>ScriptContext.Build</c> span tree; a hit
/// produced nothing at all before this counter, so the tree could not distinguish "reused" from
/// "never needed".
/// </summary>
public const string ScriptContextMemoHits = "vnext.script.context.memo.hits";

/// <summary>
/// How many times a task execution reused an already-compiled mapping factory. Set on the
/// enclosing span. On a hit the script engine is never called, so no <c>Script.Compile</c> span
/// exists — this counter is the only evidence the compile was avoided.
/// </summary>
public const string MappingFactoryMemoHits = "vnext.script.mapping.memo.hits";
```

- [ ] **Step 6: Count the script-context memo hits**

In `src/BBT.Workflow.Domain/Execution/Transitions/Context/TransitionExecutionContext.cs`, in `GetOrBuildScriptContextAsync`, tag the hit branch:

```csharp
if (Cache.TryGetValue("ScriptContext", out var cached) && cached is ScriptContext scriptContext)
{
    // The build path is spanned (ScriptContext.Build and its children); the reuse path was silent,
    // so a trace could not tell a reused context from one that was never needed.
    Activity.Current.IncrementCounterTag(TelemetryConstants.TagNames.ScriptContextMemoHits);
    return scriptContext;
}
```

Add `using System.Diagnostics;` and `using BBT.Workflow.Logging;` if they are not already present.

- [ ] **Step 7: Count the mapping-factory memo hits**

In `src/BBT.Workflow.Application/Tasks/Executors/Core/TaskExecutorBase.cs`, in `GetOrCompileMappingAsync`, tag the hit branch. The current shape is `if (!context.CompiledMappingFactories.TryGetValue(key, out var boxed)) { … compile … }` — add an `else` (or invert into a hit branch, whichever reads better in that file):

```csharp
else
{
    // A memo hit means the engine is never called, so no Script.Compile span is produced at all.
    // Without this counter the trace shows no compile and no reuse — indistinguishable from a task
    // that has no mapping.
    Activity.Current.IncrementCounterTag(TelemetryConstants.TagNames.MappingFactoryMemoHits);
}
```

- [ ] **Step 8: Build and run the regression gate**

Run: `dotnet build vnext.sln -v q --nologo` → 0 errors.
Run: `dotnet test test/BBT.Workflow.Application.Tests --nologo -v q` and `dotnet test test/BBT.Workflow.Domain.Tests --nologo -v q` → no NEW failing test name versus the baseline named in Global Constraints.

- [ ] **Step 9: Document both counters**

In `docs/runtime/trace-span-tree.md`, add a short subsection covering the three memo layers and how each reports itself: the compile cache (span in both cases, `vnext.script.cache.hit` distinguishes), the script-context memo (span tree on a miss, `vnext.script.context.memo.hits` on the enclosing span for hits), and the mapping-factory memo (`Script.Compile` on a miss, `vnext.script.mapping.memo.hits` for hits). Say explicitly that a counter was chosen over a span per hit because of trace volume.

- [ ] **Step 10: Commit**

```bash
git add src/BBT.Workflow.Domain/Logging/ActivityCounterExtensions.cs \
        src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs \
        src/BBT.Workflow.Domain/Execution/Transitions/Context/TransitionExecutionContext.cs \
        src/BBT.Workflow.Application/Tasks/Executors/Core/TaskExecutorBase.cs \
        test/BBT.Workflow.Domain.Tests/Logging/ActivityCounterExtensionsTests.cs \
        docs/runtime/trace-span-tree.md
git commit -m "feat(telemetry): count script-context and mapping-factory memo hits"
```

---

### Task 3: Measure — are the compiles cold-start or per-request?

**Files:**
- Create: `docs/runtime/script-compile-measurement-2026-08-27.md`

**Interfaces:**
- Consumes: the span names from Task 1 and the counters from Task 2.
- Produces: a documented answer that gates Task 4 and scopes the follow-up plan.

**This task produces a finding, not code.** Its deliverable is a document a reader can act on.

- [ ] **Step 1: Bring the environment up and generate traffic**

Infrastructure and MockLab may already be running — check with `docker ps` first and do NOT run a wholesale `docker compose down` or `up`. If containers are absent: `cd etc/docker && docker compose up -d`, then from `/Users/U0B006/Documents/repos/burgan-tech/vnext-example`: `docker compose up -d` for MockLab.

Build ONCE sequentially (`dotnet build vnext.sln -v q --nologo`) — **never let four `dotnet run` builds race, PostSharp fails with MSB4018 file-move contention.** Then start the four apps in the background, staggered, orchestration first, each with `--launch-profile http --no-build`, waiting for `http://localhost:4201/health` → 200 between starts.

- [ ] **Step 2: Run the same flow TWICE against the same warm process**

From `/Users/U0B006/Documents/repos/burgan-tech/vnext-example`:

```bash
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings --filter "FullyQualifiedName~MoneyTransferTests" --nologo -v q
```

Run it, wait for it to finish, then run it AGAIN without restarting anything. Two runs against one process is the whole experiment: cold-start cost appears only in the first, a broken cache key appears in both.

- [ ] **Step 3: Query Elastic and compare the two runs**

Elastic is at `http://localhost:9200`, indices `.ds-traces-apm*,traces-apm*`. Query with Python `urllib` — curl is blocked for Elastic in this environment. Parse `timestamp.us`, NOT `@timestamp` (its format is rejected by Python's parser).

For each of the two runs, collect every span whose `span.name` starts with `Script.Compile/` and record: the identity suffix, `span.duration.us`, and `labels.vnext_script_cache_hit`.

Then answer, per script identity:
- Did it compile (`cache_hit: false`) in run 1 only, or in BOTH runs?
- **Run 1 only → cold start.** The fix is warm-up coverage.
- **Both runs → the cache key is unstable.** That is a BUG, and parallelizing evaluation would hide it.

Also record the `vnext.script.context.memo.hits` and `vnext.script.mapping.memo.hits` values on the enclosing spans, so the document says how much reuse is already happening.

- [ ] **Step 4: Write the finding**

Create `docs/runtime/script-compile-measurement-2026-08-27.md` containing: the two trace ids, a per-identity table (identity, run-1 duration and cache_hit, run-2 duration and cache_hit), the memo-hit counts, and a one-paragraph verdict naming which of the two causes it is. If the evidence is mixed (some identities cold-start, others recompiling every run), say so — that is a real and useful answer, not a failure to conclude.

State explicitly whether Task 4 is worth executing, using this rule: if every identity compiles only in run 1, the warm-path evaluation loop costs microseconds and **Task 4 should be skipped**; if identities recompile in run 2, **the cache-key bug must be fixed first** and Task 4 remains gated.

- [ ] **Step 5: Stop the apps and commit**

Stop the apps (`pkill -f "dotnet run"`, then `pkill -f "BBT.Workflow"`). Leave the containers running.

```bash
git add docs/runtime/script-compile-measurement-2026-08-27.md
git commit -m "docs(runtime): measure whether auto-transition rule compiles are cold-start or per-request"
```

---

### Task 4 (GATED on Task 3): Evaluate auto-transitions in parallel

**Execute this task ONLY if Task 3's document says it is worth executing.** If the compiles are
cold-start-only, the sequential loop over warm rules costs microseconds and this task is waste. If
the cache key is unstable, fix that first — parallelizing would hide it.

**Files:**
- Modify: `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/Steps/RunAutomaticTransitionsStep.cs:96-123` (`EvaluateAllTransitionsAsync`)
- Test: `test/BBT.Workflow.Application.Tests/Execution/Transitions/RunAutomaticTransitionsParallelTests.cs` (create)

**Interfaces:**
- Consumes: `IAutoConditionEvaluator.EvaluateAsync(Transition, TransitionExecutionContext, CancellationToken)` → `Result<AutoConditionEvaluation>` (unchanged).
- Produces: `EvaluateAllTransitionsAsync` returning the same `Result<List<AutoConditionEvaluation>>` with the same ordering guarantees.

**The three semantics that MUST survive** — the current loop gets them for free by being sequential, and each has to be re-established explicitly:

1. **Priority order.** `context.Target!.AutoTransitions.OrderBy(t => t.TriggerKind)` is a priority ranking, and `ProcessEvaluationResults` picks `FirstOrDefault(e => e.Status == Satisfied)`. Results must be returned in the ORIGINAL ordered sequence, not in completion order, or a lower-priority rule silently wins.
2. **Error selection.** The current loop returns the FIRST failure and abandons the rest. In parallel several may fail; the returned failure must be the first in ORDER, not the first to complete.
3. **Short-circuit is deliberately given up.** Today a satisfied rule stops the loop; in parallel every rule is evaluated. State in a comment that this trades extra compiles for wall-clock, and that those compiles populate the cache rather than being wasted.

**Why sharing one `ScriptContext` across parallel evaluations is safe** (verified — put this reasoning in a comment): `Lazy<T>` fields default to `ExecutionAndPublication`; the copy-on-write `_owned`/`_cowParent` machinery mutates only on WRITE (`SetBody`), never on read; the related-instance memo is a `ConcurrentDictionary` behind a `SemaphoreSlim`; instance data and state do not change during evaluation. **The residual risk is a condition rule that WRITES to the context** — rules return `bool` and must not, but they are user-authored C#. Say so at the call site so nobody later "adds a little data write" to a condition rule.

- [ ] **Step 1: Write the failing tests**

Create `test/BBT.Workflow.Application.Tests/Execution/Transitions/RunAutomaticTransitionsParallelTests.cs` with three tests, using NSubstitute for `IAutoConditionEvaluator`:

- **`Evaluations_AreReturnedInPriorityOrder_NotCompletionOrder`**: stub the evaluator so the LAST transition in priority order completes FIRST (e.g. give the first a `Task.Delay(50)` before returning). Assert the returned list is in the original priority order, and that a satisfied LOW-priority rule does not beat an unsatisfied-then-satisfied HIGH-priority one.
- **`WhenSeveralFail_TheFirstFailureInOrderIsReturned`**: make two transitions fail with distinguishable errors, the later one completing first. Assert the returned `Result.Error` is the earlier one in priority order.
- **`EveryTransition_IsEvaluated_EvenAfterOneIsSatisfied`**: three transitions, the first satisfied. Assert the evaluator was called three times — pinning the deliberate loss of short-circuit so a future reader does not "restore" it and silently reintroduce sequential latency.

Follow the conventions of the neighbouring pipeline-step tests in `test/BBT.Workflow.Application.Tests/Execution/` — read one before writing.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~RunAutomaticTransitionsParallelTests" --nologo -v q`
Expected: the order and error-selection tests may pass incidentally under the sequential implementation; `EveryTransition_IsEvaluated_EvenAfterOneIsSatisfied` MUST fail (the current loop breaks after the first satisfied). Report exactly which failed — that is the one proving the behaviour change.

- [ ] **Step 3: Rewrite the evaluation to run in parallel**

Replace the body of `EvaluateAllTransitionsAsync`:

```csharp
var orderedTransitions = context.Target!.AutoTransitions.OrderBy(t => t.TriggerKind).ToList();

// Evaluated in PARALLEL, reported in PRIORITY ORDER. Task.WhenAll preserves the result array's
// index alignment with the input, which is what keeps ProcessEvaluationResults' FirstOrDefault
// picking the highest-priority satisfied rule rather than whichever finished first.
//
// Every rule is now evaluated even once one is satisfied — the short-circuit is deliberately
// given up. The extra work is a compile per skipped rule, and that compile populates the type
// cache for the next run rather than being discarded.
//
// All evaluations share ONE ScriptContext (the per-transition memo). Safe for concurrent READS:
// its Lazy<T> fields are ExecutionAndPublication, the copy-on-write body machinery mutates only
// on SetBody, and the related-instance memo is a ConcurrentDictionary behind a SemaphoreSlim.
// Instance data and state do not change during evaluation. A condition rule that WRITES to the
// context would break this — rules return bool and must not.
var results = await Task.WhenAll(orderedTransitions.Select(transition =>
    autoConditionEvaluator.EvaluateAsync(transition, context, cancellationToken)));

// First failure in PRIORITY order, not in completion order.
foreach (var result in results)
{
    if (!result.IsSuccess)
        return Result<List<AutoConditionEvaluation>>.Fail(result.Error);
}

return Result<List<AutoConditionEvaluation>>.Ok(results.Select(r => r.Value).ToList());
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~RunAutomaticTransitionsParallelTests" --nologo -v q`
Expected: PASS (3 tests).

- [ ] **Step 5: Run the regression gate, with attention to auto-transition coverage**

Run: `dotnet build vnext.sln -v q --nologo` → 0 errors.
Run: `dotnet test test/BBT.Workflow.Application.Tests --nologo -v q` → no NEW failing test name versus the baseline.

**Any existing test covering auto-transition selection or `RunAutomaticTransitionsStep` deserves
individual attention** — if one breaks, the priority-order or error-selection guarantee was not
preserved. Understand and report it; do not update the assertion to match new output.

- [ ] **Step 6: Verify end to end that a real flow still selects the same transition**

With the environment from Task 3 still available, run a flow that exercises auto-transitions:

```bash
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings --filter "FullyQualifiedName~MoneyTransferTests" --nologo -v q
```

Expected: the same pass count as before the change. Then compare a trace against a pre-change one:
the `Script.Compile/*` spans under one `Step.RunAutomaticTransitions` should now OVERLAP in time
rather than run end to end. Record the before/after wall-clock of that step.

- [ ] **Step 7: Commit**

```bash
git add src/BBT.Workflow.Application/Execution/Transitions/Pipeline/Steps/RunAutomaticTransitionsStep.cs \
        test/BBT.Workflow.Application.Tests/Execution/Transitions/RunAutomaticTransitionsParallelTests.cs
git commit -m "perf(transitions): evaluate auto-transition rules in parallel, report in priority order"
```

---

## Self-Review

**Spec coverage:** Findings 1-4 (identity) → Task 1. Finding 5 (cache-hit tag already correct) →
no task, by design; noted in Task 1 Step 8's doc update. Finding 6 (invisible memos) → Task 2.
Success criterion 3 (measurement) → Task 3. The rejected `N >= 3` threshold → deliberately absent;
Task 4 parallelizes unconditionally. The follow-up fix for whatever Task 3 finds is explicitly out
of scope per the spec and gets its own plan.

**Known soft spots, stated rather than hidden:** (a) Task 1's test constructs a `Reference` with a
guessed argument order — flagged inline for correction against the real type; (b) Task 4 Step 2
expects only ONE of its three tests to fail before the change, and says so rather than pretending
all three go red; (c) Task 3 produces a document, not code, so it has no test cycle — its gate is
that the document answers a specific either/or question.

**Type consistency:** `ScriptCode.TraceIdentity` (Task 1) is consumed only in Task 1.
`IncrementCounterTag` and the two tag constants (Task 2) are used only in Task 2.
`EvaluateAllTransitionsAsync`'s signature is unchanged in Task 4, so `ProcessEvaluationResults`
needs no edit. No task references a symbol another task did not define.
