using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace BBT.Workflow.Benchmarks;

/// <summary>
/// Instance-data read primitives: JsonData.JsonElement (full parse per access, B1) and
/// ToDynamic (full ExpandoObject tree build, B2/B3).
/// </summary>
[MemoryDiagnoser]
[GcServer(true)]
public class InstanceDataAccessBenchmarks
{
    private BBT.Workflow.JsonData _data = null!;

    [Params(10, 50, 200)]
    public int DocKb { get; set; }

    [GlobalSetup]
    public void Setup() => _data = new BBT.Workflow.JsonData(PayloadFactory.Json(DocKb));

    // Validity depends on JsonData.JsonElement staying UNMEMOIZED (it re-parses per access today).
    // If a caching backing field is ever added there (Katman 2+), this benchmark silently starts
    // measuring the cached path — switch to a fresh JsonData per invocation in the same change.
    [Benchmark]
    public System.Text.Json.JsonElement ParseJsonElement() => _data.JsonElement;

    [Benchmark]
    public object? ParseAndBuildExpando() => _data.JsonElement.ToDynamic();
}
