using System;
using System.Text.Json;
using System.Reflection;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace BBT.Workflow.Benchmarks;

/// <summary>
/// Production-shaped ScriptContext branch creation with an Instance attached. This captures the
/// Instance.CreateSnapshot cost omitted by ParallelBranchBenchmarks, including history-row wrappers.
/// </summary>
[MemoryDiagnoser]
[GcServer(true)]
public class ParallelBranchWithInstanceBenchmarks
{
    private static readonly ConstructorInfo InstanceDataConstructor = typeof(InstanceData)
        .GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(Guid), typeof(Guid), typeof(string), typeof(JsonData), typeof(bool)],
            modifiers: null)!;

    private static readonly MethodInfo AcceptPersistedData = typeof(Instance)
        .GetMethod("AcceptPersistedData", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly PropertyInfo VersionNo = typeof(InstanceData)
        .GetProperty(nameof(InstanceData.VersionNo))!;

    private ScriptContext _context = null!;

    [Params(10, 50)]
    public int BodyKb { get; set; }

    [Params(1, 10, 100)]
    public int HistoryRows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var payloadJson = PayloadFactory.Json(BodyKb);
        var payload = JsonSerializer.Deserialize<System.Dynamic.ExpandoObject>(payloadJson)!;
        var instance = Instance.Create(Guid.NewGuid(), "bench-flow", "1.0.0", "bench-key");

        for (var index = 0; index < HistoryRows; index++)
        {
            var row = (InstanceData)InstanceDataConstructor.Invoke([
                Guid.NewGuid(),
                instance.Id,
                $"1.0.{index}",
                new JsonData(payloadJson),
                true
            ]);
            VersionNo.SetValue(row, 1L);
            AcceptPersistedData.Invoke(instance, [row]);
        }

        _context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetInstance(instance)
            .SetBody(payload)
            .Build();
    }

    [Benchmark]
    public ScriptContext CreateBranchWithInstance() => _context.CreateParallelBranch();
}
