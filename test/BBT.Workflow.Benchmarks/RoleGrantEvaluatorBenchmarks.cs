using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BenchmarkDotNet.Attributes;

namespace BBT.Workflow.Benchmarks;

/// <summary>
/// A state build's transition filter: ONE evaluator, a dynamic <c>$.context.Instance.Data</c> grant on each of
/// N transitions, ~20 KB of instance data. Before the authorization context was built once per evaluator it
/// was materialised per transition key, each copy carrying the full data (write → array → re-parse).
/// Uses only the public evaluator API, so the same file measures both sides.
/// </summary>
[MemoryDiagnoser]
public class RoleGrantEvaluatorBenchmarks
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly TransitionAuthorizationManager _manager = new(null!, null!);
    private readonly CallerIdentity _caller = new("someone-else", "someone-else");
    private Instance _instance = null!;
    private List<Transition> _transitions = null!;

    [Params(10)]
    public int Transitions { get; set; }

    [Params(20)]
    public int DataKb { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _instance = Instance.Create(Guid.NewGuid(), "bench-flow", "1.0.0", "bench-key");
        var row = (InstanceData)InstanceDataConstructor.Invoke([
            Guid.NewGuid(), _instance.Id, "1.0.0", new JsonData(PayloadFactory.Json(DataKb)), true
        ]);
        VersionNo.SetValue(row, 1L);
        AcceptPersistedData.Invoke(_instance, [row]);

        _transitions = new List<Transition>(Transitions);
        for (var i = 0; i < Transitions; i++)
        {
            _transitions.Add(JsonSerializer.Deserialize<Transition>($$"""
                {
                  "key": "t{{i}}", "from": null, "target": "state-{{i}}", "triggerType": "Manual",
                  "versionStrategy": "None", "labels": [], "onExecutionTasks": [],
                  "roles": [ { "role": "$user.$.context.Instance.Data.customer.name", "grant": "allow" },
                             { "role": "$role.$.context.Transition.Target", "grant": "allow" } ]
                }
                """, JsonOptions)!);
        }
    }

    [Benchmark]
    public async Task<int> FilterTransitions()
    {
        var evaluator = await _manager.CreateEvaluatorAsync(
            _instance, null, null, Array.Empty<RoleGrant>(), _caller);

        var allowed = 0;
        foreach (var transition in _transitions)
        {
            if (evaluator.IsAnyRoleAllowed(["clerk"], transition.Roles, transition))
                allowed++;
        }

        return allowed;
    }
}
