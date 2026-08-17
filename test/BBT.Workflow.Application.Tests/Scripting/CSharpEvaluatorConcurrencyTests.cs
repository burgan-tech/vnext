using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using BBT.Workflow.Scripting.Evaluators;
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
        // SHA-256 rendered as hex. A truncated name would make the reuse rule probabilistic.
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
