# Spec: Script Compile Observability & Auto-Transition Evaluation Cost

Date: 2026-08-27 · Status: approved for planning · Owner: platform

## Why

Platform-wide performance work is in progress. The method is: make the trace tree show where time
actually goes, leave no invisible cost, then act on what the numbers say. This spec covers one
region that is currently unreadable — script compilation during automatic-transition evaluation.

## The observed problem

A single transition's auto-evaluation produced three `Script.Compile` spans:

```
1. vnext_script_cache_hit: false — 1,553 ms
2. vnext_script_cache_hit: false —    40 ms
3. vnext_script_cache_hit: false —    22 ms
```

They are indistinguishable. All three are auto-transition rules, but nothing in the span says
WHICH rule. When one of them costs 1.5 s, there is no way to find the offending script.

## Findings (all verified against the code, 2026-08-27)

1. **`Script.Compile` carries no identity in its name.** `ScriptActivityHelper.StartCompileActivity()`
   takes no parameters; every compile renders as the same `Script.Compile`.

2. **An identity tag exists but is hash-shaped and miss-only.** `vnext.script.key`
   (`ScriptEngine.cs:633`) is set only when `compilation.Compiled` is true — deliberately, to keep
   the cache-hit path allocation-free. Its value is an evaluator cache key or a SHA-256 prefix:
   correct, but not human-readable.

3. **The readable identity exists upstream and is populated in practice.** `ScriptCode` carries
   `Location`, `CodeReference`, `ContentHash` and `Type`. In vnext-example **208 of 209** script
   blocks carry a real `location` (`./src/AlwaysTrueRule.csx`, `./src/CaseSettledRule.csx`, …).

4. **The identity is lost at a boundary.** `IScriptEngine`'s public overloads take `ScriptCode`,
   but every path funnels into `CompileCoreAsync(string code, …)` — where the span is started, and
   where the object is already flattened to a raw string. `scriptCode` IS in scope at all four
   `CompileCoreAsync` call sites, so threading an identity down is mechanical.

5. **`vnext.script.cache.hit` already means what it should.** Single set site
   (`ScriptActivityHelper.SetCompileResult` → `!cacheMiss`). It reports the COMPILE cache, not a
   mapping cache. No work needed for the "did we compile or reuse?" question.

6. **Two other memos are entirely invisible — they emit nothing on a hit.**
   - `TransitionExecutionContext.GetOrBuildScriptContextAsync`: on a miss the teammate's new
     `ScriptContext.Build` span tree appears (added in `a9f57b0b`, with `SnapshotInstance`,
     `RefreshInstance`, `MergeBody`, `CloneBranchBody`, `CreateParallelBranch`); on a hit, nothing.
   - `TaskExecutorBase.GetOrCompileMappingAsync` (`CompiledMappingFactories`): on a hit the engine
     is never called, so no `Script.Compile` span exists at all.

   The compile cache already models this correctly — a span in BOTH cases, the difference in a tag.
   The other two should be brought into line.

## Decisions taken

- **Span identity beats caller-supplied context.** `Script.Compile/{location}` names the script;
  the parent span (the transition transaction) already names the transition. Threading the
  transition key down from callers was considered and rejected as invasive.
- **Memo hits get a COUNTER TAG on the parent span, not a span each.** A span per hit would drown
  the tree — a 100-item FanOut batch would add 100 hit spans. A counter answers "how often did we
  avoid the work?" without the volume.
- **No `N >= 3` threshold for parallel evaluation.** It gates on rule COUNT while the cost is
  compile TIME, and the two do not track each other: 5 warm rules (~1 ms total) would be
  parallelized for nothing, while 2 cold rules at 800 ms each would stay sequential and miss the
  biggest win. It also creates two code paths that must both honour priority order, first-match-wins
  and error selection, with the parallel path getting the LESS production exposure.
- **Parallel evaluation may share one `ScriptContext`.** Verified safe for concurrent reads:
  `Lazy<T>` fields default to `ExecutionAndPublication`; the copy-on-write `_owned`/`_cowParent`
  machinery mutates only on WRITE (`SetBody`), not on read; the related-instance memo is a
  `ConcurrentDictionary` behind a `SemaphoreSlim`. Instance data and state do not change during
  evaluation. The residual risk is a rule that WRITES to the context — condition rules return
  `bool` and must not, but they are user-authored C#, so the assumption must be documented at the
  call site.

## Scope

**In scope:** span identity (Findings 1-4), memo-hit counters (Finding 6), a measurement task that
determines whether the observed compiles are once-per-process or per-request, and a fully specified
parallel-evaluation task whose EXECUTION is gated on that measurement.

**Out of scope:** the fix for whatever the measurement finds (warm-up coverage vs. an unstable
cache key) — that gets its own plan once the cause is known. `vnext.script.key` stays exactly as
it is; name readability and tag precision do not conflict.

## Success criteria

1. A trace shows `Script.Compile/AlwaysTrueRule.csx` — the script is identifiable without opening
   the span.
2. A transition that reused a memoized `ScriptContext` or mapping factory reports how many times it
   did so, on the enclosing span.
3. The measurement answers, with trace evidence: are these compiles once-per-process or
   per-request?
4. No new failing test names versus the master baseline; no measurable regression on the cache-hit
   hot path.
