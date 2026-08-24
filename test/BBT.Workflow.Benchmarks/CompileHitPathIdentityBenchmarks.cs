using System;
using System.Linq;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Evaluators;
using BBT.Workflow.Scripting.Helpers;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BBT.Workflow.Benchmarks;

/// <summary>
/// Per-call fixed cost of <see cref="ScriptEngine.CompileToInstanceAsync{T}(ScriptCode, ScriptSettings?, System.Collections.Generic.IEnumerable{Microsoft.CodeAnalysis.MetadataReference}?, System.Collections.Generic.IEnumerable{string}?, System.Threading.CancellationToken)"/>
/// on a WARM cache, targeting <see cref="ITransitionMapping"/> (not <see cref="IBenchScript"/>) so
/// the Katman 1 precomputed-identity path engages: the target type's assembly (BBT.Workflow.Domain)
/// is already part of the engine's own <c>DefaultReferences</c>, so no <c>extraReferences</c> are
/// needed and <c>engineControlsInputs</c> stays true inside <c>CompileToInstanceAsync</c>'s ScriptCode
/// overload. Using a benchmark-local type (like <see cref="IBenchScript"/>, whose assembly is never
/// one of the engine's defaults) would force <c>extraReferences</c> and fall through to the
/// per-call raw-key path measured by <see cref="CompileHitPathBenchmarks"/> instead — the exact
/// thing this suite exists to avoid.
///
/// Compare against <see cref="CompileHitPathBenchmarks"/> (raw evaluator, no engine, per-call
/// GenerateCacheKey): this suite exercises the engine-level memoized profile + <c>ScriptCode.ContentHash</c>
/// + precomputed cache key path added in Katman 1 Tasks 1-3, so a warm hit here is expected to pay only
/// the fast-path dictionary lookup and <see cref="ScriptActivator"/> instantiation — no per-call source
/// hashing, no per-call profile string rebuild.
/// </summary>
[MemoryDiagnoser]
[GcServer(true)]
public class CompileHitPathIdentityBenchmarks
{
    private ScriptEngine _engine = null!;
    private ScriptCode _scriptCode = null!;

    [Params(1, 4, 16)]
    public int SourceKb { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var evaluator = new CSharpEvaluator();
        var services = new ServiceCollection().BuildServiceProvider();
        _engine = new ScriptEngine(
            evaluator,
            new NoopScriptServices(),
            new NoopWorkflowMetrics(),
            new ScriptHelperRegistry(evaluator),
            new ScriptHelpersOptions { Enabled = false },
            services,
            NullLogger<ScriptEngine>.Instance);

        // Target type ITransitionMapping: its assembly is already in the engine's DefaultReferences,
        // so extraReferences is NOT needed here → the precomputed-key (identity) path is measured.
        // Using a benchmark-assembly type (e.g. IBenchScript) would force extraReferences and, per
        // Task 3's engineControlsInputs rule, fall back to the per-call raw-key path instead.
        var padding = new string('/', 80) + "\n";
        var pad = string.Concat(Enumerable.Repeat(padding, SourceKb * 1024 / padding.Length + 1));
        _scriptCode = ScriptCode.FromNative(
            pad + "\npublic class IdProbe : ITransitionMapping { public System.Threading.Tasks.Task<dynamic> Handler(ScriptContext context) => System.Threading.Tasks.Task.FromResult<dynamic>(42); }\n");

        // Warm: the single real compile happens here; measured iterations are always hits.
        _ = _engine.CompileToInstanceAsync<ITransitionMapping>(_scriptCode).GetAwaiter().GetResult();
    }

    [Benchmark]
    public object WarmCompileViaScriptCode()
        => _engine.CompileToInstanceAsync<ITransitionMapping>(_scriptCode).GetAwaiter().GetResult();
}
