using System;
using System.Collections.Generic;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Tasks.Mapping;

namespace BBT.Workflow.Benchmarks;

/// <summary>
/// Per-task binding preparation on the default (Local) HTTP path, from mapped task to the typed binding
/// the invoker sends. Legacy: <c>TaskBindingMapper.CreateEnvelope</c> (serialize) → caller-credential
/// rewrite through a parsed <c>JsonNode</c> (parse + reserialize) → <c>Deserialize&lt;HttpTaskBinding&gt;</c>
/// in the local invoker. Typed: map to the object, merge the credential on its Headers string, hand it
/// over. <c>WithCaller</c> = the caller credential has to be added (the rewrite path); otherwise the
/// mapping already set every credential header (nothing to add).
/// </summary>
[MemoryDiagnoser]
[GcServer(true)]
public class TaskBindingDispatchBenchmarks
{
    private static readonly IReadOnlyDictionary<string, string> Caller = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["sub"] = "alice", ["act_sub"] = "alice-actor", ["position"] = "HQ", ["client_id"] = "web-client", ["role"] = "maker"
    };

    private HttpTask _task = null!;

    [Params(1, 10)]
    public int BodyKb { get; set; }

    [Params(true, false)]
    public bool WithCaller { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var headers = WithCaller
            ? """{ "User-Agent": "vNext/1.0", "X-Channel": "web" }"""
            : """{ "User-Agent": "vNext/1.0", "sub": "m", "act_sub": "m2", "position": "p", "client_id": "c", "role": "r" }""";
        var config = $$"""{ "url": "https://api.local/orders", "method": "POST", "headers": {{headers}}, "body": {{PayloadFactory.Json(BodyKb)}} }""";
        _task = HttpTask.Create(JsonDocument.Parse(config).RootElement.Clone());
        _task.SetReference(new Reference("bench", "test", "sys-tasks", "1.0.0"));
    }

    [Benchmark(Baseline = true)]
    public HttpTaskBinding LegacyEnvelopeRoundTrip()
    {
        var envelope = TaskBindingMapper.CreateEnvelope(_task).Value!;
        var element = envelope.Binding;
        var binding = System.Text.Json.Nodes.JsonNode.Parse(element.GetRawText())!.AsObject();
        var current = binding["Headers"]?.GetValue<string?>();
        var merged = HttpTaskInvocation.WithCallerCredential(current, Caller);
        if (merged != current)
        {
            binding["Headers"] = merged;
            element = JsonSerializer.SerializeToElement(binding);
        }

        return element.Deserialize<HttpTaskBinding>()!;
    }

    [Benchmark]
    public HttpTaskBinding TypedBinding() =>
        TaskBindingCredentials.WithCallerCredential(
            (HttpTaskBinding)TaskBindingMapper.CreateBinding(_task).Value.Binding, Caller);
}
