using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Reflection;
using BBT.Workflow.Instances;
using System.Text.Json;
using System.Threading;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Caller;
using BBT.Workflow.Scripting.Evaluators;
using BBT.Workflow.Scripting.Expressions;
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
/// <item><c>LazyScopeDataOnly</c>: an <see cref="ExpressionScope"/> bound for an expression that uses only
/// <c>data</c> (full mapping-input arity, unused slots default). Gate G-P2 compares it with <c>EagerContextBuild</c>.</item>
/// <item><c>DeConditionWarm</c> / <c>DeValueWarm</c>: the Dynamic Expresso evaluators on a warm
/// compiled-expression cache (each includes the eager context build, as in production).</item>
/// <item><c>RoslynOutputHandlerWarm</c>: compile once in setup, invoke <c>OutputHandler</c> per op; the handler is a
/// 3-entry output mapping (Body subtree, instance-data scalar, timestamp).</item>
/// <item><c>DeOutputMapping3Warm</c>: the same 3 entries as three Dynamic Expresso value evaluations.</item>
/// <item><c>RoslynOutputHandlerCold</c> (class <see cref="ExpressionMappingColdBenchmarks"/>): cold compile of that handler.</item>
/// </list>
/// Context shape: Body (task response) and Instance.LatestData both carry the DocKb payload; headers/query/route
/// are null; <c>Workflow</c>/<c>Transition</c> are null.
/// </summary>
[MemoryDiagnoser]
[GcServer(true)]
public class ExpressionMappingBenchmarks
{
    // 3-entry output mapping: one Body subtree, one instance-data scalar, one timestamp.
    internal const string MappingBody =
        "public class {0} : IMapping {{" +
        " public System.Threading.Tasks.Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context) => System.Threading.Tasks.Task.FromResult(new ScriptResponse());" +
        " public System.Threading.Tasks.Task<ScriptResponse> OutputHandler(ScriptContext context) {{" +
        "  var data = new System.Dynamic.ExpandoObject();" +
        "  var d = (System.Collections.Generic.IDictionary<string, object>)data;" +
        "  d[\"customer\"] = context.Body.customer;" +
        "  d[\"ownerName\"] = context.Instance.LatestData.Data.JsonElement.GetProperty(\"customer\").GetProperty(\"name\").GetString();" +
        "  d[\"stamp\"] = System.DateTimeOffset.UtcNow.ToString(\"o\");" +
        "  return System.Threading.Tasks.Task.FromResult(new ScriptResponse {{ Data = data }});" +
        " }}" +
        " }}";

    // The same 3 entries via Dynamic Expresso: three value-evaluator calls, each paying the eager context build.
    private static readonly string[] OutputExpressions =
    [
        "context.Body.customer.ToString()",
        "context.Instance.Data.customer.name.ToString()",
        "DateTime.UtcNow.ToString(\"o\")"
    ];

    private ScriptCode[] _outputScripts = null!;

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
        // Body = task response (output phase); Instance.LatestData = the same DocKb payload.
        var instance = Instance.Create(Guid.NewGuid(), "bench-flow", "1.0.0", "bench-key");
        var row = (InstanceData)InstanceDataConstructor.Invoke([
            Guid.NewGuid(), instance.Id, "1.0.0", new JsonData(json), true
        ]);
        VersionNo.SetValue(row, 1L);
        AcceptPersistedData.Invoke(instance, [row]);
        _context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetInstance(instance)
            .SetBody(JsonSerializer.Deserialize<ExpandoObject>(json))
            .Build();
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
        _outputScripts = Array.ConvertAll(OutputExpressions,
            e => ScriptCode.FromNative(e, ConditionScriptLocations.DynamicExpresso));
        foreach (var script in _outputScripts)
        {
            var r = _value.Evaluate(script, _context);
            if (!r.IsSuccess)
                throw new InvalidOperationException("DeOutputMapping3Warm expression failed: " + r.Error.Message);
        }
        var valueResult = _value.Evaluate(_valueScript, _context);
        if (!valueResult.IsSuccess)
            throw new InvalidOperationException("DeValueWarm expression failed: " + valueResult.Error.Message);
    }

    [Benchmark]
    public ExpressoRuleContext EagerContextBuild() => ExpressoRuleContextMapper.FromScriptContext(_context);

    private static readonly IReadOnlyList<DynamicExpresso.Parameter> MappingInputParameters =
        ExpressionSignatures.Mapping(ScopeKind.MappingInput).Parameters;

    private static readonly IReadOnlySet<string> DataOnly = new HashSet<string>(StringComparer.Ordinal) { ExpressionScope.DataVariable };

    [Benchmark]
    public object?[] LazyScopeDataOnly()
        => ExpressionScope.Create(_context, TimeProvider.System, NullCallerAccessor.Instance, ScopeKind.MappingInput)
            .Bind(MappingInputParameters, DataOnly);

    [Benchmark]
    public object DeConditionWarm()
        => _condition.EvaluateAsync(_conditionScript, _context).GetAwaiter().GetResult();

    [Benchmark]
    public object DeValueWarm() => _value.Evaluate(_valueScript, _context);

    [Benchmark]
    public ScriptResponse RoslynOutputHandlerWarm()
        => _warmMapping.OutputHandler(_context).GetAwaiter().GetResult();

    [Benchmark]
    public object DeOutputMapping3Warm()
    {
        object? last = null;
        foreach (var script in _outputScripts)
            last = _value.Evaluate(script, _context);
        return last!;
    }
}

/// <summary>
/// Cold compile of the 3-entry mapping. A unique class name per invocation defeats the compile cache (an
/// [IterationSetup] source would be shared by every invocation inside one BenchmarkDotNet iteration, turning
/// all but the first into hits). Not parameterised by DocKb: compile cost does not depend on payload size.
/// </summary>
[MemoryDiagnoser]
[GcServer(true)]
public class ExpressionMappingColdBenchmarks
{
    private static int _uniqueCounter;
    private ScriptEngine _engine = null!;

    [GlobalSetup]
    public void Setup()
    {
        var evaluator = new CSharpEvaluator();
        _engine = new ScriptEngine(
            evaluator,
            new NoopScriptServices(),
            new ScriptHelperRegistry(evaluator),
            new ScriptHelpersOptions { Enabled = false },
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<ScriptEngine>.Instance);
    }

    [Benchmark]
    public IMapping RoslynOutputHandlerCold()
    {
        var name = "ColdProbe" + Interlocked.Increment(ref _uniqueCounter);
        return _engine
            .CompileToInstanceAsync<IMapping>(ScriptCode.FromNative(string.Format(ExpressionMappingBenchmarks.MappingBody, name)))
            .GetAwaiter().GetResult();
    }
}
