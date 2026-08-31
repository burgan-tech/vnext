using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Scripting.Evaluators;
using BBT.Workflow.Scripting.Sandbox;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting;

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
        const int callerCount = 8;

        // Task.Run alone is not a guarantee all 8 run concurrently: on a starved/small pool the thread
        // pool can dispatch them one at a time, letting later callers land on an already-warm cache and
        // never race at all — a false-green in the one test guarding this fix. Force the floor up so 8
        // threads are available immediately, and hold every caller at a rendezvous before any of them
        // calls the evaluator so all 8 genuinely call CompileToInstanceAsync at (as near as possible)
        // the same instant.
        ThreadPool.SetMinThreads(16, 16);

        var evaluator = new CSharpEvaluator();
        var context = new TestLoadContext();
        using var barrier = new Barrier(callerCount);

        var tasks = Enumerable.Range(0, callerCount)
            .Select(_ => Task.Run(() =>
            {
                // A timeout keeps a stuck rendezvous from hanging the test/suite instead of failing it.
                var rendezvoused = barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                rendezvoused.ShouldBeTrue("Rendezvous timed out before all callers arrived.");

                return evaluator.CompileToInstanceAsync<object>(SampleScript, loadContext: context);
            }))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.ShouldAllBe(r => r.Instance != null);
        results.Select(r => r.Instance.GetType()).Distinct().Count().ShouldBe(1);
        evaluator.CachedTypeCount.ShouldBe(1);

        // The assertions above pass even if the Lazy de-duplication regressed away: the assembly-reuse
        // fix (CompileAndLoad) lets N redundant concurrent compiles into this shared context all
        // resolve to the one reused assembly without throwing, so "no exception" plus a shared final
        // Type no longer prove there was only one Roslyn emit. Count the actual compile invocations —
        // the one signal the reuse fix cannot mask — to prove the real guarantee: exactly one compile,
        // not eight that happened to converge on the same assembly.
        evaluator.CompileInvocationCount.ShouldBe(1);
    }

    [Fact]
    public async Task CompileToInstanceAsync_ShouldNameTheAssemblyAfterTheWholeCacheKey()
    {
        var evaluator = new CSharpEvaluator();

        var outcome = await evaluator.CompileToInstanceAsync<object>(SampleScript);

        var name = outcome.Instance.GetType().Assembly.GetName().Name;
        name.ShouldNotBeNull();
        name.ShouldStartWith("Script_");
        // SHA-256 rendered as hex. A truncated name would make the reuse rule probabilistic.
        name["Script_".Length..].Length.ShouldBe(64);
    }

    [Fact]
    public async Task CompileToInstanceAsync_WhenCompilationFails_ShouldNotCacheTheFailure()
    {
        var evaluator = new CSharpEvaluator();
        const string broken = "public class Broken { this is not valid C# }";

        await Should.ThrowAsync<ScriptCompilationException>(
            () => evaluator.CompileToInstanceAsync<object>(broken));
        evaluator.CachedTypeCount.ShouldBe(0);

        // A cached Lazy would replay the first exception forever; the entry must be gone.
        await Should.ThrowAsync<ScriptCompilationException>(
            () => evaluator.CompileToInstanceAsync<object>(broken));
        evaluator.CachedTypeCount.ShouldBe(0);
    }

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

        result.Instance.ShouldNotBeNull();
        second.CachedTypeCount.ShouldBe(1);
    }

    /// <summary>
    /// A target the sample script does not implement, so the matchedType lookup in CompileAndLoad
    /// fails on purpose.
    /// </summary>
    private interface INotImplementedByScript
    {
    }

    [Fact]
    public async Task CompileToInstanceAsync_WhenSameCodeCompiledUnderDifferentLoadContexts_ShouldNotShareCacheEntry()
    {
        // The load context is derived internally (a ConditionalWeakTable-backed scope keyed on the
        // AssemblyLoadContext instance itself), not passed as a separate argument — so there is no way
        // to express a context/scope mismatch. Same source, two distinct contexts, must still land in
        // two separate cache entries and two separate assemblies.
        var evaluator = new CSharpEvaluator();
        var contextA = new TestLoadContext();
        var contextB = new TestLoadContext();

        var a = await evaluator.CompileToInstanceAsync<object>(SampleScript, loadContext: contextA);
        var b = await evaluator.CompileToInstanceAsync<object>(SampleScript, loadContext: contextB);

        evaluator.CachedTypeCount.ShouldBe(2);
        AssemblyLoadContext.GetLoadContext(a.Instance.GetType().Assembly).ShouldBeSameAs(contextA);
        AssemblyLoadContext.GetLoadContext(b.Instance.GetType().Assembly).ShouldBeSameAs(contextB);
    }

    [Fact]
    public async Task CompileToInstanceAsync_WhenNoLoadContextIsSupplied_ShouldStillShareOneCacheEntry()
    {
        // The negative case for the test above: the no-helper path never passes a shared load context,
        // so two identical compiles with none supplied must still hit the same cache entry — an absent
        // context must not mint a distinct scope.
        var evaluator = new CSharpEvaluator();

        await evaluator.CompileToInstanceAsync<object>(SampleScript);
        await evaluator.CompileToInstanceAsync<object>(SampleScript);

        evaluator.CachedTypeCount.ShouldBe(1);
    }

    [Fact]
    public async Task CompileToInstanceAsync_WhenRetryingAfterATypeMatchFailure_ShouldSurfaceTheRealDiagnostic()
    {
        // Reproduces the real production trigger: a single evaluator (it's a process-wide singleton,
        // per TaskServiceCollectionExtensions.cs:270) sharing one AssemblyLoadContext. The first call
        // loads the assembly successfully but finds no matching type, throws InvalidOperationException,
        // and evicts its own faulted Lazy. A shared context cannot unload a single assembly, so the
        // assembly stays loaded under that name. Without reuse, the retry's LoadFromStream throws
        // FileLoadException and masks the real diagnostic for the rest of the process lifetime.
        var context = new TestLoadContext();
        var evaluator = new CSharpEvaluator();

        var first = await Should.ThrowAsync<InvalidOperationException>(
            () => evaluator.CompileToInstanceAsync<INotImplementedByScript>(SampleScript, loadContext: context));
        first.Message.ShouldContain("No type implementing");

        var retry = await Should.ThrowAsync<InvalidOperationException>(
            () => evaluator.CompileToInstanceAsync<INotImplementedByScript>(SampleScript, loadContext: context));
        retry.Message.ShouldContain("No type implementing");
    }

    [Fact]
    public void CompileToInstanceAsync_WithEmptyCode_ShouldThrowSynchronously()
    {
        // The argument guard runs in the synchronous part of the method — an empty-code call throws
        // at the call site, it does not return a faulted task. Pinned so the async-single-flight
        // split (sync validate + async slow path) never regresses the guard into a task fault.
        var evaluator = new CSharpEvaluator();

        Should.Throw<ArgumentException>(() => { _ = evaluator.CompileToInstanceAsync<object>("   "); });
    }

    [Fact]
    public async Task CompileToInstanceAsync_ShouldReturnBeforeTheCompileCompletes()
    {
        // The observable form of "waiters do not block": the method hands back a still-running task
        // instead of blocking the calling thread until the Roslyn emit finishes. With the old
        // blocking Lazy this call did not return until the compile was done, and during cold bursts
        // the blocked callers starved the thread pool.
        var evaluator = new CSharpEvaluator();
        var heavyScript = "public class HeavyMapping {\n"
            + string.Join("\n", Enumerable.Range(0, 400).Select(i => $"    public int Value{i} => {i};"))
            + "\n}";

        var pending = evaluator.CompileToInstanceAsync<object>(heavyScript);

        // Immediately after the synchronous return the emit is still running on the pool.
        pending.IsCompleted.ShouldBeFalse("the call must not block until the compile completes");

        var result = await pending;
        result.Compiled.ShouldBeTrue();
        result.Instance.ShouldNotBeNull();
    }

    [Fact]
    public async Task CompileToInstanceAsync_WhenContentChanges_ShouldCompileFreshAndKeepTheOldTypeUsable()
    {
        // The version-bump contract: a domain package publish (1.0.0-pkg.1.0.1 -> 1.0.0-pkg.1.0.2)
        // changes the mapping's decoded content while its authored name/reference stays the same.
        // The cache key is content-addressed, so the new content MUST compile fresh — a stale cached
        // type served for new content would silently run the old version's logic — and the old
        // entry must remain intact and usable for anything still holding it.
        var evaluator = new CSharpEvaluator();
        const string v1 = "public class VersionedMapping { public int Value => 1; }";
        const string v2 = "public class VersionedMapping { public int Value => 2; }";

        var first = await evaluator.CompileToInstanceAsync<object>(v1);
        var second = await evaluator.CompileToInstanceAsync<object>(v2);

        second.Compiled.ShouldBeTrue("changed content must be a fresh compile, never a cache hit");
        evaluator.CachedTypeCount.ShouldBe(2);
        evaluator.CompileInvocationCount.ShouldBe(2);
        second.Instance.GetType().ShouldNotBeSameAs(first.Instance.GetType());

        // Both versions stay independently executable — the new publish does not corrupt the old.
        first.Instance.GetType().GetProperty("Value")!.GetValue(first.Instance).ShouldBe(1);
        second.Instance.GetType().GetProperty("Value")!.GetValue(second.Instance).ShouldBe(2);

        // And the unchanged content is still a hit afterwards.
        var firstAgain = await evaluator.CompileToInstanceAsync<object>(v1);
        firstAgain.Compiled.ShouldBeFalse();
        evaluator.CompileInvocationCount.ShouldBe(2);
    }
}
