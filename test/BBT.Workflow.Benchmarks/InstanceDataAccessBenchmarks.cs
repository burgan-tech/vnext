using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace BBT.Workflow.Benchmarks;

/// <summary>
/// Instance-data read primitives: JsonData.JsonElement (full parse per access, B1) and
/// ToDynamic (full ExpandoObject tree build, B2/B3).
/// </summary>
[MemoryDiagnoser]
public class InstanceDataAccessBenchmarks
{
    private BBT.Workflow.JsonData _data = null!;

    [Params(10, 50, 200)]
    public int DocKb { get; set; }

    [GlobalSetup]
    public void Setup() => _data = new BBT.Workflow.JsonData(PayloadFactory.Json(DocKb));

    [Benchmark]
    public System.Text.Json.JsonElement ParseJsonElement() => _data.JsonElement;

    [Benchmark]
    public object? ParseAndBuildExpando() => _data.JsonElement.ToDynamic();
}
