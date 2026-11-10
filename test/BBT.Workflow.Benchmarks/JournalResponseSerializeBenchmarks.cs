using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Benchmarks;

/// <summary>
/// Per-task journal + instance-data serialization of a task response
/// (<c>TaskExecutionEngine.ExecuteCoreAsync</c>). Legacy: serialize the whole response (walking the Data
/// graph) AND materialize the Data delta (walking it again). Reuse: materialize the delta once and write the
/// journal response with that element standing in for Data (a token copy). NoJournal: history none / no
/// persistence strategy — only the delta is built.
/// </summary>
[MemoryDiagnoser]
[GcServer(true)]
public class JournalResponseSerializeBenchmarks
{
    private StandardTaskResponse _response = null!;

    [Params(10, 50)]
    public int PayloadKb { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _response = new StandardTaskResponse
        {
            IsSuccess = true,
            StatusCode = 200,
            Data = JsonSerializer.Deserialize<ExpandoObject>(PayloadFactory.Json(PayloadKb)),
            Headers = new Dictionary<string, string> { ["content-type"] = "application/json" },
            Metadata = new Dictionary<string, object> { ["Url"] = "https://api.local", ["Method"] = "POST" },
            ExecutionDurationMs = 12,
            TaskType = "Http"
        };
    }

    [Benchmark(Baseline = true)]
    public (JsonData Journal, JsonData Delta) LegacySerializeTwice()
    {
        var journal = new JsonData(JsonSerializer.Serialize(_response, JsonSerializerConstants.JsonOptions));
        var delta = JsonData.FromMaterializedObject((object)_response.Data!, JsonSerializerConstants.JsonOptions);
        return (journal, delta);
    }

    [Benchmark]
    public (JsonData Journal, JsonData Delta) ReuseMaterializedDelta()
    {
        object original = _response.Data!;
        var delta = JsonData.FromMaterializedObject(original, JsonSerializerConstants.JsonOptions);
        try
        {
            _response.Data = delta.JsonElement;
            return (new JsonData(JsonSerializer.Serialize(_response, JsonSerializerConstants.JsonOptions)), delta);
        }
        finally
        {
            _response.Data = original;
        }
    }

    [Benchmark]
    public JsonData NoJournal_DeltaOnly() =>
        JsonData.FromMaterializedObject((object)_response.Data!, JsonSerializerConstants.JsonOptions);
}
