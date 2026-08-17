# Script Assembly Load Context Double-Compile Race — Design

**Date:** 2026-08-17
**Status:** Approved (design)
**Scope:** Runtime (`BBT.Workflow.Modules.Scripting`, `BBT.Workflow.Application`)

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

**(c) The concurrency is structural, not incidental.** `InstanceSubCompletedEvent`,
`InstanceSubFaultedEvent` and `InstanceSubCanceledEvent` are all `EventHookMode.DurablePostCommit`,
so every subflow completion is processed **twice** — local hook plus Inbox forward — and both paths
reach the same `SubflowOutputMappingService.ApplyAsync`. Both callers receive the *same* `HelperSet`
(the registry's `Lazy` guarantees it), both miss `_typeCache`, both compile, and the loser throws.

On a cold cache, losing this race is the expected outcome rather than an edge case.

### Why it surfaced recently

The helpers feature landed in `4d49a8fe` (v0.0.60, 2026-06-10). Before it, `loadContext` was always
null, so the same race was harmless — it wasted CPU and produced a duplicate assembly in a
throwaway context. The domain adopting `application-helper@1.0.0` converted a benign race into a
crash. The evaluator's source is byte-identical across v0.0.70 → v0.0.80 → master (blob
`5fff7bc2`), so this is a usage change, not a version regression.

### Why "under load"

Once a compilation succeeds the entry is cached, so the race is confined to each pod's cold window.
Load triggers HPA scale-out; every new pod opens a fresh cold window, and on that pod one copy of
every concurrent subflow completion fails. Rolling deploys have the same effect.

## 2. Goals

1. A given cache key is compiled at most once per process, regardless of caller concurrency.
2. Loading a script assembly into a load context is idempotent, so a partial failure cannot leave a
   shared context permanently unable to serve that script.
3. The cache key distinguishes load contexts, so two helper sets cannot share one compiled type.

## 3. Non-goals

Explicitly out of scope, tracked separately so they are not lost:

- **Double-apply of subflow output mapping.** Fixing the crash makes *both* processings succeed, and
  both then call `instanceDataWriteService.AppendAsync`. The dual-processing design
  (`EventHookMode.DurablePostCommit`) is the likely source of the observed flow inconsistency, and
  this design does not address it.
- **Helper-set load contexts are never unloaded.** `ScriptHelperRegistry` only unloads on a faulted
  build; a superseded healthy set leaks. Pre-existing, unchanged here.
- **Deduplication of subflow terminal event processing.** Separate workstream.

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

This also repairs the sticky-failure path. Today the `matchedType == null` branch unloads only when
`loadContext is null` (`CSharpEvaluator.cs:199-203`); with a shared context the assembly stays loaded
while nothing is cached, so every later attempt throws for the process lifetime. The same applies if
`Activator.CreateInstance` throws between load and cache publication. An assembly cannot be removed
from a live context, so reuse is the only correct recovery. The existing unload-when-owned behaviour
is unchanged for the `loadContext is null` path.

### 5.3 Cache scope

`HelperSet` gains a `Key` property carrying the registry's content hash (already computed in
`GetOrBuildHelpers`). `ScriptEngine` passes it to the evaluator as an explicit `cacheScope`
argument — `helperSet.Key` on the helper path, `null` otherwise. `GenerateCacheKey` appends
`|alc:{cacheScope}`.

This removes a latent correctness bug: `MetadataReference.CreateFromImage(image).Display` is null,
so the helper assembly contributes nothing to the cache key today. Two helper sets exporting the same
namespaces would share one cache entry for identical mapping source, and the second flow would
silently execute the first flow's helper implementations. Relying on `Display` is replaced by an
explicit identity.

Defence in depth: because the assembly name is derived from the cache key, distinct helper sets now
also produce distinct assembly names, so they cannot collide even within one context.

`cacheScope` is added as an optional trailing parameter on `IEvaluator.CompileToInstanceAsync`, which
keeps existing call sites source-compatible.

## 6. Error semantics

| Situation | Behaviour |
|---|---|
| Concurrent callers, same cache key | One compiles; the others block and receive the same `Type`. No exception. |
| Compilation fails (Roslyn / sandbox analyzer) | All waiting callers observe the same exception; the entry is evicted so the next caller recompiles. |
| Assembly already present in the context under that name | Reused; no `FileLoadException`. |
| No implementing type found | Throws as today; unloads only when the context is owned. The next attempt reuses the loaded assembly rather than failing to load it again. |
| Caller cancels during a shared compile | The compile continues for the other callers; the cancelling caller's token is observed on entry only. |

No new error codes. `Instance:100030` continues to report genuine output-mapping failures.

## 7. Testing

New tests in `test/BBT.Workflow.Application.Tests/Scripting/`:

1. **Race** — N threads compile the same script into one shared `AssemblyLoadContext`
   simultaneously. Asserts no exception, `CachedTypeCount == 1`, and exactly one underlying compile
   (counted by a counting subclass of the existing abstract `DelegatingEvaluator` test harness).
2. **Faulted compile is not cached** — a script that fails to compile is requested twice; both calls
   throw and no entry remains. Mirrors the helper-registry test added in `4fcc95af`.
3. **Idempotent load** — a context that already holds an assembly with the colliding simple name
   serves it rather than throwing.
4. **Cache scope** — identical mapping source compiled against two different helper sets yields two
   distinct cache entries, each resolving its own helper set's types.

Regression guard: the existing sandbox and helper tests must stay green. Note the known master
baseline of pre-existing failures unrelated to scripting.

## 8. File inventory

| File | Change |
|---|---|
| `modules/BBT.Workflow.Modules.Scripting/.../Evaluators/CSharpEvaluator.cs` | `Lazy` cache, eviction, detached token, idempotent load, `cacheScope` in key |
| `modules/BBT.Workflow.Modules.Scripting/.../Evaluators/IEvaluator.cs` | Optional `cacheScope` parameter + docs |
| `modules/BBT.Workflow.Modules.Scripting/.../Helpers/IScriptHelperRegistry.cs` | `HelperSet.Key` |
| `modules/BBT.Workflow.Modules.Scripting/.../Helpers/ScriptHelperRegistry.cs` | Populate `Key` |
| `src/BBT.Workflow.Application/Scripting/ScriptEngine.cs` | Pass `helperSet.Key` as `cacheScope` |
| `test/BBT.Workflow.Application.Tests/Scripting/` | Four new tests |

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
