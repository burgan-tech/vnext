using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace BBT.Workflow.Benchmarks;

/// <summary>
/// Task audit serialization (B8): full serialize of a RawInvocationResultJson-like payload
/// with JsonSerializerConstants.JsonOptions (IgnoreCycles).
/// </summary>
[MemoryDiagnoser]
[GcServer(true)]
public class AuditSerializeBenchmarks
{
    private object _payload = null!;

    [Params(50)]
    public int ResponseKb { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var data = JsonSerializer.Deserialize<System.Dynamic.ExpandoObject>(PayloadFactory.Json(ResponseKb));
        _payload = new { IsSuccess = true, StatusCode = 200, Data = data };
    }

    [Benchmark]
    public string SerializeAudit()
        => JsonSerializer.Serialize(_payload, BBT.Workflow.JsonSerializerConstants.JsonOptions);
}
