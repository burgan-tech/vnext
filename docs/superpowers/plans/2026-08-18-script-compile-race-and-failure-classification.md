# Script Compile Race and Output-Mapping Failure Classification — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop concurrent script compilations from colliding in a shared `AssemblyLoadContext`, and stop a transient infrastructure fault during subflow output mapping from permanently faulting the parent instance.

**Architecture:** Five independent changes. Tasks 1–3 make the script type cache concurrency-safe (`Lazy<T>` + `GetOrAdd`), make assembly loading idempotent, and make the cache key distinguish load contexts. Tasks 4–5 introduce a transient/permanent classifier for output-mapping failures so only genuinely permanent failures produce a terminal outcome. Each task is independently committable and leaves the build green.

**Tech Stack:** .NET 10, C#, Roslyn (`Microsoft.CodeAnalysis.CSharp`), `System.Runtime.Loader.AssemblyLoadContext`, xUnit + Moq + Shouldly.

**Spec:** `docs/superpowers/specs/2026-08-17-script-alc-double-compile-race-design.md`

---

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `modules/BBT.Workflow.Modules.Scripting/BBT/Workflow/Scripting/Evaluators/CSharpEvaluator.cs` | Compile-once cache, idempotent load, cache key | 1, 2, 3 |
| `modules/BBT.Workflow.Modules.Scripting/BBT/Workflow/Scripting/Evaluators/IEvaluator.cs` | `cacheScope` parameter on the contract | 3 |
| `modules/BBT.Workflow.Modules.Scripting/BBT/Workflow/Scripting/Helpers/IScriptHelperRegistry.cs` | `HelperSet.Key` + the invariant it carries | 3 |
| `modules/BBT.Workflow.Modules.Scripting/BBT/Workflow/Scripting/Helpers/ScriptHelperRegistry.cs` | Populate `Key`; invariant comment on `Evict` | 3 |
| `src/BBT.Workflow.Application/Scripting/ScriptEngine.cs` | Pass `helperSet.Key` through as `cacheScope` | 3 |
| `src/BBT.Workflow.Application/SubFlow/Services/OutputMappingFailureClassifier.cs` | **New.** Sole owner of the transient/permanent decision | 4 |
| `src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs` | `SubFlowOutputMappingTransientFailure` (EventId 40089) | 4 |
| `src/BBT.Workflow.Application/SubFlow/Services/SubflowOutputMappingService.cs` | Rethrow transient, keep `Result.Fail` for permanent | 4 |
| `src/BBT.Workflow.Application/SubFlow/Services/SubflowCompletionService.cs` | Comment only — behaviour already correct | 5 |
| `src/BBT.Workflow.Application/SubFlow/Services/SubflowFaultService.cs` | Comment only — behaviour already correct | 5 |
| `test/BBT.Workflow.Application.Tests/Scripting/CSharpEvaluatorConcurrencyTests.cs` | **New.** Tasks 1–3 tests | 1, 2, 3 |
| `test/BBT.Workflow.Application.Tests/SubFlow/OutputMappingFailureClassifierTests.cs` | **New.** Classifier tests | 4 |
| `test/BBT.Workflow.Application.Tests/SubFlow/SubflowCompletionServiceTests.cs` | Caller-contract tests | 4 |

The classifier is a separate file on purpose. It is the one place that decides what "transient" means, it is pure and needs no mocks to test, and keeping it out of `SubflowOutputMappingService` means the policy can be read and changed without reading the mapping logic around it.

---

## Task 1: Compile exactly once per cache key

**Why:** `CompileToInstanceAsync` does `TryGetValue` → miss → Roslyn emit → `LoadFromStream` → `TryAdd`, with no `GetOrAdd`. Two concurrent callers with the same key both compile, produce the same assembly simple name, and the second `LoadFromStream` into a shared context throws `FileLoadException`. Spec §5.1 and §5.2 (assembly name width).

**Files:**
- Modify: `modules/BBT.Workflow.Modules.Scripting/BBT/Workflow/Scripting/Evaluators/CSharpEvaluator.cs`
- Create: `test/BBT.Workflow.Application.Tests/Scripting/CSharpEvaluatorConcurrencyTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `test/BBT.Workflow.Application.Tests/Scripting/CSharpEvaluatorConcurrencyTests.cs`:

```csharp
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using BBT.Workflow.Scripting.Evaluators;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Scripting;

/// <summary>
/// Pins the evaluator's cache concurrency contract. These run in the sequential scripting
/// collection because they assert on compile counts and shared load-context state.
/// </summary>
[Collection("ScriptingTests")]
public sealed class CSharpEvaluatorConcurrencyTests
{
    private const string SampleScript = """
        public class SampleMapping
        {
            public int Value => 42;
        }
        """;

    /// <summary>
    /// A collectible context standing in for the helper set's shared context. The production type
    /// (ScriptAssemblyLoadContext) is internal, and the evaluator only needs an AssemblyLoadContext.
    /// </summary>
    private sealed class TestLoadContext : AssemblyLoadContext
    {
        public TestLoadContext() : base(isCollectible: true)
        {
        }

        protected override Assembly? Load(AssemblyName assemblyName) => null;
    }

    [Fact]
    public async Task CompileToInstanceAsync_WhenManyCallersShareOneLoadContext_ShouldCompileOnceAndNotThrow()
    {
        var evaluator = new CSharpEvaluator();
        var context = new TestLoadContext();

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
                evaluator.CompileToInstanceAsync<object>(SampleScript, loadContext: context))));

        results.ShouldAllBe(r => r != null);
        results.Select(r => r.GetType()).Distinct().Count().ShouldBe(1);
        evaluator.CachedTypeCount.ShouldBe(1);
    }

    [Fact]
    public async Task CompileToInstanceAsync_ShouldNameTheAssemblyAfterTheWholeCacheKey()
    {
        var evaluator = new CSharpEvaluator();

        var instance = await evaluator.CompileToInstanceAsync<object>(SampleScript);

        var name = instance.GetType().Assembly.GetName().Name;
        name.ShouldNotBeNull();
        name.ShouldStartWith("Script_");
        // SHA-256 rendered as hex. A truncated name would make the §5.2 reuse rule probabilistic.
        name["Script_".Length..].Length.ShouldBe(64);
    }

    [Fact]
    public async Task CompileToInstanceAsync_WhenCompilationFails_ShouldNotCacheTheFailure()
    {
        var evaluator = new CSharpEvaluator();
        const string broken = "public class Broken { this is not valid C# }";

        await Should.ThrowAsync<InvalidOperationException>(
            () => evaluator.CompileToInstanceAsync<object>(broken));
        evaluator.CachedTypeCount.ShouldBe(0);

        // A cached Lazy would replay the first exception forever; the entry must be gone.
        await Should.ThrowAsync<InvalidOperationException>(
            () => evaluator.CompileToInstanceAsync<object>(broken));
        evaluator.CachedTypeCount.ShouldBe(0);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~CSharpEvaluatorConcurrencyTests"
```

Expected: FAIL. The concurrency test throws `System.IO.FileLoadException: ... Assembly with same name is already loaded`. The naming test fails with actual length 16.

- [ ] **Step 3: Replace the cache field and add the record struct**

In `CSharpEvaluator.cs`, replace the `_typeCache` declaration and its doc comment:

```csharp
    /// <summary>
    /// Cached compiled scripts indexed by cache key.
    ///
    /// The value is a <see cref="Lazy{T}"/> so concurrent callers with the same key compile exactly
    /// once. This is a correctness requirement, not an optimisation: the assembly's simple name is
    /// derived from the cache key, and an <see cref="AssemblyLoadContext"/> cannot hold two
    /// assemblies with the same simple name, so a second concurrent load into a shared helper
    /// context throws.
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<CompiledScript>> _typeCache = new();

    /// <summary>A compiled script type and the context its assembly was loaded into.</summary>
    private readonly record struct CompiledScript(AssemblyLoadContext Context, Type CompiledType);
```

- [ ] **Step 4: Rewrite `CompileToInstanceAsync` to use `GetOrAdd`**

Replace the body of `CompileToInstanceAsync<T>` (everything after the parameter list) with:

```csharp
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Code cannot be null or empty", nameof(code));

        // The caller's token gates entry only. Once a compile starts it is shared by every caller
        // waiting on the same Lazy, so one abandoned request must not fail it — the same rule
        // ScriptHelperRegistry applies to helper-set builds.
        cancellationToken.ThrowIfCancellationRequested();

        var cacheKey = GenerateCacheKey(code, typeof(T), extraReferences, usingDirectives, sandboxGrant);

        var lazy = _typeCache.GetOrAdd(cacheKey, _ => new Lazy<CompiledScript>(
            () => CompileAndLoad<T>(code, cacheKey, extraReferences, usingDirectives, sandboxGrant, loadContext),
            LazyThreadSafetyMode.ExecutionAndPublication));

        CompiledScript compiled;
        try
        {
            compiled = lazy.Value;
        }
        catch
        {
            // Lazy<T> caches the exception as well as the value, and this evaluator is a singleton:
            // without eviction one transient failure would be replayed for the rest of the process
            // lifetime. Remove only the entry we observed — never one another caller has published.
            _typeCache.TryRemove(new KeyValuePair<string, Lazy<CompiledScript>>(cacheKey, lazy));
            throw;
        }

        return Task.FromResult(CreateAndInjectServices<T>(compiled.CompiledType, services));
    }
```

- [ ] **Step 5: Replace `CompileAndCacheAsync` with `CompileAndLoad`**

Delete the whole `CompileAndCacheAsync<T>` method and put this in its place:

```csharp
    /// <summary>
    /// Compiles the code and loads it into the target context, returning the type to cache.
    ///
    /// Runs under <see cref="CancellationToken.None"/> deliberately: the result is shared by every
    /// caller waiting on the same <see cref="Lazy{T}"/>, so one caller disconnecting must not fail
    /// the compile the others are waiting on.
    /// </summary>
    private CompiledScript CompileAndLoad<T>(
        string code,
        string cacheKey,
        IEnumerable<MetadataReference>? extraReferences,
        IEnumerable<string>? usingDirectives,
        IReadOnlyList<string>? sandboxGrant,
        AssemblyLoadContext? loadContext)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(code, options: ParseOptions);

        // Add using directives if provided
        if (usingDirectives != null && usingDirectives.Any())
        {
            var root = syntaxTree.GetRoot();
            var usings = usingDirectives.Select(u => SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(u)));
            var newRoot = ((CompilationUnitSyntax)root).WithUsings(SyntaxFactory.List(usings));
            syntaxTree = syntaxTree.WithRootAndOptions(newRoot, syntaxTree.Options);
        }

        // The WHOLE cache key, not a prefix: the load below reuses an already-loaded assembly by
        // simple name, which is only exact if the name identifies the compilation uniquely.
        var assemblyName = $"Script_{cacheKey}";

        var compilation = CreateCompilation(assemblyName, [syntaxTree], extraReferences, sandboxGrant);

        // Layer 2 of the sandbox: semantic ban list, run before emit.
        RunSandboxAnalyzer(compilation);

        var image = EmitToImage(compilation, CancellationToken.None);

        // Use the shared collectible context when supplied (so mappings resolve helper types),
        // otherwise a fresh per-script collectible context (so we CAN unload, e.g. ClearCache).
        var context = loadContext ?? new ScriptAssemblyLoadContext(assemblyName);

        var assembly = context.LoadFromStream(new MemoryStream(image));

        // Find the type that implements T
        var types = assembly.GetTypes();
        var matchedType = types.FirstOrDefault(t =>
            typeof(T).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

        if (matchedType == null)
        {
            if (loadContext is null && context is ScriptAssemblyLoadContext owned)
            {
                owned.Unload();
            }

            var available = string.Join(", ", types.Select(t => t.FullName));
            throw new InvalidOperationException(
                $"No type implementing {typeof(T).FullName} found.\nAvailable types: {available}");
        }

        return new CompiledScript(context, matchedType);
    }
```

- [ ] **Step 6: Fix the two remaining `_typeCache` consumers**

`ClearCache()` and `InvalidateScript<T>()` now hold `Lazy<CompiledScript>`. Replace both bodies:

```csharp
    public void ClearCache()
    {
        foreach (var key in _typeCache.Keys.ToList())
        {
            if (_typeCache.TryRemove(key, out var cached) && cached.IsValueCreated)
            {
                try
                {
                    cached.Value.Context.Unload();
                }
                catch
                {
                    // Ignore unload failures
                }
            }
        }
    }
```

```csharp
        if (_typeCache.TryRemove(cacheKey, out var cached) && cached.IsValueCreated)
        {
            try
            {
                cached.Value.Context.Unload();
            }
            catch
            {
                // Ignore
            }
            return true;
        }

        return false;
```

`IsValueCreated` matters: reading `.Value` on a faulted `Lazy` rethrows the cached exception, which is exactly the pattern `ScriptHelperRegistry.Dispose` already uses.

- [ ] **Step 7: Run the tests to verify they pass**

```bash
dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~CSharpEvaluatorConcurrencyTests"
```

Expected: PASS, 3 tests.

- [ ] **Step 8: Run the existing scripting suite for regressions**

```bash
dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~BBT.Workflow.Application.Tests.Scripting"
```

Expected: no new failures compared to the pre-change run. Note the repo has a known pre-existing failure baseline on master; compare, do not assume zero.

- [ ] **Step 9: Commit**

```bash
git add modules/BBT.Workflow.Modules.Scripting/BBT/Workflow/Scripting/Evaluators/CSharpEvaluator.cs test/BBT.Workflow.Application.Tests/Scripting/CSharpEvaluatorConcurrencyTests.cs
git commit -m "fix(scripting): compile each script once per cache key"
```

---

## Task 2: Reuse an already-loaded assembly instead of reloading it

**Why:** If an earlier attempt loaded the assembly into a shared context and then failed before publishing to the cache, nothing can remove that assembly — a shared context cannot unload one assembly — so every later attempt throws forever. Reuse is the only correct recovery. Spec §5.2.

**Files:**
- Modify: `modules/BBT.Workflow.Modules.Scripting/BBT/Workflow/Scripting/Evaluators/CSharpEvaluator.cs`
- Modify: `test/BBT.Workflow.Application.Tests/Scripting/CSharpEvaluatorConcurrencyTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `CSharpEvaluatorConcurrencyTests`:

```csharp
    [Fact]
    public async Task CompileToInstanceAsync_WhenAssemblyAlreadyLoadedInContext_ShouldReuseItInsteadOfThrowing()
    {
        // Two evaluators sharing one context reproduces the state an earlier partial failure
        // leaves behind: the assembly is loaded, but this evaluator's cache knows nothing about it.
        var context = new TestLoadContext();
        var first = new CSharpEvaluator();
        var second = new CSharpEvaluator();

        await first.CompileToInstanceAsync<object>(SampleScript, loadContext: context);

        var result = await second.CompileToInstanceAsync<object>(SampleScript, loadContext: context);

        result.ShouldNotBeNull();
        second.CachedTypeCount.ShouldBe(1);
    }
```

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~WhenAssemblyAlreadyLoadedInContext"
```

Expected: FAIL with `System.IO.FileLoadException: ... Assembly with same name is already loaded`.

- [ ] **Step 3: Make the load idempotent**

In `CompileAndLoad<T>`, replace this line:

```csharp
        var assembly = context.LoadFromStream(new MemoryStream(image));
```

with:

```csharp
        // Reuse over reload. An assembly already loaded here under this name IS this compilation —
        // the name is the full hash of the compilation inputs. A shared context cannot unload a
        // single assembly, so if an earlier attempt loaded it and then failed before caching the
        // type, reloading would throw for the rest of the process lifetime.
        var assembly = context.Assemblies.FirstOrDefault(a => a.GetName().Name == assemblyName)
                       ?? context.LoadFromStream(new MemoryStream(image));
```

`AssemblyLoadContext.Assemblies` yields from a snapshot of the process's loaded assemblies, so enumerating it while another thread loads is safe.

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~CSharpEvaluatorConcurrencyTests"
```

Expected: PASS, 4 tests.

- [ ] **Step 5: Commit**

```bash
git add modules/BBT.Workflow.Modules.Scripting/BBT/Workflow/Scripting/Evaluators/CSharpEvaluator.cs test/BBT.Workflow.Application.Tests/Scripting/CSharpEvaluatorConcurrencyTests.cs
git commit -m "fix(scripting): reuse an already-loaded script assembly instead of reloading it"
```

---

## Task 3: Make the cache key distinguish load contexts

> **SUPERSEDED DURING IMPLEMENTATION — read this before the steps below.**
> Steps 3–7 as written thread an explicit `cacheScope` string alongside `loadContext` through
> `IEvaluator`, `ScriptEngine`, and a new `HelperSet.Key`. That was implemented, reviewed, and
> **withdrawn**: the scope and the context must always agree, and passing them separately made
> disagreement representable — review found an existing test already doing it. The shipped design
> derives the scope from the `AssemblyLoadContext` itself inside `CSharpEvaluator`, via a
> `ConditionalWeakTable`, adding no parameter to any public contract and removing the need for the
> `HelperSet.Key` invariant entirely.
>
> **Spec §5.3 is authoritative; the steps below are kept as the record of what was tried.**
> The goal, the failing-test-first discipline, and the test list still stand — but the two
> evaluator-level tests assert on load contexts rather than scope strings, and the decisive test is
> `ScriptEngine_Compiles_Same_Mapping_Against_Different_Helper_Sets_Without_Cross_Contamination` in
> `SandboxedScriptingTests.cs`, added after review found the original test would pass even with the
> wiring broken.
>
> Landed as `838b4f0f` → `6735c5c8` → `dd8ea966` → `326d8b36`.

**Why:** `MetadataReference.CreateFromImage(image).Display` is null, so the helper assembly contributes nothing to the cache key. Two helper sets exporting the same namespaces share one cache entry for identical mapping source, and the second flow silently executes the first flow's helper implementations. Spec §5.3.

**Files:**
- Modify: `modules/BBT.Workflow.Modules.Scripting/BBT/Workflow/Scripting/Evaluators/IEvaluator.cs`
- Modify: `modules/BBT.Workflow.Modules.Scripting/BBT/Workflow/Scripting/Evaluators/CSharpEvaluator.cs`
- Modify: `modules/BBT.Workflow.Modules.Scripting/BBT/Workflow/Scripting/Helpers/IScriptHelperRegistry.cs`
- Modify: `modules/BBT.Workflow.Modules.Scripting/BBT/Workflow/Scripting/Helpers/ScriptHelperRegistry.cs`
- Modify: `src/BBT.Workflow.Application/Scripting/ScriptEngine.cs`
- Modify: `test/BBT.Workflow.Application.Tests/Scripting/CSharpEvaluatorConcurrencyTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `CSharpEvaluatorConcurrencyTests`:

```csharp
    [Fact]
    public async Task CompileToInstanceAsync_WhenSameCodeCompiledUnderDifferentScopes_ShouldNotShareCacheEntry()
    {
        var evaluator = new CSharpEvaluator();
        var contextA = new TestLoadContext();
        var contextB = new TestLoadContext();

        var a = await evaluator.CompileToInstanceAsync<object>(
            SampleScript, loadContext: contextA, cacheScope: "helper-set-a");
        var b = await evaluator.CompileToInstanceAsync<object>(
            SampleScript, loadContext: contextB, cacheScope: "helper-set-b");

        evaluator.CachedTypeCount.ShouldBe(2);
        AssemblyLoadContext.GetLoadContext(a.GetType().Assembly).ShouldBeSameAs(contextA);
        AssemblyLoadContext.GetLoadContext(b.GetType().Assembly).ShouldBeSameAs(contextB);
    }
```

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~WhenSameCodeCompiledUnderDifferentScopes"
```

Expected: FAIL to compile — `cacheScope` is not a parameter of `CompileToInstanceAsync`.

- [ ] **Step 3: Add `cacheScope` to the `IEvaluator` contract**

In `IEvaluator.cs`, append the parameter and its doc to `CompileToInstanceAsync`:

```csharp
    /// <param name="cacheScope">
    /// Optional identity of the load context the compilation belongs to. Two callers using different
    /// shared contexts must not share a cached type, and the helper assembly's
    /// <see cref="MetadataReference"/> cannot express that (its <c>Display</c> is null for an
    /// in-memory image), so the scope is passed explicitly.
    /// </param>
    Task<T> CompileToInstanceAsync<T>(
        string code,
        IScriptServices? services = null,
        IEnumerable<MetadataReference>? extraReferences = null,
        IEnumerable<string>? usingDirectives = null,
        CancellationToken cancellationToken = default,
        AssemblyLoadContext? loadContext = null,
        IReadOnlyList<string>? sandboxGrant = null,
        string? cacheScope = null);
```

- [ ] **Step 4: Thread `cacheScope` through `CSharpEvaluator`**

Add the parameter to `CompileToInstanceAsync<T>`'s signature (same trailing position), and pass it to the key generator:

```csharp
        var cacheKey = GenerateCacheKey(
            code, typeof(T), extraReferences, usingDirectives, sandboxGrant, cacheScope);
```

Add the parameter to `GenerateCacheKey` and fold it into the key. The signature becomes:

```csharp
    private string GenerateCacheKey(
        string code,
        Type targetType,
        IEnumerable<MetadataReference>? extraReferences,
        IEnumerable<string>? usingDirectives,
        IReadOnlyList<string>? sandboxGrant = null,
        string? cacheScope = null)
```

and immediately after the `sbx:` line insert:

```csharp
        // The load context is part of the compilation identity: a type compiled into helper set A's
        // context must never be served to a caller compiling against helper set B.
        if (cacheScope != null)
        {
            sb.Append("|alc:").Append(cacheScope);
        }
```

Add the same trailing parameter to `InvalidateScript<T>` and forward it, so the invalidation key can match a helper-scoped script:

```csharp
    public bool InvalidateScript<T>(
        string code,
        IEnumerable<MetadataReference>? extraReferences = null,
        IEnumerable<string>? usingDirectives = null,
        string? cacheScope = null)
    {
        var cacheKey = GenerateCacheKey(
            code, typeof(T), extraReferences, usingDirectives, sandboxGrant: null, cacheScope: cacheScope);
```

- [ ] **Step 5: Add `Key` to `HelperSet` with the invariant it carries**

In `IScriptHelperRegistry.cs`, replace the `HelperSet` record:

```csharp
/// <param name="Key">
/// Content hash identifying this helper set — the registry's cache key. Callers pass it to the
/// evaluator as <c>cacheScope</c> so a compiled mapping is never shared across load contexts.
///
/// INVARIANT: this is a *content* hash, so it is stable across rebuilds of the same sources. That is
/// only safe because a healthy set is never replaced — <c>Evict</c> runs solely from the catch around
/// a faulted build, and <c>Dispose</c> only at process shutdown — so a cached type can never outlive
/// its load context. If TTL expiry, hot reload, or capacity eviction is ever added to the registry,
/// the evaluator's type cache must be invalidated with it, or this must become a per-context identity.
/// </param>
public sealed record HelperSet(
    MetadataReference Reference,
    IReadOnlyList<string> Namespaces,
    AssemblyLoadContext LoadContext,
    bool FromCache,
    string Key);
```

- [ ] **Step 6: Populate `Key` and mark the invariant at the eviction site**

In `ScriptHelperRegistry.BuildHelperSet`, change the return:

```csharp
            return new HelperSet(compiled.Reference, compiled.Namespaces, alc, FromCache: false, Key: key);
```

Add to the doc comment on `Evict`:

```csharp
    /// <summary>
    /// Removes a faulted cache entry, but only when it is still the entry we observed — never clobbers a
    /// healthy set that another caller has already published under the same key.
    ///
    /// This is the ONLY eviction path, and it only ever removes a faulted build. The evaluator's type
    /// cache depends on that: it keys compiled mappings by <see cref="HelperSet.Key"/>, a content hash,
    /// so evicting a healthy set would leave cached types pointing at an unloaded context. Any new
    /// eviction policy must invalidate the evaluator cache too.
    /// </summary>
```

- [ ] **Step 7: Pass the scope from `ScriptEngine`**

In `ScriptEngine.cs`, add a trailing `string? cacheScope = null` parameter to `CompileCoreAsync<T>` and forward it to the evaluator:

```csharp
            var result = await _evaluator.CompileToInstanceAsync<T>(
                code,
                _scriptServices,
                mergedReferences,
                mergedUsings,
                cancellationToken,
                loadContext,
                MergeDefaultGrant(sandboxGrant),
                cacheScope);
```

Then at the helper call site at the end of `CompileToInstanceAsync(ScriptCode, ...)`:

```csharp
        return await CompileCoreAsync<T>(
            body, refs, usings, helperSet.LoadContext, grant, cancellationToken, helperSet.Key);
```

The two no-helper call sites keep passing nothing, so their scope stays null.

- [ ] **Step 8: Run the tests to verify they pass**

```bash
dotnet build && dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~BBT.Workflow.Application.Tests.Scripting"
```

Expected: PASS, 5 tests in `CSharpEvaluatorConcurrencyTests`, no new failures elsewhere.

- [ ] **Step 9: Commit**

```bash
git add modules/BBT.Workflow.Modules.Scripting src/BBT.Workflow.Application/Scripting/ScriptEngine.cs test/BBT.Workflow.Application.Tests/Scripting/CSharpEvaluatorConcurrencyTests.cs
git commit -m "fix(scripting): key the script cache by load context, not just by source"
```

---

## Task 4: Classify output-mapping failures as transient or permanent

**Why:** `SubflowCompletionService` turns any failed output mapping into a terminal outcome — incident, `Fault`, commit — in the same transaction that closed the correlation, so nothing retries. An infrastructure fault therefore destroys a healthy instance. Spec §5.4.

**Files:**
- Create: `src/BBT.Workflow.Application/SubFlow/Services/OutputMappingFailureClassifier.cs`
- Create: `test/BBT.Workflow.Application.Tests/SubFlow/OutputMappingFailureClassifierTests.cs`
- Modify: `src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs`
- Modify: `src/BBT.Workflow.Application/SubFlow/Services/SubflowOutputMappingService.cs:78-88`
- Modify: `test/BBT.Workflow.Application.Tests/SubFlow/SubflowCompletionServiceTests.cs`

- [ ] **Step 1: Write the failing classifier tests**

Create `test/BBT.Workflow.Application.Tests/SubFlow/OutputMappingFailureClassifierTests.cs`:

```csharp
using System;
using System.IO;
using BBT.Workflow.SubFlow;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.SubFlow;

public sealed class OutputMappingFailureClassifierTests
{
    [Fact]
    public void IsTransient_ForAssemblyLoadFailure_ShouldBeTrue()
        => OutputMappingFailureClassifier.IsTransient(new FileLoadException()).ShouldBeTrue();

    [Fact]
    public void IsTransient_ForBadImageFormat_ShouldBeTrue()
        => OutputMappingFailureClassifier.IsTransient(new BadImageFormatException()).ShouldBeTrue();

    [Fact]
    public void IsTransient_ForCancellation_ShouldBeTrue()
        => OutputMappingFailureClassifier.IsTransient(new OperationCanceledException()).ShouldBeTrue();

    [Fact]
    public void IsTransient_ForWrappedAssemblyLoadFailure_ShouldBeTrue()
        => OutputMappingFailureClassifier
            .IsTransient(new InvalidOperationException("outer", new FileLoadException()))
            .ShouldBeTrue();

    [Fact]
    public void IsTransient_ForACompilationError_ShouldBeFalse()
        => OutputMappingFailureClassifier
            .IsTransient(new InvalidOperationException("Compilation failed:\nCS1002"))
            .ShouldBeFalse();

    [Fact]
    public void IsTransient_ForAnUnclassifiedException_ShouldBeFalse()
        => OutputMappingFailureClassifier.IsTransient(new NotSupportedException()).ShouldBeFalse();
}
```

- [ ] **Step 2: Run them to verify they fail**

```bash
dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~OutputMappingFailureClassifierTests"
```

Expected: FAIL to compile — `OutputMappingFailureClassifier` does not exist.

- [ ] **Step 3: Write the classifier**

Create `src/BBT.Workflow.Application/SubFlow/Services/OutputMappingFailureClassifier.cs`:

```csharp
using System;
using System.IO;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Decides whether a failure raised while applying a subflow output mapping is transient — the same
/// mapping would succeed on a later attempt — or permanent, meaning it can never succeed as written.
///
/// The distinction is load-bearing. <see cref="SubflowCompletionService"/> turns a permanent failure
/// into a terminal outcome: it faults the parent and commits, in the same transaction that closed the
/// correlation, so nothing retries. Misclassifying an infrastructure fault as permanent destroys a
/// healthy instance.
///
/// Transient is an ALLOWLIST. Anything unrecognised is permanent, which preserves the historical
/// behaviour and stops an unknown exception from becoming a poison message the broker redelivers
/// forever. Adding a type here is the intended maintenance point.
/// </summary>
public static class OutputMappingFailureClassifier
{
    /// <summary>
    /// True when <paramref name="exception"/>, or any exception it wraps, is a known transient
    /// infrastructure fault. The inner chain is walked because script invocation and type
    /// initialisation both wrap the original fault.
    /// </summary>
    public static bool IsTransient(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is FileLoadException or BadImageFormatException or OperationCanceledException)
            {
                return true;
            }
        }

        return false;
    }
}
```

- [ ] **Step 4: Run the classifier tests to verify they pass**

```bash
dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~OutputMappingFailureClassifierTests"
```

Expected: PASS, 6 tests.

- [ ] **Step 5: Add the log method**

In `src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs`, immediately after the `SubFlowOutputMappingFailed` declaration (which ends at the `Guid parentInstanceId);` on line 1144), insert:

```csharp
    /// <summary>
    /// Logs that a subflow output mapping hit a transient infrastructure fault and is being rethrown
    /// for redelivery rather than faulting the parent.
    /// </summary>
    [LoggerMessage(
        EventId = 40089,
        Level = LogLevel.Warning,
        Message = "SubFlow output mapping hit a transient failure for parent instance {ParentInstanceId}; rethrowing for redelivery")]
    public static partial void SubFlowOutputMappingTransientFailure(
        this ILogger logger,
        Exception exception,
        Guid parentInstanceId);
```

EventId 40089 is unused; 40088 and 40090 are taken.

- [ ] **Step 6: Write the failing caller-contract tests**

Add `using System.IO;` to the top of `test/BBT.Workflow.Application.Tests/SubFlow/SubflowCompletionServiceTests.cs` — it is not in the current using block and `FileLoadException` needs it. Then add these two tests:

```csharp
    [Fact]
    public async Task CompletionAsync_WhenOutputMappingFailsTransiently_ShouldPropagateWithoutFaultingOrCommitting()
    {
        var parent = CreateParentInstance(out var subInstanceId, SubFlowType.SubFlow);
        SetupCompletedCorrelationPath(parent);
        _outputMappingService
            .Setup(x => x.ApplyAsync(
                It.IsAny<Instance>(), It.IsAny<Definitions.Workflow>(), It.IsAny<string>(),
                It.IsAny<JsonElement?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileLoadException("Assembly with same name is already loaded"));

        await Should.ThrowAsync<FileLoadException>(
            () => CreateService().CompletionAsync(CreateInput(parent.Id, subInstanceId)));

        // Nothing is committed, so the correlation completion rolls back with the transaction and
        // the delivery is redelivered against unchanged state.
        parent.Status.ShouldNotBe(InstanceStatus.Faulted);
        _uow.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompletionAsync_WhenOutputMappingFailsPermanently_ShouldFaultParentAndCommit()
    {
        var parent = CreateParentInstance(out var subInstanceId, SubFlowType.SubFlow);
        SetupCompletedCorrelationPath(parent);
        _outputMappingService
            .Setup(x => x.ApplyAsync(
                It.IsAny<Instance>(), It.IsAny<Definitions.Workflow>(), It.IsAny<string>(),
                It.IsAny<JsonElement?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Fail(WorkflowErrors.SubFlowOutputMappingFailed(
                parent.Id, "mapping script is invalid", "at Mapping.OutputHandler()")));

        await CreateService().CompletionAsync(CreateInput(parent.Id, subInstanceId));

        parent.Status.ShouldBe(InstanceStatus.Faulted);
        _uow.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
```

- [ ] **Step 7: Run them to verify the transient one fails**

```bash
dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~SubflowCompletionServiceTests"
```

Expected: the permanent test PASSES already (that is today's behaviour). The transient test currently also passes for the wrong reason — `ThrowsAsync` bypasses `ApplyAsync`'s catch entirely because the mapping service is mocked. **Both tests are here to lock the caller contract, not to drive Step 8.** Step 8 is driven by the classifier tests from Step 4. Record the result and move on.

- [ ] **Step 8: Split the catch in `SubflowOutputMappingService`**

In `SubflowOutputMappingService.cs`, replace the single `catch (Exception ex)` block at the end of `ApplyAsync` with two blocks, filtered one first:

```csharp
        catch (Exception ex) when (OutputMappingFailureClassifier.IsTransient(ex))
        {
            // Rethrow so the caller's UnitOfWork is never committed: the correlation completion rolls
            // back with the transaction and the delivery is redelivered against unchanged state.
            // Returning Result.Fail here would fault the parent permanently, with nothing to retry it
            // — see docs/superpowers/specs/2026-08-17-script-alc-double-compile-race-design.md §5.4.
            logger.SubFlowOutputMappingTransientFailure(ex, parentInstance.Id);
            throw;
        }
        catch (Exception ex)
        {
            logger.SubFlowOutputMappingFailed(ex, parentInstance.Id);
            return Result.Fail(WorkflowErrors.SubFlowOutputMappingFailed(
                parentInstance.Id,
                ScriptDiagnostics.Explain(ex),
                stackTrace: ex.ToString()));
        }
```

Order matters: the filtered catch must come first, or the general one wins.

- [ ] **Step 9: Run the full SubFlow and scripting suites**

```bash
dotnet build && dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~BBT.Workflow.Application.Tests.SubFlow|FullyQualifiedName~BBT.Workflow.Application.Tests.Scripting"
```

Expected: PASS, no new failures.

- [ ] **Step 10: Commit**

```bash
git add src/BBT.Workflow.Application/SubFlow/Services src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs test/BBT.Workflow.Application.Tests/SubFlow
git commit -m "fix(subflow): stop a transient output-mapping fault from faulting the parent"
```

---

## Task 5: Correct the now-misleading comments at both call sites

**Why:** Both callers already behave correctly once `ApplyAsync` throws for transient failures — a failed `Result` now means "permanent" by construction. Their comments still claim otherwise, which is how this defect survived review the first time. No behaviour change.

**Files:**
- Modify: `src/BBT.Workflow.Application/SubFlow/Services/SubflowCompletionService.cs:249-252`
- Modify: `src/BBT.Workflow.Application/SubFlow/Services/SubflowFaultService.cs:249-251`

- [ ] **Step 1: Update the completion-service comment**

Replace the comment above the `if (!mappingResult.IsSuccess)` block:

```csharp
                    if (!mappingResult.IsSuccess)
                    {
                        // A failed Result means PERMANENT: OutputMappingFailureClassifier rethrows
                        // transient infrastructure faults, so they never reach this branch. Faulting
                        // here is therefore correct — retrying a mapping that cannot succeed as
                        // written would only replay the same failure, and the fault propagates to the
                        // grandparent via InstanceSubFaultedEvent.
```

- [ ] **Step 2: Update the fault-service comment**

Replace the comment above its `if (!mappingResult.IsSuccess)` block:

```csharp
                    // A failed Result means PERMANENT here too (transient faults are rethrown by
                    // OutputMappingFailureClassifier and abort this delivery). Non-blocking on
                    // purpose: the instance is already marked Faulted/transitioned above, so log and
                    // proceed to commit and propagate via InstanceSubFaultedEvent.
```

- [ ] **Step 3: Verify the build**

```bash
dotnet build
```

Expected: success, no warnings introduced.

- [ ] **Step 4: Commit**

```bash
git add src/BBT.Workflow.Application/SubFlow/Services/SubflowCompletionService.cs src/BBT.Workflow.Application/SubFlow/Services/SubflowFaultService.cs
git commit -m "docs(subflow): record that a failed mapping Result now means permanent"
```

---

## Final verification

- [ ] **Run the full test suite and compare against the pre-change baseline**

```bash
dotnet test
```

Master carries a known set of pre-existing failures (largely `AmbientServiceProvider` leakage across parallel collections). Capture the baseline before starting and compare counts — the requirement is *no new* failures, not zero failures.

- [ ] **Confirm every spec section has landed**

| Spec | Task |
|---|---|
| §5.1 atomic compilation, eviction, detached token | 1 |
| §5.2 idempotent load + full-key assembly name | 1 (name), 2 (load) |
| §5.3 cache scope + registry invariant | 3 |
| §5.4 failure classification (allowlist) | 4 |
| §5.4 caller behaviour | 5 |
| §7 tests 1–4 | 1, 2, 3 |
| §7 test 5 | 4 |

**Reversed during implementation.** This plan originally skipped the `SubflowFaultService` test from spec §7 test 5, arguing it would only assert that an exception propagates through a method that does not catch it. That was wrong: `SubflowFaultService.cs:288-292` *does* catch, and the behaviour is correct only because that handler rethrows. Change it to swallow and redelivery breaks silently, dropping the child's data with no signal. The test was added.

The same review round added `SubflowOutputMappingServiceTests.cs`, which the plan lacked entirely: the two caller-contract tests in Task 4 mock `ISubflowOutputMappingService` wholesale and therefore never execute the catch-split this change is about.
