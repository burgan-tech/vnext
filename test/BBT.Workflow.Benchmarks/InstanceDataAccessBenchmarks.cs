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
    private string _json = null!;

    [Params(10, 50, 200)]
    public int DocKb { get; set; }

    [GlobalSetup]
    public void Setup() => _json = PayloadFactory.Json(DocKb);

    // Katman 2 / Task 1 landed a JsonElement memo on JsonData (JsonElement ??= parse-once). A
    // shared _data field would now measure the cached path after the first invocation, hiding the
    // parse cost this benchmark exists to track. Each invocation below constructs a fresh JsonData
    // from the setup-produced _json string by design, so ParseJsonElement/ParseAndBuildExpando keep
    // measuring the cold parse/build cost regardless of the memo.
    [Benchmark]
    public System.Text.Json.JsonElement ParseJsonElement() => new BBT.Workflow.JsonData(_json).JsonElement;

    [Benchmark]
    public object? ParseAndBuildExpando() => new BBT.Workflow.JsonData(_json).JsonElement.ToDynamic();
}
