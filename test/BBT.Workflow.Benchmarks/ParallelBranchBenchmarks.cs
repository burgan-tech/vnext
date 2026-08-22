using BenchmarkDotNet.Attributes;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.Logging.Abstractions;

namespace BBT.Workflow.Benchmarks;

/// <summary>
/// ScriptContext.CreateParallelBranch cost (B6): deep JSON round-trip copy of Body + task
/// responses. Paid once per FanOut item; the branch is then discarded, never merged.
/// </summary>
[MemoryDiagnoser]
public class ParallelBranchBenchmarks
{
    private ScriptContext _context = null!;

    [Params(10, 50)]
    public int BodyKb { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _context = new ScriptContext(NullLogger<ScriptContext>.Instance);
        // Populate Body the way executors do: SetStandardResponse merges into Body.
        var payload = System.Text.Json.JsonSerializer.Deserialize<System.Dynamic.ExpandoObject>(
            PayloadFactory.Json(BodyKb));
        _context.SetStandardResponse(new StandardTaskResponse { IsSuccess = true, Data = payload }, "seedTask");
    }

    [Benchmark]
    public ScriptContext CreateBranch() => _context.CreateParallelBranch();
}
