using System;
using System.Dynamic;
using System.Text.Json;
using System.Threading;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Evaluators;
using BBT.Workflow.Scripting.Helpers;
using BBT.Workflow.Scripting.Rules;
using BBT.Workflow.Tasks.Evaluators;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BBT.Workflow.Benchmarks;

/// <summary>
/// BEFORE numbers for the Roslyn-mapping to Dynamic Expresso refactor (issue #1227). Names are reused
/// by the AFTER comparison, so do not rename them.
/// <list type="bullet">
/// <item><c>EagerContextBuild</c>: today's <see cref="ExpressoRuleContextMapper.FromScriptContext"/>.</item>
/// <item><c>DeConditionWarm</c> / <c>DeValueWarm</c>: the Dynamic Expresso evaluators on a warm
/// compiled-expression cache (each includes the eager context build, as in production).</item>
/// <item><c>RoslynOutputHandlerWarm</c>: compile once in setup, invoke <c>OutputHandler</c> per op.</item>
/// <item><c>RoslynOutputHandlerCold</c>: a unique source per invocation, so each op pays a full compile.</item>
/// </list>
/// Context shape: Body (DocKb), headers, query, route values, metadata; <c>Instance</c>, <c>Workflow</c>,
/// <c>Transition</c> are null (the runtime-only aggregates are not constructible in a micro-benchmark), so
/// the eager build here under-states production by the Instance.Data parse.
/// </summary>
[MemoryDiagnoser]
[GcServer(true)]
public class ExpressionMappingBenchmarks
{
    private const string MappingBody =
        "public class {0} : IMapping {{" +
        " public System.Threading.Tasks.Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context) => System.Threading.Tasks.Task.FromResult(new ScriptResponse());" +
        " public System.Threading.Tasks.Task<ScriptResponse> OutputHandler(ScriptContext context) =>" +
        " System.Threading.Tasks.Task.FromResult(new ScriptResponse {{ Data = new System.Dynamic.ExpandoObject() }});" +
        " }}";

    private static int _uniqueCounter;

    private ScriptContext _context = null!;
    private ScriptEngine _engine = null!;
    private IMapping _warmMapping = null!;
    private ScriptCode _conditionScript = null!;
    private ScriptCode _valueScript = null!;
    private DynamicExpressoConditionEvaluator _condition = null!;
    private DynamicExpressoValueEvaluator _value = null!;

    [Params(10, 50, 200)]
    public int DocKb { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var json = PayloadFactory.Json(DocKb);
        _context = new ScriptContext(NullLogger<ScriptContext>.Instance);
        _context.SetBody(JsonSerializer.Deserialize<ExpandoObject>(json));
        _context.MetaData["source"] = "bench";

        _condition = new DynamicExpressoConditionEvaluator(NullLogger<DynamicExpressoConditionEvaluator>.Instance);
        _value = new DynamicExpressoValueEvaluator(NullLogger<DynamicExpressoValueEvaluator>.Instance);
        _conditionScript = ScriptCode.FromNative("context.Body.customer.segment.ToString() == \"retail\"", ConditionScriptLocations.DynamicExpresso);
        _valueScript = ScriptCode.FromNative("\"k:\" + context.Body.customer.name.ToString()", ConditionScriptLocations.DynamicExpresso);

        var evaluator = new CSharpEvaluator();
        _engine = new ScriptEngine(
            evaluator,
            new NoopScriptServices(),
            new ScriptHelperRegistry(evaluator),
            new ScriptHelpersOptions { Enabled = false },
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<ScriptEngine>.Instance);

        _warmMapping = _engine
            .CompileToInstanceAsync<IMapping>(ScriptCode.FromNative(string.Format(MappingBody, "WarmProbe")))
            .GetAwaiter().GetResult();

        // Warm the expression caches and fail loudly if an expression does not evaluate.
        var conditionResult = _condition.EvaluateAsync(_conditionScript, _context).GetAwaiter().GetResult();
        if (!conditionResult.IsSuccess)
            throw new InvalidOperationException("DeConditionWarm expression failed: " + conditionResult.Error.Message);
        var valueResult = _value.Evaluate(_valueScript, _context);
        if (!valueResult.IsSuccess)
            throw new InvalidOperationException("DeValueWarm expression failed: " + valueResult.Error.Message);
    }

    [Benchmark]
    public ExpressoRuleContext EagerContextBuild() => ExpressoRuleContextMapper.FromScriptContext(_context);

    [Benchmark]
    public object DeConditionWarm()
        => _condition.EvaluateAsync(_conditionScript, _context).GetAwaiter().GetResult();

    [Benchmark]
    public object DeValueWarm() => _value.Evaluate(_valueScript, _context);

    [Benchmark]
    public ScriptResponse RoslynOutputHandlerWarm()
        => _warmMapping.OutputHandler(_context).GetAwaiter().GetResult();

    // A unique class name per invocation defeats the compile cache; the string format is negligible next
    // to a Roslyn compile. An [IterationSetup] source would be shared by every invocation inside one
    // iteration (BenchmarkDotNet batches them), turning all but the first into cache hits.
    [Benchmark]
    public IMapping RoslynOutputHandlerCold()
    {
        var name = "ColdProbe" + Interlocked.Increment(ref _uniqueCounter);
        return _engine
            .CompileToInstanceAsync<IMapping>(ScriptCode.FromNative(string.Format(MappingBody, name)))
            .GetAwaiter().GetResult();
    }
}
