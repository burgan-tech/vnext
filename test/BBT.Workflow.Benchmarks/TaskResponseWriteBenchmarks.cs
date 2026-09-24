using BenchmarkDotNet.Attributes;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.Logging.Abstractions;

namespace BBT.Workflow.Benchmarks;

/// <summary>
/// ScriptContext.SetStandardResponse cost — the per-task response write every executor pays
/// (<c>TaskExecutorBase.UpdateScriptContextWithResponse</c>): one serialize+ToDynamic of the
/// response, the in-place merge into Body, and (since the slot-isolation fix for
/// vnext-client-sdk-core#6) one structural <c>DynamicCloner.DeepClone</c> of the response tree
/// for the <c>TaskResponse</c> slot. Steady state: the same response is re-merged, so Body's key
/// set — and therefore the merge cost — stays constant across operations; the before/after delta
/// of this benchmark isolates the clone.
/// <para>
/// Measured at the fix (2026-09-22, same machine, clone hunk reverted vs applied):
/// 10 KB 81.7 → 116.5 μs (+34.8 μs), 50 KB 417.5 → 558.9 μs (+141.4 μs) — accepted as the price
/// of slot correctness; tens of microseconds against task invocations that cost milliseconds.
/// </para>
/// </summary>
[MemoryDiagnoser]
[GcServer(true)]
public class TaskResponseWriteBenchmarks
{
    private ScriptContext _context = null!;
    private StandardTaskResponse _response = null!;

    [Params(10, 50)]
    public int BodyKb { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _context = new ScriptContext(NullLogger<ScriptContext>.Instance);
        var payload = System.Text.Json.JsonSerializer.Deserialize<System.Dynamic.ExpandoObject>(
            PayloadFactory.Json(BodyKb));
        _response = new StandardTaskResponse { IsSuccess = true, StatusCode = 200, Data = payload };
    }

    [Benchmark]
    public void SetStandardResponse() => _context.SetStandardResponse(_response, "benchTask");
}
