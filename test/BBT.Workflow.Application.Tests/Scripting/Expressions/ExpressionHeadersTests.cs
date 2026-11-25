using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Timer;
using BBT.Workflow.Headers;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Caller;
using BBT.Workflow.Scripting.Expressions;
using BBT.Workflow.Scripting.Related;
using BBT.Workflow.Scripting.Rules;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Evaluators;
using BBT.Workflow.Tasks.Executors;
using BBT.Workflow.Tasks.Mapping;
using DynamicExpresso;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Scripting.Expressions;

using InputFn = Func<RuleJsonDynamic, RuleJsonDynamic, ExpressionInstance, DateTimeOffset, ExpressionUser, ExpressoRuleContext, RuleJsonDynamic, ExpressionConfig, ExpressionHeaders, object>;

/// <summary>
/// The <c>headers</c> expression variable (<see cref="ExpressionHeaders"/>): a lazy, read-only view over the very
/// dictionary behind <c>context.Headers</c> — so <c>Workflow:HeaderExposure:Deny</c> applies to both alike, and the
/// runtime's transport headers are never reachable — with the <c>headers["x"]</c> / <c>headers.has("x")</c> shape, in
/// every slot that binds <see cref="ExpressionScope"/>.
/// </summary>
public sealed class ExpressionHeadersTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    private readonly ExpressionCompiler _compiler = new(64);
    private readonly FakeTimeProvider _clock = new(Now);

    // ---- helpers ----

    private static Dictionary<string, string?> RequestHeaders() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Authorization"] = "Bearer t",
        ["x-channel"] = "web",
        ["X-Customer-No"] = "123",
    };

    private static ScriptContextFactory Factory(params string[] deny) =>
        new(
            Substitute.For<IComponentCacheStore>(),
            NullLogger<ScriptContext>.Instance,
            NullLogger<RelatedInstanceAccessor>.Instance,
            headerExposureDenyList: new HeaderExposureDenyList(Options.Create(new HeaderExposureOptions { Deny = [.. deny] })));

    private static Instance InstanceWithData(string json = """{"x":1}""")
    {
        var instance = Instance.Create(Guid.NewGuid(), "headers-flow", "1.0.0", "headers-key");
        var row = new InstanceData(Guid.NewGuid(), instance.Id, "1.0.0", new JsonData(json), true);
        typeof(InstanceData).GetProperty(nameof(InstanceData.VersionNo))!.SetValue(row, 1L);
        instance.AcceptPersistedData(row);
        return instance;
    }

    /// <summary>A context built the way the runtime builds one: the deny list reduces <c>Headers</c>, not the transport view.</summary>
    private static async Task<ScriptContext> BuildAsync(Dictionary<string, string?>? headers = null, params string[] deny) =>
        await Factory(deny)
            .NewBuilder(Substitute.For<IInstanceRepository>())
            .WithRuntime(Substitute.For<IRuntimeInfoProvider>())
            .WithInstance(InstanceWithData())
            .WithHeaders(headers ?? RequestHeaders())
            .BuildAsync();

    private static ScriptContext ContextWith(object? headers) =>
        new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .SetInstance(InstanceWithData())
            .SetHeaders(headers)
            .Build();

    private async Task<object?> EvalAsync(ScriptContext context, string text)
    {
        var signature = ExpressionSignatures.Mapping(ScopeKind.MappingInput);
        var compiled = _compiler.Compile<InputFn>(text, signature);
        compiled.IsSuccess.ShouldBeTrue(compiled.IsSuccess ? null : compiled.Error.Message);
        var scope = ExpressionScope.Create(context, _clock, NullCallerAccessor.Instance, ScopeKind.MappingInput);
        (await scope.PrepareAsync(compiled.Value!.UsedIdentifiers)).IsSuccess.ShouldBeTrue();
        try
        {
            return compiled.Value.Invoke.DynamicInvoke(scope.Bind(signature.Parameters, compiled.Value.UsedIdentifiers));
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static ScriptCode Expr(string code) => ScriptCode.FromNative(code, ConditionScriptLocations.DynamicExpresso);

    // ---- shape ----

    [Theory]
    [InlineData("headers[\"x-channel\"]", "web")]
    [InlineData("headers[\"X-CHANNEL\"]", "web")]
    [InlineData("headers[\"x-customer-no\"]", "123")]
    [InlineData("headers[\"x-missing\"]", null)]
    [InlineData("headers[\"x-missing\"] ?? \"none\"", "none")]
    [InlineData("headers.has(\"X-Channel\")", true)]
    [InlineData("headers.has(\"x-missing\")", false)]
    [InlineData("headers[\"x-channel\"] == \"web\" && !headers.has(\"x-missing\")", true)]
    public async Task Headers_IndexerAndHas_AreCaseInsensitive_MissingIsNull(string text, object? expected)
    {
        (await EvalAsync(await BuildAsync(), text)).ShouldBe(expected);
    }

    [Fact]
    public async Task Headers_AnOrdinalDictionary_StillMatchesCaseInsensitively()
    {
        var context = ContextWith(new Dictionary<string, string?> { ["x-channel"] = "web" });

        (await EvalAsync(context, "headers[\"X-Channel\"]")).ShouldBe("web");
        (await EvalAsync(context, "headers.has(\"X-CHANNEL\")")).ShouldBe(true);
    }

    [Fact]
    public async Task Headers_OtherSourceShapes_ReadTheSameWay()
    {
        IDictionary<string, object?> expando = new ExpandoObject();
        expando["x-channel"] = "web";
        using var doc = JsonDocument.Parse("""{"X-Channel":"mobile","x-n":5}""");

        (await EvalAsync(ContextWith(expando), "headers[\"X-CHANNEL\"]")).ShouldBe("web");
        (await EvalAsync(ContextWith(doc.RootElement), "headers[\"x-channel\"]")).ShouldBe("mobile");
        (await EvalAsync(ContextWith(doc.RootElement), "headers[\"x-n\"]")).ShouldBe("5");
    }

    [Fact]
    public async Task Headers_NoHeadersOnTheContext_IsAnEmptyView()
    {
        var context = ContextWith(null);

        (await EvalAsync(context, "headers[\"x-channel\"]")).ShouldBeNull();
        (await EvalAsync(context, "headers.has(\"x-channel\")")).ShouldBe(false);
    }

    [Fact]
    public void Headers_HyphenatedMemberAccess_DoesNotCompile()
    {
        // headers.x-channel parses as (headers.x) - channel: hyphenated names need the indexer.
        _compiler.Compile<InputFn>("headers.x-channel", ExpressionSignatures.Mapping(ScopeKind.MappingInput))
            .IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void Headers_IsTheLastVariableOfEveryScopeSignature_AndNotOfTheAgentTool()
    {
        foreach (var signature in new[]
                 {
                     ExpressionSignatures.Condition, ExpressionSignatures.Key, ExpressionSignatures.ValueSlot,
                     ExpressionSignatures.Mapping(ScopeKind.MappingInput), ExpressionSignatures.Mapping(ScopeKind.MappingOutput)
                 })
        {
            signature.Parameters[^1].Name.ShouldBe("headers", signature.Name);
            signature.Parameters[^1].Type.ShouldBe(typeof(ExpressionHeaders));
        }

        ExpressionSignatures.AgentDecision.Parameters.Select(p => p.Name).ShouldContain("headers");
        ExpressionSignatures.AgentTool.Parameters.Select(p => p.Name).ShouldNotContain("headers");
    }

    // ---- no copy, lazy ----

    [Fact]
    public async Task Headers_IsAViewOverTheContextDictionary_NotACopy()
    {
        var headers = RequestHeaders();
        var context = await BuildAsync(headers);
        var signature = ExpressionSignatures.Mapping(ScopeKind.MappingInput);
        var used = new HashSet<string> { "headers" };
        var scope = ExpressionScope.Create(context, _clock, NullCallerAccessor.Instance, ScopeKind.MappingInput);
        var view = (ExpressionHeaders)scope.Bind(signature.Parameters, used)[^1]!;

        headers["x-late"] = "added after binding";

        view["x-late"].ShouldBe("added after binding");
        scope.Bind(signature.Parameters, used)[^1].ShouldBeSameAs(view);
    }

    [Fact]
    public async Task Headers_IsNotBuilt_WhenTheExpressionDoesNotUseIt()
    {
        var signature = ExpressionSignatures.Mapping(ScopeKind.MappingInput);
        var compiled = _compiler.Compile<InputFn>("data.x", signature);
        var scope = ExpressionScope.Create(await BuildAsync(), _clock, NullCallerAccessor.Instance, ScopeKind.MappingInput);

        compiled.Value!.UsedIdentifiers.ShouldNotContain("headers");
        scope.Bind(signature.Parameters, compiled.Value.UsedIdentifiers)[^1].ShouldBeNull();
    }

    // ---- header exposure ----

    [Fact]
    public async Task Headers_ADeniedName_IsAbsentFromHeadersAndContextHeadersAlike()
    {
        var context = await BuildAsync(deny: ["authorization", "x-customer-no"]);

        (await EvalAsync(context, "headers.has(\"Authorization\")")).ShouldBe(false);
        (await EvalAsync(context, "headers[\"authorization\"]")).ShouldBeNull();
        (await EvalAsync(context, "headers[\"x-customer-no\"]")).ShouldBeNull();
        (await EvalAsync(context, "headers[\"x-channel\"]")).ShouldBe("web");

        (await EvalAsync(context, "context.Headers[\"authorization\"] == null")).ShouldBe(true);
        (await EvalAsync(context, "context.Headers[\"x-customer-no\"] == null")).ShouldBe(true);
        (await EvalAsync(context, "context.Headers[\"x-channel\"].ToString()")).ShouldBe("web");
    }

    [Fact]
    public async Task Headers_NeverSeesATransportOnlyHeader()
    {
        // The runtime still holds the header (its own credential and identity reads); the expression cannot reach it.
        var context = await BuildAsync(deny: ["authorization"]);

        context.GetTransportHeadersAsDictionary()["Authorization"].ShouldBe("Bearer t");
        (await EvalAsync(context, "headers.has(\"authorization\") || headers[\"Authorization\"] != null")).ShouldBe(false);
    }

    [Fact]
    public async Task Headers_WithAnEmptyDenyList_SeesEveryHeader()
    {
        (await EvalAsync(await BuildAsync(), "headers[\"authorization\"]")).ShouldBe("Bearer t");
    }

    // ---- every slot ----

    [Theory]
    [InlineData("headers[\"x-channel\"] == \"web\"", true)]
    [InlineData("headers[\"x-channel\"] == \"mobile\"", false)]
    [InlineData("headers.has(\"authorization\")", false)]
    public async Task Headers_InAConditionRule(string rule, bool expected)
    {
        var evaluator = new DynamicExpressoConditionEvaluator(
            NullLogger<DynamicExpressoConditionEvaluator>.Instance, _compiler, _clock);

        var result = await evaluator.EvaluateAsync(Expr(rule), await BuildAsync(deny: ["authorization"]));

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        result.Value.ShouldBe(expected);
    }

    [Fact]
    public async Task Headers_InAKey()
    {
        var evaluator = new DynamicExpressoValueEvaluator(
            NullLogger<DynamicExpressoValueEvaluator>.Instance, _compiler, _clock);

        var result = await evaluator.EvaluateAsync(Expr("\"k:\" + headers[\"x-channel\"]"), await BuildAsync());

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        result.Value.ShouldBe("k:web");
    }

    [Fact]
    public async Task Headers_InATimer()
    {
        var evaluator = new ValueSlotExpressionEvaluator(_compiler, _clock);

        var result = await evaluator.EvaluateTimerAsync(
            Expr("headers[\"x-channel\"] == \"web\" ? \"PT5M\" : \"PT1H\""), await BuildAsync());

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        result.Value!.ScheduleType.ShouldBe(TimerScheduleType.Duration);
        result.Value.Duration.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Headers_InATaskMapping()
    {
        var task = HttpTask.Create("""{ "url": "https://x.test", "method": "POST", "body": { "static": 1 } }""".ToJsonElement());
        task.SetReference(new Reference("decl-task", "test-domain", "sys-tasks", "1.0.0"));
        var input = new MappingBlock(null, null, new OrderedStringMap(
        [
            new KeyValuePair<string, string>("$.body.channel", "headers[\"X-Channel\"]"),
            new KeyValuePair<string, string>("$.body.hasAuth", "headers.has(\"authorization\")")
        ]));
        var binding = OnExecuteTask.Create(1, task, null, input: input);
        var context = new TaskExecutorContext(
            task, binding, await BuildAsync(deny: ["authorization"]), null, TaskTrigger.OnExecute, TaskExecutionOrigin.Flow)
        {
            Caller = NullCallerAccessor.Instance
        };
        var runner = new DeclarativeTaskMappingRunner(_compiler, _clock, new ConfigurationBuilder().Build());

        var prepared = await runner.PrepareInputAsync(task, context, CancellationToken.None);

        prepared.IsSuccess.ShouldBeTrue(prepared.IsSuccess ? null : prepared.Error.Message);
        task.Body!.Value.GetRawText().ShouldBe("""{"static":1,"channel":"web","hasAuth":false}""");
    }

    [Fact]
    public void Headers_IsNotAnInputSelectRoot()
    {
        var plan = DeclarativeMappingPlan.Build(
            _compiler, new MappingBlock("$.headers", "$.body.h", null), ScopeKind.MappingInput);

        plan.IsSuccess.ShouldBeFalse();
        plan.Error.Message!.ShouldNotContain("headers,");
    }
}
