using BenchmarkDotNet.Attributes;

namespace BBT.Workflow.Benchmarks;

/// <summary>
/// Instance-data append primitives (B9): Merge (2 parses + expando merge + serialize) and
/// NormalizedJson (canonical rebuild with per-node SerializeToElement). Runs once per task
/// over a growing document → O(n²) profile per transition.
/// </summary>
[MemoryDiagnoser]
[GcServer(true)]
public class AppendPathBenchmarks
{
    private BBT.Workflow.JsonData _accumulated = null!;
    private BBT.Workflow.JsonData _delta = null!;

    [Params(10, 50, 200)]
    public int DocKb { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _accumulated = new BBT.Workflow.JsonData(PayloadFactory.Json(DocKb));
        _delta = new BBT.Workflow.JsonData("{\"taskResult\":{\"status\":\"ok\",\"score\":87,\"note\":\"appended\"}}");
    }

    [Benchmark]
    public BBT.Workflow.JsonData Merge() => _accumulated.Merge(_delta);

    [Benchmark]
    public string NormalizeFresh() => new BBT.Workflow.JsonData(_accumulated.Json).NormalizedJson;
}
