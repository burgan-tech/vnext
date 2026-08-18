# Script Compile Race and Output-Mapping Failure Classification — Design

**Date:** 2026-08-17 (revised 2026-08-18 — corrected §1c, added §5.4, closed three gaps in
§5.2/§5.3/§5.4; §5.3 reworked during implementation review to derive the scope from the load context
rather than pass it explicitly)
**Status:** Approved (design)
**Scope:** Runtime (`BBT.Workflow.Modules.Scripting`, `BBT.Workflow.Application` — scripting
evaluator and SubFlow terminal services)

## 1. Problem

Under load, subflow completions fail with:

```
Instance:100030 — SubFlow output mapping failed for parent instance '<id>':
Could not load file or assembly 'Script_6A6A92A6A310A0D7, Version=0.0.0.0, ...'.
Assembly with same name is already loaded

System.IO.FileLoadException
   at CSharpEvaluator.CompileAndCacheAsync[T](...) CSharpEvaluator.cs:186
   at SubflowOutputMappingService.ApplyAsync(...) SubflowOutputMappingService.cs:52
```

Three independent conditions intersect. None is sufficient alone.

**(a) Compilation is check-then-act, not atomic.** `CSharpEvaluator.CompileToInstanceAsync`
(`CSharpEvaluator.cs:78-91`) does `TryGetValue` → miss → Roslyn emit (~100–500 ms) →
`LoadFromStream` → `TryAdd`. There is no `GetOrAdd` and no `Lazy<T>`. The assembly's simple name is
`Script_{cacheKey[..16]}`, derived from the cache key, so two concurrent compilations of the same
script produce **the same assembly name**. The vulnerable window is the entire emit duration, not a
narrow instant.

**(b) A declared helper set makes the load context shared.** When a flow declares
`scripts.helpers`, `ScriptEngine` passes the helper set's singleton-lifetime
`AssemblyLoadContext` into the evaluator (`ScriptEngine.cs:264`). Without helpers, `loadContext` is
null and every compilation gets a fresh collectible context
(`CSharpEvaluator.cs:185`), where a name collision is impossible. An `AssemblyLoadContext` cannot
hold two assemblies with the same simple name, so the second `LoadFromStream` throws.

**(c) Concurrent completions of the same flow compile the same mapping.** Every parallel subflow
completion of a given workflow reaches `SubflowOutputMappingService.ApplyAsync` with identical
mapping source, so they share one cache key and one `HelperSet` load context. Whenever more than one
is in flight while the entry is still cold, they all compile and every one but the winner throws.

Note what is **not** the source: duplicate deliveries of the *same* completion cannot race here.
`SubflowCompletionService` serializes them behind a per-`(parent, subInstance)` distributed lock
(`vnext:{domain}:{flow}:{parentId}:sub:{subInstanceId}`), so the second delivery either waits and
short-circuits on `correlation.IsCompleted` or is redelivered. The concurrency comes from *distinct*
instances, each holding a different lock key.

### Why it surfaced recently

The helpers feature landed in `4d49a8fe` (v0.0.60, 2026-06-10). Before it, `loadContext` was always
null, so the same race was harmless — it wasted CPU and produced a duplicate assembly in a
throwaway context. The domain adopting `application-helper@1.0.0` converted a benign race into a
crash. The evaluator's source is byte-identical across v0.0.70 → v0.0.80 → master (blob
`5fff7bc2`), so this is a usage change, not a version regression.

### Why "under load"

Once a compilation succeeds the entry is cached, so the exposure is each pod's cold window — but
load determines how many completions fall inside it. At low volume, completions arrive far enough
apart that the first one warms the cache before the next begins. At high volume, N completions of
the same flow overlap inside that window and N-1 of them fail. Pod churn widens it further: HPA
scale-out and rolling deploys each open a fresh cold window on a pod that is immediately taking
load.

### Consequential damage

The failure does not stop at a logged error. `SubflowCompletionService` treats any failed output
mapping as permanent and faults the parent (§5.4), so each loser terminates an otherwise healthy
instance. That is the flow inconsistency observed in preprod, and it is what makes this urgent
rather than merely noisy.

## 2. Goals

1. A given cache key is compiled at most once per process, regardless of caller concurrency.
2. Loading a script assembly into a load context is idempotent, so a partial failure cannot leave a
   shared context permanently unable to serve that script.
3. The cache key distinguishes load contexts, so two helper sets cannot share one compiled type.
4. Recoverable duplicate assembly loading is resolved inside the evaluator without redelivery;
   loader failures that cannot be proven recoverable produce a visible terminal outcome.

## 3. Non-goals

Explicitly out of scope, tracked separately so they are not lost:

- **Deduplication of subflow terminal event processing.** Already handled: `ISubItemTerminalGuard`
  plus the per-`(parent, subInstance)` lock make correlation completion and output mapping commit in
  one transaction, so a duplicate delivery cannot apply the mapping twice. Making the at-least-twice
  delivery cheaper remains a separate workstream.
- **Helper-set load contexts are never unloaded.** `ScriptHelperRegistry` only unloads on a faulted
  build; a superseded healthy set leaks. Pre-existing, unchanged here.
- **`SubflowFaultService` skipping output mapping on failure.** It logs and proceeds
  (`SubflowFaultService.cs:249-257`), which is correct about not re-faulting an already-faulted
  parent but silently drops the child's data. §5.4 extends the same classification there; the wider
  question of what a fault-path parent should receive is out of scope.

## 4. Existing building blocks (verified)

- `CSharpEvaluator` is registered `TryAddSingleton<IEvaluator>`
  (`TaskServiceCollectionExtensions.cs:270`); it is the only production implementation. Tests add a
  `DelegatingEvaluator` wrapper (`SandboxedScriptingTests.cs:440`).
- `ScriptHelperRegistry` already implements the exact pattern this design adopts: `GetOrAdd` with
  `Lazy<T>` at `LazyThreadSafetyMode.ExecutionAndPublication`, faulted-entry eviction via
  `TryRemove(KeyValuePair)`, and compilation detached from the caller's cancellation token. The
  eviction requirement was learned in `4fcc95af` ("stop caching failed helper-set builds").
- `HelperSet` is a record; the registry already computes its content hash as the local `key` in
  `GetOrBuildHelpers`.
- `ScriptSettings.HasHelpers` requires an explicit non-empty `helpers` list — there is no implicit
  default helper set.
- `SubflowCompletionService` already runs correlation completion and output mapping inside one
  distributed lock and one `correlationUow`, with a `correlation.IsCompleted` short-circuit after
  re-reading under the lock. Duplicate protection is therefore transactional, not best-effort.
- Throwing out of `CompletionAsync` is an established redelivery mechanism in this file:
  `SubflowTerminalLockNotAcquiredException` does exactly that, deliberately, so the broker retries.

## 5. Architecture

### 5.1 Atomic compilation

`_typeCache` becomes `ConcurrentDictionary<string, Lazy<CompiledScript>>`, where `CompiledScript` is
a `readonly record struct (AssemblyLoadContext Context, Type Type)`. Entries are created through
`GetOrAdd` with `LazyThreadSafetyMode.ExecutionAndPublication`, so exactly one thread per cache key
runs the compile-and-load factory and the rest block on the same `Lazy`.

Three deliberate decisions:

**Instance creation stays outside the `Lazy`.** Only `(Context, Type)` is cached.
`Activator.CreateInstance` and `ScriptBase.SetServices` run per call, as they do today. A per-call
failure therefore cannot poison the shared entry, and callers keep receiving distinct instances with
their own injected services.

**Faulted entries are evicted**, mirroring `ScriptHelperRegistry.Evict`:
`_typeCache.TryRemove(new KeyValuePair<string, Lazy<CompiledScript>>(cacheKey, lazy))`. The
key-and-value overload is required — it must never clobber a healthy entry another thread has since
published. Without eviction, `Lazy<T>` replays the cached exception for the remaining process
lifetime, and the evaluator is a singleton.

**Compilation runs with `CancellationToken.None`; the caller's token is checked only on entry.**
Once the compile is shared, one caller disconnecting must not fail the compilation every other
caller is awaiting. This is the same trade-off `ScriptHelperRegistry.BuildHelperSet` already
documents, and the reasoning transfers unchanged.

Blocking is accepted: the alternative is every waiting thread performing the same Roslyn emit.

### 5.2 Idempotent assembly load

In the factory, replace the bare load with a reuse-then-load:

```csharp
var existing = context.Assemblies.FirstOrDefault(a => a.GetName().Name == assemblyName);
var assembly = existing ?? context.LoadFromStream(new MemoryStream(image));
```

`assemblyName` is derived from the hash of the exact compilation inputs, so an assembly already
loaded in that context under that name is by construction the same compilation; reusing it is
correct, not a workaround.

That argument only holds if the name carries the whole hash. Today it is truncated to
`cacheKey[..16]` — 64 bits — which makes reuse *probably* correct rather than correct: two distinct
cache keys sharing a 16-character prefix would cause the second compilation to silently receive the
first one's assembly, where today it would throw. The collision probability is negligible for any
realistic script corpus, but the weakening is free to avoid, so **the assembly name uses the full
cache key**: `Script_{cacheKey}`. Assembly simple names have no practical length limit, the cost is
a longer name in stack traces, and in exchange the reuse rule becomes exact.

This also repairs the sticky-failure path. Today the `matchedType == null` branch unloads only when
`loadContext is null` (`CSharpEvaluator.cs:199-203`); with a shared context the assembly stays loaded
while nothing is cached, so every later attempt throws for the process lifetime. The same applies if
`Activator.CreateInstance` throws between load and cache publication. An assembly cannot be removed
from a live context, so reuse is the only correct recovery. The existing unload-when-owned behaviour
is unchanged for the `loadContext is null` path.

### 5.3 Cache scope

The cache key gains a component identifying the load context the compilation belongs to.
`CSharpEvaluator` derives it from the `AssemblyLoadContext` itself: a private
`ConditionalWeakTable<AssemblyLoadContext, string>` hands each context instance a stable id on first
use, and `GenerateCacheKey` folds that in. A null `loadContext` yields a null scope, so keys on the
no-helper path are unchanged.

This removes a latent correctness bug: `MetadataReference.CreateFromImage(image).Display` is null,
so the helper assembly contributes nothing to the cache key today. Two helper sets exporting the same
namespaces would share one cache entry for identical mapping source, and the second flow would
silently execute the first flow's helper implementations. Relying on `Display` is replaced by an
identity that cannot be absent.

Defence in depth: because the assembly name is derived from the cache key, distinct helper sets now
also produce distinct assembly names, so they cannot collide even within one context.

**Why derive rather than pass it in.** The first version of this section threaded an explicit
`cacheScope` string alongside `loadContext` through `IEvaluator`, `ScriptEngine`, and a new
`HelperSet.Key`. It worked, but the scope and the context must always agree and nothing enforced
that — a caller could supply one without the other. That hazard was not hypothetical: review found
an existing test doing exactly it. Deriving from the context instead is better on every axis raised:

- No parameter is added to `IEvaluator`, which ships in a NuGet-published module — no
  binary-compatibility question, and `CompileToInstanceAsync` does not grow an eighth parameter.
- A scope/context mismatch becomes unrepresentable rather than merely undocumented.
- **The registry invariant disappears.** The earlier design keyed on the helper set's *content*
  hash, which is stable across rebuilds, so it was correct only while `ScriptHelperRegistry` never
  replaced a healthy entry — a property that had to be preserved by documentation at two sites. A
  context-derived scope needs no such promise: a replaced set gets a new context and therefore a new
  scope, automatically.

One consequence worth stating plainly: if a helper set is ever superseded, its old load context and
every assembly in it are **retained for the process lifetime**. The context is not collected —
`_typeCache` holds `CompiledScript.Context` strongly — so the weak keying does not reclaim anything
here. That is still the right trade: a retained context beats serving the wrong compiled code, which
is exactly what the content-hash design would have done. It is recorded as a comment on the table.

(This paragraph originally claimed the entries were "stranded" and the context collected. That was
wrong, and the code comment inherited the error before review caught it — noted because the mistake
is easy to repeat: `ConditionalWeakTable`'s weak key says nothing about what the *value cache*
pins.)

### 5.4 Loader recovery belongs at the source

The assembly-name collision is recoverable only when the evaluator can prove that another caller
loaded the exact same compilation. That proof is the presence of an assembly whose simple name is
the full cache key in the same load context. `CompileAndLoad` therefore scans before loading and, if
`LoadFromStream` still throws `FileLoadException`, scans once more to close the scan/load race across
different evaluator instances. An exact match is reused; without one the original exception is
preserved.

The SubFlow layer must not infer retryability from broad CLR exception types. `FileLoadException`
also represents permanent strong-name, dependency and version failures; `BadImageFormatException`
normally represents a corrupt or incompatible image. Retrying either type generically consumes the
Inbox retry budget (five attempts in the default worker configuration) and can dead-letter the event
while leaving the parent correlation open. Unclassified loader failures therefore follow the normal
failed-`Result` path: completion faults and commits the parent visibly, while the already-faulted path
logs and commits its terminal state.

Genuine caller/host cancellation remains separate: `OperationCanceledException` is rethrown only
when the supplied cancellation token is cancelled. A cancellation-shaped downstream failure with a
live caller token is a permanent mapping failure at this boundary. If a future component has a
provably retryable infrastructure condition, that component must expose a dedicated typed exception;
the SubFlow layer must not reconstruct that meaning from `FileLoadException`, message text, or other
framework exception categories.

| Situation | Behaviour |
|---|---|
| Concurrent callers, same cache key | One compiles; the others block and receive the same `Type`. No exception. |
| Compilation fails (Roslyn / sandbox analyzer) | All waiting callers observe the same exception; the entry is evicted so the next caller recompiles. |
| Assembly already present in the context under that name | Reused; no `FileLoadException`. The name carries the full cache key, so the reuse is exact rather than probabilistic. |
| Unclassified exception during output mapping | Treated as permanent (today's behaviour): incident, fault, commit. |
| No implementing type found | Throws as today; unloads only when the context is owned. The next attempt reuses the loaded assembly rather than failing to load it again. |
| Caller cancels during a shared compile | The compile continues for the other callers; the cancelling caller's token is observed on entry only. |
| Output mapping fails permanently | Unchanged: incident, parent faulted, committed. |
| Duplicate load wins between scan and load | The evaluator rescans and reuses the exact full-key assembly; no redelivery is needed. |
| Unclassified loader failure during output mapping | Failed `Result`; completion faults and commits the parent visibly. |

No new error codes. `Instance:100030` continues to report genuine, permanent output-mapping
failures, including loader failures that could not be proven to be the recoverable duplicate-load race.

## 7. Testing

New tests in `test/BBT.Workflow.Application.Tests/Scripting/`:

1. **Race** — N threads compile the same script into one shared `AssemblyLoadContext`
   simultaneously. Asserts no exception, `CachedTypeCount == 1`, and exactly one underlying compile
   (counted by a counting subclass of the existing abstract `DelegatingEvaluator` test harness).
2. **Faulted compile is not cached** — a script that fails to compile is requested twice; both calls
   throw and no entry remains. Mirrors the helper-registry test added in `4fcc95af`.
3. **Idempotent load** — a context that already holds an assembly with the colliding simple name
   serves it rather than throwing.
4. **Cache scope** — identical mapping source compiled into two different load contexts yields two
   distinct cache entries, each assembly landing in its own context; and two scope-less compiles
   still share one entry. Crucially, this is also covered end-to-end through `ScriptEngine`: two
   helper sets exporting the same namespace and type name but different values, one byte-identical
   mapping, each resolving its own helper. The evaluator-level test alone would pass even with the
   wiring broken.

In `test/BBT.Workflow.Application.Tests/SubFlow/`:

5. **Permanent loader failure** — unclassified `FileLoadException` and
   `BadImageFormatException` values become failed results instead of redelivery signals; completion's
   existing permanent-failure coverage verifies that a failed result faults and commits the parent.

Regression guard: the existing sandbox and helper tests must stay green. Note the known master
baseline of pre-existing failures unrelated to scripting.

## 8. File inventory

| File | Change |
|---|---|
| `modules/BBT.Workflow.Modules.Scripting/.../Evaluators/CSharpEvaluator.cs` | `Lazy` cache, eviction, detached token, idempotent load, `cacheScope` in key |
| `modules/BBT.Workflow.Modules.Scripting/.../Evaluators/IEvaluator.cs` | Unchanged surface — the null-`Display` rationale moves to the derivation |
| `src/BBT.Workflow.Application/SubFlow/Services/SubflowOutputMappingService.cs` | Preserve caller cancellation; convert unclassified failures to a failed result |
| `src/BBT.Workflow.Application/SubFlow/Services/SubflowCompletionService.cs` | Comment update only — the failed-`Result` branch already means "permanent" |
| `src/BBT.Workflow.Application/SubFlow/Services/SubflowFaultService.cs` | Keep log-and-proceed for permanent failures |
| `test/BBT.Workflow.Application.Tests/Scripting/` | Four new tests |
| `test/BBT.Workflow.Application.Tests/SubFlow/` | Permanent loader-failure tests |

## 9. Decisions log

- **`Lazy<T>` over a unique assembly name per load.** Suffixing the assembly name with a counter is a
  one-line fix with no blocking, but it leaves the duplicate work in place and deposits a garbage
  assembly in a shared, never-unloaded context on every race — an unbounded leak. It also breaks the
  deterministic cache-key-to-assembly-name relationship that made this incident diagnosable.
- **`Lazy<T>` over restructuring load-context ownership.** Moving the compiled-mapping cache into
  `HelperSet` would fix the scope bug structurally, but it is a much wider change and the evaluator's
  cache also serves the no-helper path. Rejected for blast radius; §5.3 addresses the same bug
  narrowly.
- **Blocking is accepted.** Waiting threads would otherwise each run the same Roslyn emit.
- **Detached cancellation mirrors `ScriptHelperRegistry`.** Deliberately consistent with the adjacent
  shared-artifact cache rather than inventing a second policy.
- **An earlier reading of this incident was wrong and is recorded here so it is not repeated.** The
  duplicate `DurablePostCommit` processing was initially blamed both for the compile concurrency and
  for a supposed double-apply of output mapping. Neither holds: the per-`(parent, subInstance)` lock
  serializes duplicate deliveries, and correlation completion and output mapping share one
  transaction. The real concurrency source is parallel *distinct* completions (§1c), and the real
  consistency damage is the permanent-fault classification (§5.4).
- **Loader recovery lives in `CSharpEvaluator`, not SubFlow.** Only the evaluator can prove that the
  full-key assembly appeared in the same context between scan and load. A broad CLR exception type
  is not a retryability contract.
- **No speculative transient marker.** There is no current producer of a domain-specific retryable
  script-infrastructure exception, so adding one now would be dead API. A future producer may add a
  typed exception at the source together with bounded-retry tests.
- **The assembly name carries the full cache key rather than a 16-character prefix.** §5.2's reuse
  rule is only sound if the name identifies the compilation uniquely; truncation to 64 bits made
  that probabilistic, and widening it costs nothing but stack-trace length.
- **The cache scope is derived from the load context, not passed alongside it.** An explicit
  `cacheScope` string was implemented first and then withdrawn during review: it made scope/context
  disagreement representable (an existing test already did it), added a parameter to a
  NuGet-published contract, and was correct only while `ScriptHelperRegistry` never replaced a
  healthy entry — an invariant that had to be held up by documentation at two sites. Deriving from
  the context removes all three problems at once. See §5.3.
