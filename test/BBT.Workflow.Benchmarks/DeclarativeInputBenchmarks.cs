using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Caller;
using BBT.Workflow.Scripting.Expressions;
using BBT.Workflow.Shared;
using BBT.Workflow.Tasks.Executors;
using BBT.Workflow.Tasks.Mapping;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Configuration;

namespace BBT.Workflow.Benchmarks;

/// <summary>
/// Declarative INPUT block over an Http task: the block edits a copy of the task's <c>config</c> and the task re-reads
/// it through its own <c>Configure</c> parser (<see cref="WorkflowTask.ApplyConfig"/>). Each op starts from a fresh
/// clone of the cached task, as the task factory hands one per execution.
/// <list type="bullet">
/// <item><c>CloneOnly</c>: the per-execution clone every task pays anyway (baseline).</item>
/// <item><c>CloneAndApplyConfig</c>: clone + one config copy and re-parse with no expression — the generic write-back's own cost.</item>
/// <item><c>CloneAndInputBlock</c>: clone + the real runner: <c>$.url</c> from <c>task</c> + <c>config</c> + <c>urlEncode</c>,
/// a <c>select/into</c> into the body and one header (plan memo and compiler cache warm).</item>
/// </list>
/// </summary>
[MemoryDiagnoser]
public class DeclarativeInputBenchmarks
{
    private const string Data = """{"customer":{"id":"c-42","name":"Ada Lovelace","segment":"retail"},"amount":1250.5}""";

    private HttpTask _cached = null!;
    private TaskExecutorContext _ctx = null!;
    private DeclarativeTaskMappingRunner _runner = null!;

    [GlobalSetup]
    public void Setup()
    {
        _cached = HttpTask.Create(JsonSerializer.Deserialize<JsonElement>(
            """{ "url": "API_BASEURL/api/customers", "method": "POST", "body": { "source": "static" }, "headers": { "x-a": "1" } }"""));
        _cached.SetReference(new Reference("bench-input", "bench-domain", "sys-tasks", "1.0.0"));

        var input = new MappingBlock("$.data.customer", "$.body.customer", new OrderedStringMap(
        [
            new KeyValuePair<string, string>("$.url",
                "task.url.ToString().Replace(\"API_BASEURL\", config.get(\"Api:BaseUrl\")) + \"/\" + urlEncode(data.customer.id)"),
            new KeyValuePair<string, string>("$.headers.x-channel", "\"web\"")
        ]));
        var template = DeclarativeBench.Context(Data, output: null!);
        var binding = OnExecuteTask.Create(1, _cached, null, input: input, output: null);
        _ctx = new TaskExecutorContext(_cached, binding, template.ScriptContext, null, TaskTrigger.OnExecute, TaskExecutionOrigin.Flow)
        {
            Caller = NullCallerAccessor.Instance
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("Api:BaseUrl", "https://api.test")])
            .Build();
        _runner = new DeclarativeTaskMappingRunner(new ExpressionCompiler(4096), TimeProvider.System, configuration);

        var task = (HttpTask)_cached.Clone();
        var warm = _runner.PrepareInputAsync(task, _ctx, CancellationToken.None).GetAwaiter().GetResult();
        if (!warm.IsSuccess || task.Url != "https://api.test/api/customers/c-42")
            throw new InvalidOperationException("DeclarativeInput warm-up failed: " + (warm.IsSuccess ? task.Url : warm.Error.Message));
    }

    [Benchmark(Baseline = true)]
    public WorkflowTask CloneOnly() => _cached.Clone();

    [Benchmark]
    public WorkflowTask CloneAndApplyConfig()
    {
        var task = _cached.Clone();
        task.ApplyConfig(JsonSerializer.SerializeToElement(System.Text.Json.Nodes.JsonNode.Parse(task.Config.GetRawText())));
        return task;
    }

    [Benchmark]
    public WorkflowTask CloneAndInputBlock()
    {
        var task = _cached.Clone();
        _runner.PrepareInputAsync(task, _ctx, CancellationToken.None).GetAwaiter().GetResult();
        return task;
    }
}
