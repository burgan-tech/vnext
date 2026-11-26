using System;
using System.Collections.Generic;
using BBT.Workflow.Scripting.Evaluators;
using System.Text.Json;
using System.Threading;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Caller;
using BBT.Workflow.Scripting.Expressions;
using BBT.Workflow.Scripting.Helpers;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Executors;
using BBT.Workflow.Tasks.Mapping;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BBT.Workflow.Benchmarks;

/// <summary>
/// The same task mapping written as a <c>.csx</c> <see cref="IMapping"/> and as declarative blocks, each run through
/// the runner production uses (<see cref="ScriptTaskMappingRunner"/> / <see cref="DeclarativeTaskMappingRunner"/>)
/// with a NEW <see cref="TaskExecutorContext"/> per op, as the executor builds one per task execution. Both compile
/// caches are warm (the steady state after the first execution); <see cref="MappingFormColdBenchmarks"/> covers the
/// first execution.
/// <list type="bullet">
/// <item>Output: select the task response's <c>customer</c> subtree, copy <c>customer.name</c> from instance data,
/// stamp the time. <c>…Persisted</c> adds the UTF-8 serialization the instance-data append needs, because the two
/// forms hand different objects to it (an <c>ExpandoObject</c> vs a <c>JsonNode</c>).</item>
/// <item>Input: build the URL (base-URL replace + URL-encoded id), copy the instance's <c>customer</c> into the body,
/// add one header. Both start from a clone of the cached task, as the task factory hands one per execution.</item>
/// </list>
/// Not measured, paid the same by both forms: filing the task response on the script context
/// (<c>UpdateScriptContextWithResponse</c>) and the invocation itself.
/// </summary>
[MemoryDiagnoser]
[GcServer(true)]
public class MappingFormBenchmarks
{
    private const string InputData = """{"customer":{"id":"c 42/x","name":"Ada Lovelace","segment":"retail"},"amount":1250.5}""";

    internal const string CsxOutput =
        "public class {0} : IMapping {{" +
        " public System.Threading.Tasks.Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context) => System.Threading.Tasks.Task.FromResult(new ScriptResponse());" +
        " public System.Threading.Tasks.Task<ScriptResponse> OutputHandler(ScriptContext context) {{" +
        "  var data = new System.Dynamic.ExpandoObject();" +
        "  var d = (System.Collections.Generic.IDictionary<string, object>)data;" +
        "  d[\"customer\"] = context.Body.customer;" +
        "  d[\"ownerName\"] = context.Instance.LatestData.Data.JsonElement.GetProperty(\"customer\").GetProperty(\"name\").GetString();" +
        "  d[\"stamp\"] = System.DateTimeOffset.UtcNow;" +
        "  return System.Threading.Tasks.Task.FromResult(new ScriptResponse {{ Data = data }});" +
        " }}" +
        " }}";

    internal const string CsxInput =
        "public class {0} : IMapping {{" +
        " public System.Threading.Tasks.Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context) {{" +
        "  var http = (HttpTask)task;" +
        "  var customer = context.Instance.LatestData.Data.JsonElement.GetProperty(\"customer\");" +
        "  http.SetUrl(http.Url.Replace(\"API_BASEURL\", \"https://api.test\") + \"/\" + System.Uri.EscapeDataString(customer.GetProperty(\"id\").GetString()));" +
        "  http.SetBody(new System.Collections.Generic.Dictionary<string, object> {{ [\"source\"] = \"static\", [\"customer\"] = customer }});" +
        "  http.AddHeader(\"x-channel\", \"web\");" +
        "  return System.Threading.Tasks.Task.FromResult(new ScriptResponse());" +
        " }}" +
        " public System.Threading.Tasks.Task<ScriptResponse> OutputHandler(ScriptContext context) => System.Threading.Tasks.Task.FromResult(new ScriptResponse());" +
        " }}";

    internal static MappingBlock InputBlock(string baseUrl = "\"https://api.test\"") => new(
        "$.data.customer", "$.body.customer",
        new OrderedStringMap(
        [
            new KeyValuePair<string, string>("$.url",
                $"task.url.ToString().Replace(\"API_BASEURL\", {baseUrl}) + \"/\" + urlEncode(data.customer.id)"),
            new KeyValuePair<string, string>("$.headers.x-channel", "\"web\"")
        ]));

    private ScriptTaskMappingRunner _csxRunner = null!;
    private DeclarativeTaskMappingRunner _blockRunner = null!;
    private TaskExecutorContext _csxOutputCtx = null!;
    private TaskExecutorContext _blockOutputCtx = null!;
    private TaskInvocationResult _result = null!;
    private HttpTask _cachedTask = null!;
    private TaskExecutorContext _csxInputCtx = null!;
    private TaskExecutorContext _blockInputCtx = null!;

    [Params(10, 50, 200)]
    public int DocKb { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var json = PayloadFactory.Json(DocKb);
        _csxRunner = new ScriptTaskMappingRunner(MappingFormBench.Engine());
        _blockRunner = new DeclarativeTaskMappingRunner(new ExpressionCompiler(4096), TimeProvider.System);
        _result = DeclarativeBench.Result(json);

        var outputTemplate = DeclarativeBench.Context(json, DeclarativeBench.Block());
        _blockOutputCtx = outputTemplate;
        _csxOutputCtx = MappingFormBench.WithBinding(outputTemplate,
            OnExecuteTask.Create(1, outputTemplate.Task, ScriptCode.FromNative(string.Format(CsxOutput, "FormOutput"))));

        // Input: the instance carries the small customer document the URL and body are built from.
        _cachedTask = HttpTask.Create(JsonSerializer.Deserialize<JsonElement>(
            """{ "url": "API_BASEURL/api/customers", "method": "POST", "body": { "source": "static" }, "headers": { "x-a": "1" } }"""));
        _cachedTask.SetReference(new Reference("bench-input", "bench-domain", "sys-tasks", "1.0.0"));
        var inputTemplate = DeclarativeBench.Context(InputData, DeclarativeBench.Block());
        _blockInputCtx = MappingFormBench.WithBinding(inputTemplate,
            OnExecuteTask.Create(1, _cachedTask, null, input: InputBlock(), output: null), _cachedTask);
        _csxInputCtx = MappingFormBench.WithBinding(inputTemplate,
            OnExecuteTask.Create(1, _cachedTask, ScriptCode.FromNative(string.Format(CsxInput, "FormInput"))), _cachedTask);

        // Warm both caches and prove the two forms produce the same thing.
        var csxOut = (IDictionary<string, object?>)CsxOutputProduction()!;
        var blockOut = JsonSerializer.SerializeToNode(BlockOutputProduction())!.AsObject();
        if (JsonSerializer.Serialize(csxOut["customer"]) != blockOut["customer"]!.ToJsonString()
            || (string?)csxOut["ownerName"] != (string?)blockOut["ownerName"] || blockOut["stamp"] is null)
            throw new InvalidOperationException("Output forms disagree: csx=" + JsonSerializer.Serialize(csxOut) + " block=" + blockOut.ToJsonString());

        var csxTask = (HttpTask)CsxInputProduction();
        var blockTask = (HttpTask)BlockInputProduction();
        const string expectedUrl = "https://api.test/api/customers/c%2042%2Fx";
        if (csxTask.Url != expectedUrl || blockTask.Url != expectedUrl)
            throw new InvalidOperationException($"Input URL differs: csx={csxTask.Url} block={blockTask.Url}");
        if (csxTask.Body?.GetProperty("customer").GetProperty("id").GetString() != "c 42/x"
            || blockTask.Body?.GetProperty("customer").GetProperty("id").GetString() != "c 42/x")
            throw new InvalidOperationException($"Input body differs: csx={csxTask.Body} block={blockTask.Body}");
    }

    [Benchmark(Baseline = true)]
    public object? CsxOutputProduction()
        => _csxRunner.ProcessOutputAsync(_csxOutputCtx.Task, _result, MappingFormBench.Fresh(_csxOutputCtx), CancellationToken.None)
            .GetAwaiter().GetResult().Value;

    [Benchmark]
    public object? BlockOutputProduction()
        => _blockRunner.ProcessOutputAsync(_blockOutputCtx.Task, _result, MappingFormBench.Fresh(_blockOutputCtx), CancellationToken.None)
            .GetAwaiter().GetResult().Value;

    [Benchmark]
    public byte[] CsxOutputPersisted()
        => JsonSerializer.SerializeToUtf8Bytes(CsxOutputProduction(), JsonSerializerConstants.JsonOptions);

    [Benchmark]
    public byte[] BlockOutputPersisted()
        => JsonSerializer.SerializeToUtf8Bytes(BlockOutputProduction(), JsonSerializerConstants.JsonOptions);

    [Benchmark]
    public WorkflowTask CsxInputProduction()
    {
        var task = _cachedTask.Clone();
        _csxRunner.PrepareInputAsync(task, MappingFormBench.Fresh(_csxInputCtx), CancellationToken.None).GetAwaiter().GetResult();
        return task;
    }

    [Benchmark]
    public WorkflowTask BlockInputProduction()
    {
        var task = _cachedTask.Clone();
        _blockRunner.PrepareInputAsync(task, MappingFormBench.Fresh(_blockInputCtx), CancellationToken.None).GetAwaiter().GetResult();
        return task;
    }
}

/// <summary>
/// The first execution of a mapping after publish or a pod start: the <c>.csx</c> is compiled by Roslyn into its own
/// assembly, the block's expressions are parsed by Dynamic Expresso. A unique class name / expression text per call
/// defeats each compile cache. Setup also records, outside the timed runs, how many <c>AssemblyLoadContext</c>s and how
/// much retained heap 20 cold compiles of each form leave behind (<c>bdn-mapping-form-footprint.txt</c> in the temp
/// directory).
/// </summary>
[MemoryDiagnoser]
[GcServer(true)]
public class MappingFormColdBenchmarks
{
    private const int FootprintCompiles = 20;
    private static int _unique;
    private ScriptEngine _engine = null!;
    private DeclarativeTaskMappingRunner _blockRunner = null!;
    private string _json = null!;
    private TaskInvocationResult _result = null!;

    [GlobalSetup]
    public void Setup()
    {
        _engine = MappingFormBench.Engine();
        _blockRunner = new DeclarativeTaskMappingRunner(new ExpressionCompiler(4096), TimeProvider.System);
        _json = PayloadFactory.Json(10);
        _result = DeclarativeBench.Result(_json);
        BlockOutputCold();
        CsxOutputCold();
        RecordFootprint();
    }

    private void RecordFootprint()
    {
        var blockBefore = (Alc: DeclarativeBench.AlcCount(), Heap: GC.GetTotalMemory(forceFullCollection: true));
        var keepBlocks = new List<object?>();
        for (var i = 0; i < FootprintCompiles; i++) keepBlocks.Add(BlockOutputCold());
        var blockAfter = (Alc: DeclarativeBench.AlcCount(), Heap: GC.GetTotalMemory(forceFullCollection: true));

        var keepCsx = new List<IMapping>();
        for (var i = 0; i < FootprintCompiles; i++) keepCsx.Add(CsxOutputCold());
        var csxAfter = (Alc: DeclarativeBench.AlcCount(), Heap: GC.GetTotalMemory(forceFullCollection: true));
        GC.KeepAlive(keepBlocks);
        GC.KeepAlive(keepCsx);

        var line = $"FOOTPRINT compiles={FootprintCompiles} " +
                   $"block: alc+{blockAfter.Alc - blockBefore.Alc} heap+{(blockAfter.Heap - blockBefore.Heap) / 1024} KB; " +
                   $"csx: alc+{csxAfter.Alc - blockAfter.Alc} heap+{(csxAfter.Heap - blockAfter.Heap) / 1024} KB";
        Console.WriteLine("// " + line);
        System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bdn-mapping-form-footprint.txt"), line);
    }

    /// <summary>Cold block: unique expression text (compiler-cache miss) and a fresh context (plan memo miss).</summary>
    [Benchmark]
    public object? BlockOutputCold()
    {
        var n = Interlocked.Increment(ref _unique);
        var ctx = DeclarativeBench.Context(_json, DeclarativeBench.Block($"now.AddSeconds({n})"));
        return _blockRunner.ProcessOutputAsync(ctx.Task, _result, ctx, CancellationToken.None).GetAwaiter().GetResult().Value;
    }

    /// <summary>Cold <c>.csx</c>: a unique class name per call, so the Roslyn compile cache misses.</summary>
    [Benchmark(Baseline = true)]
    public IMapping CsxOutputCold()
    {
        var name = "FormCold" + Interlocked.Increment(ref _unique);
        return _engine.CompileToInstanceAsync<IMapping>(
                ScriptCode.FromNative(string.Format(MappingFormBenchmarks.CsxOutput, name)), cancellationToken: CancellationToken.None)
            .GetAwaiter().GetResult();
    }
}

internal static class MappingFormBench
{
    public static ScriptEngine Engine()
    {
        var evaluator = new CSharpEvaluator();
        return new ScriptEngine(
            evaluator,
            new NoopScriptServices(),
            new ScriptHelperRegistry(evaluator),
            new ScriptHelpersOptions { Enabled = false },
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<ScriptEngine>.Instance);
    }

    /// <summary>A template context with another binding (and optionally another task) over the same script context.</summary>
    public static TaskExecutorContext WithBinding(TaskExecutorContext template, OnExecuteTask binding, WorkflowTask? task = null)
        => new(task ?? template.Task, binding, template.ScriptContext, null, TaskTrigger.OnExecute, TaskExecutionOrigin.Flow)
        {
            Caller = NullCallerAccessor.Instance
        };

    /// <summary>A new per-execution context, as the executor builds one per task execution (memos empty).</summary>
    public static TaskExecutorContext Fresh(TaskExecutorContext template) => WithBinding(template, template.OnExecuteTask);
}
