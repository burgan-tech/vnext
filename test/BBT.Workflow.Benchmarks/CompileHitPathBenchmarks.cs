using System.Linq;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using BBT.Workflow.Scripting.Evaluators;
using Microsoft.CodeAnalysis;

namespace BBT.Workflow.Benchmarks;

public interface IBenchScript
{
    int Run();
}

/// <summary>
/// Per-call fixed cost of CompileToInstanceAsync on a WARM cache:
/// GenerateCacheKey (full-source SHA256 + OrderBys) + fast path + Activator.CreateInstance.
/// Analysis items A1/A3/A4 — ai-docs/script-perf-analysis-2026-08-23.md.
/// </summary>
[MemoryDiagnoser]
public class CompileHitPathBenchmarks
{
    private CSharpEvaluator _evaluator = null!;
    private string _source = null!;
    private MetadataReference[] _references = null!;

    [Params(1, 4, 16)]
    public int SourceKb { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _evaluator = new CSharpEvaluator();
        _references =
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location),
            MetadataReference.CreateFromFile(typeof(IBenchScript).Assembly.Location)
        ];
        // Pad the source to the requested size with comment lines: cache-key generation walks the
        // full text, so source size is the measurement's main parameter.
        var padding = new string('/', 80) + "\n";
        var pad = string.Concat(Enumerable.Repeat(padding, SourceKb * 1024 / padding.Length + 1));
        _source = pad + "\npublic class HitPathProbe : BBT.Workflow.Benchmarks.IBenchScript\n{\n    public int Run() => 42;\n}\n";
        // Warm: the single real compile happens here; measured iterations are always hits.
        _ = _evaluator.CompileToInstanceAsync<IBenchScript>(_source, extraReferences: _references)
            .GetAwaiter().GetResult();
    }

    [Benchmark]
    public IBenchScript WarmCompileToInstance()
        => _evaluator.CompileToInstanceAsync<IBenchScript>(_source, extraReferences: _references)
            .GetAwaiter().GetResult().Instance;
}
