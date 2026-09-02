using System.Dynamic;
using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace BBT.Workflow.Benchmarks;

/// <summary>
/// Production-shaped task-output delta creation. Both methods leave JsonElement materialized,
/// matching InstanceDataWriteService's immediate read of delta.JsonElement.
/// </summary>
[MemoryDiagnoser]
[GcServer(true)]
public class TaskOutputMaterializationBenchmarks
{
    private object _payload = null!;

    [Params(10, 50, 200)]
    public int PayloadKb { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _payload = JsonSerializer.Deserialize<ExpandoObject>(PayloadFactory.Json(PayloadKb))!;
    }

    [Benchmark(Baseline = true)]
    public JsonData LegacySerializeThenParse()
    {
        var data = new JsonData(JsonSerializer.Serialize(_payload, JsonSerializerConstants.JsonOptions));
        _ = data.JsonElement;
        return data;
    }

    [Benchmark]
    public JsonData MaterializeTextAndElementTogether()
        => JsonData.FromMaterializedObject(_payload, JsonSerializerConstants.JsonOptions);
}
