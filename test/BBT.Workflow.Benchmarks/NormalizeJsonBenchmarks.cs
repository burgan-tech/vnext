using System;
using System.Text;
using BenchmarkDotNet.Attributes;
using BBT.Workflow.Benchmarks.Legacy;

namespace BBT.Workflow.Benchmarks;

/// <summary>
/// <c>JsonData.NormalizedJson</c> (feeds the persisted DataHash), old vs new. <see cref="Legacy"/> is the
/// verbatim pre-single-pass implementation; <see cref="New"/> builds a fresh <see cref="JsonData"/> per
/// invocation so the memoized value is never reused. <c>Deep30</c> is a ~20 KB document nested 30
/// object levels deep — the shape where the old per-node re-serialization was O(size × depth).
/// </summary>
[MemoryDiagnoser]
[GcServer(true)]
public class NormalizeJsonBenchmarks
{
    private string _json = null!;

    [Params("1KB", "20KB", "200KB", "Deep30")]
    public string Payload { get; set; } = "1KB";

    [GlobalSetup]
    public void Setup()
    {
        _json = Payload switch
        {
            "1KB" => PayloadFactory.Json(1),
            "20KB" => PayloadFactory.Json(20),
            "200KB" => PayloadFactory.Json(200),
            _ => Deep(30, PayloadFactory.Json(20)),
        };

        if (new JsonData(_json).NormalizedJson != LegacyNormalizeJson.Normalize(_json))
            throw new InvalidOperationException("NormalizeJson parity broken for the benchmark payload.");
    }

    private static string Deep(int levels, string leaf)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < levels; i++) sb.Append("{\"z\":").Append(i).Append(",\"level").Append(i).Append("\":");
        sb.Append(leaf);
        for (var i = 0; i < levels; i++) sb.Append(",\"a\":[1,{\"y\":2,\"x\":1}]}");
        return sb.ToString();
    }

    [Benchmark(Baseline = true)]
    public string Legacy() => LegacyNormalizeJson.Normalize(_json);

    [Benchmark]
    public string New() => new JsonData(_json).NormalizedJson;
}
