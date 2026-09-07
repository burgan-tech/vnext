# Cherry-pick report — `feature/write-path-perf` (last 8) onto `feature/trace-span-tree`

**Date:** 2026-08-27
**Branch:** `feature/trace-span-tree`
**Base before:** `d45fcf8e`
**HEAD after:** `559a9450`
**Result:** all 8 commits landed, in the specified order. Local only — nothing pushed.

All 8 were applied with `git cherry-pick -x`, so each new commit body carries a
`(cherry picked from commit …)` origin line.

---

## Per-commit outcome

| # | Source sha | New sha | Outcome |
|---|-----------|---------|---------|
| 1 | `474cab35` perf(scripting): template-reused compilations, cached sandbox references, single-pass analyzer | `dabc8566` | **CONFLICT** — 1 file, 2 hunks (resolved, see below) |
| 2 | `8aea22cb` perf(scripting): warm up Roslyn at startup and drop the dev JIT pessimizations | `9db6bc3b` | Clean |
| 3 | `1b899dce` fix(scripting): resolve the scoped IScriptEngine from a scope in the warmup service | `30165aba` | Clean |
| 4 | `757b3472` perf(write-path): skip guaranteed-empty task-journal probes, CAS job close, one context per schedule step | `0393f085` | Clean (auto-merged 6 shared files) |
| 5 | `0d5e529b` perf(monitor): batch timeline task reads and bound the task-stats aggregation | `bb7b4e41` | Clean |
| 6 | `cde847c4` perf(journal): set-based task completion and a self-sufficient transition close | `6caecdb1` | Clean |
| 7 | `b1b0abfe` perf(instances): CAS status flips, batched job settles, single-column long-poll arm | `0f23a5b8` | Clean (auto-merged `HandleLongPollTerminationStep.cs`) |
| 8 | `08cad52d` fix(journal): target the owned Json scalar in MarkCompletedAsync SetProperty | `559a9450` | Clean |

No cherry-pick had to be aborted. No instrumentation was dropped, no symbol was
invented, nothing was stubbed.

---

## Conflict resolutions

### Commit 1 — `474cab35`

**File:** `src/BBT.Workflow.Application/Scripting/ScriptEngine.cs`
Both hunks sit in the success path of the compile method, immediately after
`stopwatch.Stop()`.

#### Hunk 1 — cache-outcome label and reported duration

* **Ours (HEAD, trace-span-tree)** computed `durationSeconds` from the local
  stopwatch, derived a two-valued `cache` label (`miss` / `hit`), and — the
  instrumentation part — called
  `ScriptActivityHelper.SetCompileResult(compileActivity, compilation.Compiled, "success")`
  to stamp the compile span.
* **Theirs (`474cab35`, write-path-perf)** replaced the two-valued label with a
  **three-outcome** one (`miss` / `wait` / `hit`, using the new
  `compilation.Waited`) and introduced `reportedDuration`: the evaluator's own
  `compilation.CompileDuration` on a real compile, observed wall time otherwise.
  Their commit message explains why — a single-flight *waiter*'s wall time is
  someone else's compile, so labelling it `hit` inflated hit latency during cold
  bursts.

* **Resolution — both.** Kept their `cache` expression, their `reportedDuration`,
  their `durationSeconds` (now derived from `reportedDuration`) **and** their
  explanatory comment verbatim; then re-appended our `SetCompileResult` span call
  underneath. The two sides are orthogonal: theirs decides *what number and label
  are correct*, ours decides *where that outcome is recorded on the span*. Neither
  reads the other's output, so keeping both is a pure superset. `Waited` and
  `CompileDuration` are members added by this very commit, so nothing dangles.

#### Hunk 2 — `ScriptCompileTelemetry.Record(...)`

* **Ours** passed `stopwatch.Elapsed.TotalMilliseconds` and, crucially, a fourth
  argument `telemetryTarget` — the activity captured *before* the
  `Script.Compile` span is started. That capture-before-span ordering is
  load-bearing on our branch (documented in the comment block above the method and
  in `ScriptCompileTelemetry`'s class remarks): the compile span is started with an
  explicit parent context, so its `Activity.Parent` is null and a lazy re-resolve
  from `Activity.Current` would never walk up to the task-key-carrying ancestor.
* **Theirs** passed the corrected `reportedDuration.TotalMilliseconds` and only
  three arguments (their branch had no `telemetryTarget` overload).

* **Resolution — both.** `ScriptCompileTelemetry.Record(compilation.Compiled,
  reportedDuration.TotalMilliseconds, "success", telemetryTarget)`. Their accurate
  duration flows into the accumulator; our explicit target argument is preserved so
  the accumulator still lands on the task span rather than on whatever happens to
  be `Activity.Current`. Dropping `telemetryTarget` would have silently
  re-orphaned the accumulator — exactly the failure the ordering comment warns
  about — while dropping `reportedDuration` would have reintroduced the inflated
  hit latency.

### Commits 4 and 7 — auto-merged shared files (verified, not blindly trusted)

Git auto-merged the remaining shared files without conflict:
`CreateTransitionRecordStep.cs`, `RunOnEntryTasksStep.cs`, `RunOnExecuteTasksStep.cs`,
`RunOnExitTasksStep.cs`, `ScheduleTransitionsStep.cs`, `TaskExecutionEngine.cs`
(commit 4) and `HandleLongPollTerminationStep.cs` (commit 7).

Because a textual auto-merge can still lose semantics, the tree was built after
commit 4 (0 errors) and the branch's `StepOutcome.ContinueNoWork()` instrumentation
was grepped across all pipeline steps — present in every step that had it before,
including all of the shared files above. Nothing was silently dropped.

---

## Build

`dotnet build vnext.sln -v q --nologo` was run after the commit-1 resolution, after
commit 4, and at the end.

* After commit 1: **0 errors**
* After commit 4: **0 errors**
* Final (HEAD `559a9450`): **0 errors**, 189 warnings

The warning count rose from 180 to 189 purely from the incoming commits' own code
(XML-comment and nullable-annotation warnings in newly added members); none are in
resolved regions.

---

## Tests — `dotnet test test/BBT.Workflow.Application.Tests`

```
Failed: 21, Passed: 1692, Skipped: 8, Total: 1721
```

Compared against the baseline in
`…/scratchpad/master-failures.txt` (58 pre-existing names):

* **20 of the 21 failures are in the baseline.** (The baseline lists 38 names that
  are *not* failing here — consistent with the known `AmbientServiceProvider`
  parallel-collection leakage that makes the baseline run noisier than a
  single-project run.)
* **1 name is not in the baseline:**
  `BBT.Workflow.Caching.CacheSetL1Tests.Second_latest_read_costs_only_the_generation_read`

  **Not attributable to these cherry-picks or to my resolutions.** Evidence:
  1. `git diff d45fcf8e..HEAD --name-only` touches **no** caching file at all — the
     8 commits do not go near the component-cache L1 path.
  2. The test was run at the pre-cherry-pick base `d45fcf8e` in a throwaway git
     worktree and **fails there identically** (`Failed: 1, Passed: 6`). It is
     pre-existing on this branch and simply absent from the master-derived
     baseline.

  The assertion is `harness.Cache.Reads should be ["sys-views:core:account-type-
  selection-view:gen"] but was []` — the second latest-read performed no generation
  read at all. Worth someone's attention on the branch, but it predates this work.

* **New test files arriving with the cherry-picks all pass.** No failure comes from
  `CSharpEvaluatorUsingMergeTests`, `ScriptCompilePerfHarness`,
  `InstanceTaskRepositoryTests`, or `EfCoreInstanceTaskRepositoryTests`. So there is
  nothing in the "test arrived broken with its commit" category either — the two
  categories are distinguished and both are empty apart from the pre-existing
  `CacheSetL1Tests` case above.

---

## Scope discipline

* Nothing was modified outside what the cherry-picks required. The only hand-edit in
  the whole run is the two-hunk resolution in `ScriptEngine.cs`.
* gRPC transport defaults untouched — `git diff d45fcf8e..HEAD --name-only` matches
  no gRPC/transport file; the branch stays `Transport: "http"` with no active
  `--app-protocol grpc`.
* No integration suite run, no apps started, nothing pushed.

## `git log --oneline -10`

```
559a9450 fix(journal): target the owned Json scalar in MarkCompletedAsync SetProperty
0f23a5b8 perf(instances): CAS status flips, batched job settles, single-column long-poll arm
6caecdb1 perf(journal): set-based task completion and a self-sufficient transition close
bb7b4e41 perf(monitor): batch timeline task reads and bound the task-stats aggregation
0393f085 perf(write-path): skip guaranteed-empty task-journal probes, CAS job close, one context per schedule step
30165aba fix(scripting): resolve the scoped IScriptEngine from a scope in the warmup service
9db6bc3b perf(scripting): warm up Roslyn at startup and drop the dev JIT pessimizations
dabc8566 perf(scripting): template-reused compilations, cached sandbox references, single-pass analyzer
d45fcf8e refactor(telemetry): make the propagator's no-op structural and its repairs observable
290a8ea5 fix(telemetry): make the gRPC hop one trace tree again
```
