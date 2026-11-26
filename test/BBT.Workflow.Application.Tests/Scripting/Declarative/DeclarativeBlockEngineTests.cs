using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Caller;
using BBT.Workflow.Scripting.Declarative;
using BBT.Workflow.Scripting.Expressions;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Scripting.Declarative;

/// <summary>
/// The site-independent declarative block engine (<see cref="DeclarativeBlockEngine"/>): the target and select rules of
/// the non-task sites (transition output, SubFlow input / output), the per-site variable lists, and the plan cache.
/// Task-binding behaviour is pinned by <c>DeclarativeTaskMappingRunnerTests</c>.
/// </summary>
public sealed class DeclarativeBlockEngineTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero));
    private readonly ExpressionCompiler _compiler = new(64);

    [Fact]
    public void SubFlowInput_RejectsTargetOutsideDocument()
    {
        var plan = DeclarativeMappingPlan.Build(
            _compiler, Block(set: [("$.foo", "1")]), DeclarativeSiteKind.SubFlowInput);

        plan.IsSuccess.ShouldBeFalse();
        plan.Error.Message!.ShouldContain("$.foo");
        plan.Error.Message!.ShouldContain("attributes, headers, key, tags");
    }

    [Fact]
    public void SubFlowInput_SelectNeedsInto_AndIntoNeedsSelect()
    {
        DeclarativeMappingPlan.Build(_compiler, Block(select: "$.data.x"), DeclarativeSiteKind.SubFlowInput)
            .IsSuccess.ShouldBeFalse();
        DeclarativeMappingPlan.Build(_compiler, Block(into: "$.attributes"), DeclarativeSiteKind.SubFlowInput)
            .IsSuccess.ShouldBeFalse();
        DeclarativeMappingPlan.Build(_compiler, Block(select: "$.data.x", into: "$.attributes"), DeclarativeSiteKind.SubFlowInput)
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void SubFlowInput_KeyMustBeWholeMember()
    {
        var plan = DeclarativeMappingPlan.Build(
            _compiler, Block(set: [("$.key.x", "\"k\"")]), DeclarativeSiteKind.SubFlowInput);

        plan.IsSuccess.ShouldBeFalse();
        plan.Error.Message!.ShouldContain("$.key.x");

        DeclarativeMappingPlan.Build(_compiler, Block(set: [("$.key", "\"k\"")]), DeclarativeSiteKind.SubFlowInput)
            .IsSuccess.ShouldBeTrue();
        DeclarativeMappingPlan.Build(_compiler, Block(set: [("$.attributes.a.b", "1")]), DeclarativeSiteKind.SubFlowInput)
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task SubFlowOutput_DefaultSelectReadsResultData()
    {
        var plan = Build(Block(into: "$.kyc"), DeclarativeSiteKind.SubFlowOutput);
        var result = JsonSerializer.Deserialize<JsonElement>("""{"data":{"a":1},"status":"completed"}""");
        var scope = ExpressionScope.Create(Context(), _clock, NullCallerAccessor.Instance, ScopeKind.SubFlowOutput, result);
        var target = new JsonObject();

        var applied = await DeclarativeBlockEngine.ApplyAsync(plan, scope, target, result, Site(), CancellationToken.None);

        applied.IsSuccess.ShouldBeTrue(applied.IsSuccess ? null : applied.Error.Message);
        target.ToJsonString().ShouldBe("""{"kyc":{"a":1}}""");
    }

    [Fact]
    public async Task SubFlowOutput_SetReadsResult()
    {
        var plan = Build(Block(set: [("$.childStatus", "result.status")]), DeclarativeSiteKind.SubFlowOutput);
        var result = JsonSerializer.Deserialize<JsonElement>("""{"data":{"a":1},"status":"completed"}""");
        var scope = ExpressionScope.Create(Context(), _clock, NullCallerAccessor.Instance, ScopeKind.SubFlowOutput, result);
        var target = new JsonObject();

        var applied = await DeclarativeBlockEngine.ApplyAsync(plan, scope, target, result, Site(), CancellationToken.None);

        applied.IsSuccess.ShouldBeTrue(applied.IsSuccess ? null : applied.Error.Message);
        target.ToJsonString().ShouldBe("""{"childStatus":"completed"}""");
    }

    [Fact]
    public async Task TransitionOutput_SelectReadsVariables()
    {
        var plan = Build(Block(select: "$.input.application", into: "$.application"), DeclarativeSiteKind.TransitionOutput);
        var scope = ExpressionScope.Create(Context(), _clock, NullCallerAccessor.Instance, ScopeKind.TransitionOutput);
        var target = new JsonObject();

        var applied = await DeclarativeBlockEngine.ApplyAsync(plan, scope, target, null, Site(), CancellationToken.None);

        applied.IsSuccess.ShouldBeTrue(applied.IsSuccess ? null : applied.Error.Message);
        target.ToJsonString().ShouldBe("""{"application":{"amount":7}}""");
    }

    [Fact]
    public async Task TransitionOutput_SelectWithoutInto_MergesAtTheRoot()
    {
        var plan = Build(Block(select: "$.data.customer"), DeclarativeSiteKind.TransitionOutput);
        var scope = ExpressionScope.Create(Context(), _clock, NullCallerAccessor.Instance, ScopeKind.TransitionOutput);
        var target = new JsonObject();

        var applied = await DeclarativeBlockEngine.ApplyAsync(plan, scope, target, null, Site(), CancellationToken.None);

        applied.IsSuccess.ShouldBeTrue(applied.IsSuccess ? null : applied.Error.Message);
        target.ToJsonString().ShouldBe("""{"id":7}""");
    }

    [Fact]
    public void TransitionOutput_ResultIsNotAVariable()
    {
        var plan = DeclarativeMappingPlan.Build(
            _compiler, Block(set: [("$.x", "result.data")]), DeclarativeSiteKind.TransitionOutput);

        plan.IsSuccess.ShouldBeFalse();
        plan.Error.Message!.ShouldContain("set['$.x']");
        // A compile failure naming the unknown identifier — not some other rejection of the entry (an empty
        // expression, an unwritable target).
        plan.Error.Message.ShouldContain("does not compile");
        plan.Error.Message.ShouldContain("result");

        // The same expression compiles where result IS a variable, so the failure is about `result` alone.
        DeclarativeMappingPlan.Build(_compiler, Block(set: [("$.x", "result.data")]), DeclarativeSiteKind.SubFlowOutput)
            .IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(DeclarativeSiteKind.TransitionOutput, "task.url")]
    [InlineData(DeclarativeSiteKind.SubFlowInput, "config[\"x\"]")]
    [InlineData(DeclarativeSiteKind.SubFlowOutput, "urlEncode(\"a b\")")]
    public void NonTaskSites_HaveNoTaskVariables(DeclarativeSiteKind kind, string expression)
    {
        var target = kind == DeclarativeSiteKind.SubFlowInput ? "$.attributes.x" : "$.x";

        DeclarativeMappingPlan.Build(_compiler, Block(set: [(target, expression)]), kind).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task SubFlowInput_SelectWritesIntoAttributes()
    {
        var plan = Build(Block(select: "$.data.customer", into: "$.attributes.customer", set: [("$.key", "instance.key")]),
            DeclarativeSiteKind.SubFlowInput);
        var scope = ExpressionScope.Create(Context(), _clock, NullCallerAccessor.Instance, ScopeKind.SubFlowInput);
        var target = new JsonObject();

        var applied = await DeclarativeBlockEngine.ApplyAsync(plan, scope, target, null, Site(), CancellationToken.None);

        applied.IsSuccess.ShouldBeTrue(applied.IsSuccess ? null : applied.Error.Message);
        target.ToJsonString().ShouldBe("""{"attributes":{"customer":{"id":7}},"key":"ctx-key"}""");
    }

    [Fact]
    public async Task MissingSource_WritesNothing()
    {
        var plan = Build(Block(select: "$.data.missing", into: "$.x", set: [("$.y", "data.missing.deep")]),
            DeclarativeSiteKind.TransitionOutput);
        var scope = ExpressionScope.Create(Context(), _clock, NullCallerAccessor.Instance, ScopeKind.TransitionOutput);
        var target = new JsonObject();

        var applied = await DeclarativeBlockEngine.ApplyAsync(plan, scope, target, null, Site(), CancellationToken.None);

        applied.IsSuccess.ShouldBeTrue();
        target.Count.ShouldBe(0);
    }

    [Fact]
    public async Task SetFailure_NamesTheSite()
    {
        var plan = Build(Block(set: [("$.x", "1 / data.zero")]), DeclarativeSiteKind.TransitionOutput);
        var scope = ExpressionScope.Create(Context("""{"zero":0}"""), _clock, NullCallerAccessor.Instance, ScopeKind.TransitionOutput);

        var applied = await DeclarativeBlockEngine.ApplyAsync(
            plan, scope, new JsonObject(), null, new DeclarativeSite("approve", "output"), CancellationToken.None);

        applied.IsSuccess.ShouldBeFalse();
        applied.Error.Message!.ShouldStartWith("approve declarative output mapping failed: set['$.x']");
    }

    [Fact]
    public void PlanCache_SameBlockSameKind_ReturnsSameInstance()
    {
        var block = Block(set: [("$.x", "1")]);

        var first = DeclarativePlanCache.GetOrBuild(_compiler, block, DeclarativeSiteKind.TransitionOutput);
        var second = DeclarativePlanCache.GetOrBuild(_compiler, block, DeclarativeSiteKind.TransitionOutput);
        var otherKind = DeclarativePlanCache.GetOrBuild(_compiler, block, DeclarativeSiteKind.SubFlowOutput);
        var equalBlock = DeclarativePlanCache.GetOrBuild(_compiler, Block(set: [("$.x", "1")]), DeclarativeSiteKind.TransitionOutput);

        first.IsSuccess.ShouldBeTrue();
        second.Value.ShouldBeSameAs(first.Value);
        otherKind.Value.ShouldNotBeSameAs(first.Value);
        equalBlock.Value.ShouldNotBeSameAs(first.Value); // keyed by the block instance, not by value equality
    }

    [Fact]
    public void Signatures_HaveTheirOwnNames()
    {
        ExpressionSignatures.Mapping(ScopeKind.TransitionOutput).Name.ShouldBe("transition-output");
        ExpressionSignatures.Mapping(ScopeKind.SubFlowInput).Name.ShouldBe("subflow-input");
        ExpressionSignatures.Mapping(ScopeKind.SubFlowOutput).Name.ShouldBe("subflow-output");
        ExpressionSignatures.Mapping(ScopeKind.SubFlowOutput).Parameters.Select(p => p.Name)
            .ShouldBe(["data", "input", "instance", "now", "user", "context", "result", "headers"]);
        ExpressionSignatures.Mapping(ScopeKind.TransitionOutput).Parameters.Select(p => p.Name)
            .ShouldBe(["data", "input", "instance", "now", "user", "context", "headers"]);
    }

    [Fact]
    public void EventInput_DataIsUnknown()
    {
        var plan = DeclarativeMappingPlan.Build(
            _compiler, Block(set: [("$.key", "data.id")]), DeclarativeSiteKind.EventInput);

        plan.IsSuccess.ShouldBeFalse();
        plan.Error.Message!.ShouldContain("does not compile");
        plan.Error.Message.ShouldContain("data");

        // instance and user are not event variables either; input is.
        DeclarativeMappingPlan.Build(_compiler, Block(set: [("$.key", "instance.key")]), DeclarativeSiteKind.EventInput)
            .IsSuccess.ShouldBeFalse();
        DeclarativeMappingPlan.Build(_compiler, Block(set: [("$.key", "user.available")]), DeclarativeSiteKind.EventInput)
            .IsSuccess.ShouldBeFalse();
        DeclarativeMappingPlan.Build(_compiler, Block(set: [("$.key", "input.id")]), DeclarativeSiteKind.EventInput)
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void EventInput_KeyMustBeWholeMember()
    {
        var plan = DeclarativeMappingPlan.Build(
            _compiler, Block(set: [("$.key.x", "\"k\"")]), DeclarativeSiteKind.EventInput);

        plan.IsSuccess.ShouldBeFalse();
        plan.Error.Message!.ShouldContain("$.key.x");

        var outside = DeclarativeMappingPlan.Build(
            _compiler, Block(set: [("$.data.x", "1")]), DeclarativeSiteKind.EventInput);
        outside.IsSuccess.ShouldBeFalse();
        outside.Error.Message!.ShouldContain("key, attributes");

        DeclarativeMappingPlan.Build(_compiler, Block(set: [("$.key", "\"k\"")]), DeclarativeSiteKind.EventInput)
            .IsSuccess.ShouldBeTrue();
        DeclarativeMappingPlan.Build(_compiler, Block(set: [("$.attributes.a.b", "1")]), DeclarativeSiteKind.EventInput)
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task EventInput_SelectInputIntoAttributes()
    {
        var plan = Build(Block(select: "$.input", into: "$.attributes"), DeclarativeSiteKind.EventInput);
        var scope = ExpressionScope.Create(Context(input: new Dictionary<string, object> { ["a"] = 1 }), _clock,
            NullCallerAccessor.Instance, ScopeKind.EventInput);
        var target = new JsonObject();

        var applied = await DeclarativeBlockEngine.ApplyAsync(plan, scope, target, null, Site(), CancellationToken.None);

        applied.IsSuccess.ShouldBeTrue(applied.IsSuccess ? null : applied.Error.Message);
        target.ToJsonString().ShouldBe("""{"attributes":{"a":1}}""");
    }

    [Theory]
    [InlineData(DeclarativeSiteKind.WorkflowOutput)]
    [InlineData(DeclarativeSiteKind.FunctionOutput)]
    public void WorkflowOutput_TargetOutsideDocument_Fails(DeclarativeSiteKind kind)
    {
        var plan = DeclarativeMappingPlan.Build(_compiler, Block(set: [("$.foo", "1")]), kind);

        plan.IsSuccess.ShouldBeFalse();
        plan.Error.Message!.ShouldContain("$.foo");
        plan.Error.Message.ShouldContain("data, statusCode, headers");

        DeclarativeMappingPlan.Build(_compiler, Block(set: [("$.statusCode.x", "1")]), kind)
            .IsSuccess.ShouldBeFalse();
        DeclarativeMappingPlan.Build(
                _compiler, Block(set: [("$.statusCode", "201"), ("$.data.a", "1"), ("$.headers.x-a", "\"v\"")]), kind)
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task FunctionOutput_SelectReadsResult()
    {
        var plan = Build(Block(select: "$.getX.body", into: "$.data"), DeclarativeSiteKind.FunctionOutput);
        var result = JsonSerializer.Deserialize<JsonElement>(
            """{"getX":{"body":{"n":1},"headers":{},"statusCode":200}}""");
        var scope = ExpressionScope.Create(Context(), _clock, NullCallerAccessor.Instance, ScopeKind.FunctionOutput, result);
        var target = new JsonObject();

        var applied = await DeclarativeBlockEngine.ApplyAsync(plan, scope, target, result, Site(), CancellationToken.None);

        applied.IsSuccess.ShouldBeTrue(applied.IsSuccess ? null : applied.Error.Message);
        target.ToJsonString().ShouldBe("""{"data":{"n":1}}""");
    }

    [Fact]
    public async Task FunctionOutput_SetReadsResult()
    {
        var plan = Build(Block(set: [("$.statusCode", "result.getX.statusCode")]), DeclarativeSiteKind.FunctionOutput);
        var result = JsonSerializer.Deserialize<JsonElement>(
            """{"getX":{"body":{"n":1},"headers":{},"statusCode":200}}""");
        var scope = ExpressionScope.Create(Context(), _clock, NullCallerAccessor.Instance, ScopeKind.FunctionOutput, result);
        var target = new JsonObject();

        var applied = await DeclarativeBlockEngine.ApplyAsync(plan, scope, target, result, Site(), CancellationToken.None);

        applied.IsSuccess.ShouldBeTrue(applied.IsSuccess ? null : applied.Error.Message);
        target.ToJsonString().ShouldBe("""{"statusCode":200}""");
    }

    [Theory]
    [InlineData(DeclarativeSiteKind.EventInput, "$.input")]
    [InlineData(DeclarativeSiteKind.WorkflowOutput, "$.data")]
    [InlineData(DeclarativeSiteKind.FunctionOutput, "$.getX.body")]
    public void NewSites_SelectWithoutInto_Fails(DeclarativeSiteKind kind, string select)
    {
        var plan = DeclarativeMappingPlan.Build(_compiler, Block(select: select), kind);

        plan.IsSuccess.ShouldBeFalse();
        plan.Error.Message!.ShouldContain("'into'");
    }

    [Theory]
    [InlineData(DeclarativeSiteKind.EventInput, "$.attributes")]
    [InlineData(DeclarativeSiteKind.WorkflowOutput, "$.data")]
    [InlineData(DeclarativeSiteKind.FunctionOutput, "$.data")]
    public void NewSites_IntoWithoutSelect_Fails(DeclarativeSiteKind kind, string into)
    {
        var plan = DeclarativeMappingPlan.Build(_compiler, Block(into: into), kind);

        plan.IsSuccess.ShouldBeFalse();
        plan.Error.Message!.ShouldContain("'select'");
    }

    [Fact]
    public void WorkflowOutput_ResultIsNotAVariable()
    {
        DeclarativeMappingPlan.Build(_compiler, Block(set: [("$.data.x", "result.a")]), DeclarativeSiteKind.WorkflowOutput)
            .IsSuccess.ShouldBeFalse();
        DeclarativeMappingPlan.Build(_compiler, Block(set: [("$.data.x", "result.a")]), DeclarativeSiteKind.FunctionOutput)
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void NewSites_SignaturesAndScopes()
    {
        DeclarativeMappingPlan.ScopeOf(DeclarativeSiteKind.EventInput).ShouldBe(ScopeKind.EventInput);
        DeclarativeMappingPlan.ScopeOf(DeclarativeSiteKind.WorkflowOutput).ShouldBe(ScopeKind.WorkflowOutput);
        DeclarativeMappingPlan.ScopeOf(DeclarativeSiteKind.FunctionOutput).ShouldBe(ScopeKind.FunctionOutput);

        ExpressionSignatures.Mapping(ScopeKind.EventInput).Name.ShouldBe("event-input");
        ExpressionSignatures.Mapping(ScopeKind.WorkflowOutput).Name.ShouldBe("workflow-output");
        ExpressionSignatures.Mapping(ScopeKind.FunctionOutput).Name.ShouldBe("function-output");
        ExpressionSignatures.Mapping(ScopeKind.EventInput).Parameters.Select(p => p.Name)
            .ShouldBe(["input", "now", "context", "headers"]);
        ExpressionSignatures.Mapping(ScopeKind.WorkflowOutput).Parameters.Select(p => p.Name)
            .ShouldBe(["data", "input", "instance", "now", "user", "context", "headers"]);
        ExpressionSignatures.Mapping(ScopeKind.FunctionOutput).Parameters.Select(p => p.Name)
            .ShouldBe(["data", "input", "instance", "now", "user", "context", "result", "headers"]);

        Build(Block(select: "$.input", into: "$.attributes"), DeclarativeSiteKind.EventInput).IsInputStyle.ShouldBeTrue();
        Build(Block(set: [("$.data", "data")]), DeclarativeSiteKind.WorkflowOutput).IsInputStyle.ShouldBeFalse();
    }

    // ---- helpers ----

    private DeclarativeMappingPlan Build(MappingBlock block, DeclarativeSiteKind kind)
    {
        var plan = DeclarativeMappingPlan.Build(_compiler, block, kind);
        plan.IsSuccess.ShouldBeTrue(plan.IsSuccess ? null : plan.Error.Message);
        return plan.Value!;
    }

    private static DeclarativeSite Site() => new("test-site", "output");

    private static MappingBlock Block(string? select = null, string? into = null, (string Path, string Expr)[]? set = null) =>
        new(select, into, set is null
            ? null
            : new OrderedStringMap(set.Select(e => new KeyValuePair<string, string>(e.Path, e.Expr))));

    private static ScriptContext Context(
        string instanceJson = """{"customer":{"id":7}}""", Dictionary<string, object>? input = null)
    {
        var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0.0", "ctx-key");
        var row = new InstanceData(Guid.NewGuid(), instance.Id, "1.0.0", new JsonData(instanceJson), true);
        typeof(InstanceData).GetProperty(nameof(InstanceData.VersionNo))!.SetValue(row, 1L);
        instance.AcceptPersistedData(row);
        return new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .SetInstance(instance)
            .SetCurrentTransition(new ScriptTransitionRequest(
                () => input ?? new Dictionary<string, object> { ["application"] = new Dictionary<string, object> { ["amount"] = 7 } },
                () => new Dictionary<string, object?>()))
            .Build();
    }
}
