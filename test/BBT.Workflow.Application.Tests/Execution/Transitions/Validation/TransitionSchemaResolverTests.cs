using BBT.Workflow.Scripting.Caller;
using BBT.Workflow.Authorization;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Contracts;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Execution.Validation;

/// <summary>
/// Unit tests for <see cref="TransitionSchemaResolver"/>: the one resolver shared by the write path
/// (transition validation) and the read path (schema function).
/// </summary>
public sealed class TransitionSchemaResolverTests
{
    private const string RuleA = """{ "location": "./a.csx", "code": "cmV0dXJuIHRydWU7" }""";
    private const string RuleB = """{ "location": "./b.csx", "code": "cmV0dXJuIGZhbHNlOw==" }""";

    private readonly ITaskConditionService _conditionService = Substitute.For<ITaskConditionService>();
    private readonly IScriptContextFactory _scriptContextFactory = Substitute.For<IScriptContextFactory>();
    private readonly IScriptContextBuilder _builder = Substitute.For<IScriptContextBuilder>();
    private readonly TransitionSchemaResolver _resolver;

    private object? _capturedBody;
    private bool _queryParametersCalled;
    private Dictionary<string, string?>? _capturedHeaders;
    private ICallerAccessor? _capturedCaller;
    private readonly ICallerRoleResolver _roleResolver = Substitute.For<ICallerRoleResolver>();

    public TransitionSchemaResolverTests()
    {
        _builder.WithWorkflow(Arg.Any<Definitions.Workflow?>()).Returns(_builder);
        _builder.WithInstance(Arg.Any<Instance?>()).Returns(_builder);
        _builder.WithRuntime(Arg.Any<IRuntimeInfoProvider>()).Returns(_builder);
        _builder.WithTransition(Arg.Any<string?>()).Returns(_builder);
        _builder.WithBody(Arg.Do<object?>(b => _capturedBody = b)).Returns(_builder);
        _builder.WithHeaders(Arg.Do<Dictionary<string, string?>?>(h => _capturedHeaders = h)).Returns(_builder);
        _builder.WithCaller(Arg.Do<ICallerAccessor?>(c => _capturedCaller = c)).Returns(_builder);
        _builder.WithQueryParameters(Arg.Any<Dictionary<string, object?>?>())
            .Returns(_ => { _queryParametersCalled = true; return _builder; });
        _builder.WithQueryParameters(Arg.Any<Dictionary<string, string?>?>())
            .Returns(_ => { _queryParametersCalled = true; return _builder; });
        _builder.BuildAsync(Arg.Any<CancellationToken>())
            .Returns(_ => new ScriptContext(NullLogger<ScriptContext>.Instance));
        _scriptContextFactory.NewBuilder(Arg.Any<IInstanceRepository>()).Returns(_builder);

        _resolver = new TransitionSchemaResolver(
            new ContractEntrySelector(_conditionService),
            _scriptContextFactory,
            Substitute.For<IInstanceRepository>(),
            Substitute.For<IRuntimeInfoProvider>(),
            new CallerAccessorFactory(_roleResolver, NullLogger<CallerAccessorFactory>.Instance),
            NullLogger<TransitionSchemaResolver>.Instance);
    }

    [Fact]
    public async Task NoSchema_ReturnsNull_WithoutContext()
    {
        var result = await Resolve(Transition(""));

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
        _scriptContextFactory.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task SingleSchema_ReturnsIt_WithoutBuildingContext()
    {
        var result = await Resolve(Transition($$""", "schema": {{Ref("s1")}}"""));

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Key.ShouldBe("s1");
        _scriptContextFactory.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task RuleMatches_ReturnsMatchedReference()
    {
        var transition = Transition($$"""
            , "schemas": [
                { "rule": {{RuleA}}, "schema": {{Ref("s1")}} },
                { "schema": {{Ref("s2")}} }
            ]
            """);
        Condition(true);

        var result = await Resolve(transition);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Key.ShouldBe("s1");
    }

    [Fact]
    public async Task NoMatch_NoFallback_FailsWithTransitionSchemaNotMatched()
    {
        var transition = Transition($$""", "schemas": [ { "rule": {{RuleA}}, "schema": {{Ref("s1")}} } ]""");
        Condition(false);

        var result = await Resolve(transition);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.TransitionSchemaNotMatched);
        result.Error.Code.ShouldBe("Transition:100025");
    }

    [Fact]
    public async Task EveryRuleThrows_NoFallback_Fails()
    {
        var transition = Transition($$"""
            , "schemas": [
                { "rule": {{RuleA}}, "schema": {{Ref("s1")}} },
                { "rule": {{RuleB}}, "schema": {{Ref("s2")}} }
            ]
            """);
        _conditionService
            .ExecuteConditionAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Fail(Error.Failure("script.boom", "compile error")));

        var result = await Resolve(transition);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.TransitionSchemaNotMatched);
    }

    [Fact]
    public async Task StartTransition_NullLatestData_RunsRuleAgainstEmptyObject()
    {
        var transition = Transition($$""", "schemas": [ { "rule": {{RuleA}}, "schema": {{Ref("s1")}} } ]""");
        Condition(true);
        var instance = NewInstance();
        instance.LatestData.ShouldBeNull();

        var result = await _resolver.ResolveAsync(NewWorkflow(), instance, transition, null);

        result.IsSuccess.ShouldBeTrue();
        _capturedBody.ShouldBeOfType<JsonData>().Json.ShouldBe("{}");
    }

    [Fact]
    public async Task Context_HasNoQueryParameters_AndCarriesRequestHeaders()
    {
        var transition = Transition($$""", "schemas": [ { "rule": {{RuleA}}, "schema": {{Ref("s1")}} } ]""");
        Condition(true);
        var headers = new Dictionary<string, string?> { ["x-tenant"] = "acme" };

        var result = await _resolver.ResolveAsync(NewWorkflow(), NewInstance(), transition, headers);

        result.IsSuccess.ShouldBeTrue();
        _queryParametersCalled.ShouldBeFalse();
        _capturedHeaders.ShouldNotBeNull();
        _capturedHeaders["x-tenant"].ShouldBe("acme");
        _builder.Received(1).WithTransition("t");
    }

    [Fact]
    public async Task Context_CarriesARequestCallerAccessor_ResolvedFromTheRequestHeaders()
    {
        var transition = Transition($$""", "schemas": [ { "rule": {{RuleA}}, "schema": {{Ref("s1")}} } ]""");
        Condition(true);
        _roleResolver.ResolveRoleSetAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(new CallerRoleResolution(["morph.approver"], CallerRoleOutcome.Resolved));
        var headers = new Dictionary<string, string?> { ["sub"] = "u1" };

        var result = await _resolver.ResolveAsync(NewWorkflow(), NewInstance(), transition, headers);

        result.IsSuccess.ShouldBeTrue();
        var caller = _capturedCaller.ShouldBeOfType<CallerAccessor>();
        caller.ResolutionStarted.ShouldBeFalse();
        (await caller.HasRoleAsync("morph.approver")).ShouldBeTrue();
        await _roleResolver.Received(1).ResolveRoleSetAsync(
            Arg.Is<IReadOnlyDictionary<string, string?>?>(h => h != null && h["sub"] == "u1"),
            Arg.Any<CancellationToken>());
    }

    private Task<Result<Reference?>> Resolve(Transition transition) =>
        _resolver.ResolveAsync(NewWorkflow(), NewInstance(), transition, null);

    private void Condition(bool verdict) =>
        _conditionService
            .ExecuteConditionAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Ok(verdict));

    private static string Ref(string key) =>
        $$"""{ "key": "{{key}}", "domain": "d", "flow": "sys-schemas", "version": "1.0.0" }""";

    private static Transition Transition(string extra) =>
        JsonSerializer.Deserialize<Transition>(
            $$"""{ "key": "t", "target": "x", "triggerType": 0, "versionStrategy": "Patch", "labels": [] {{extra}} }""",
            JsonSerializerConstants.JsonOptions)!;

    private static Instance NewInstance() => Instance.Create(Guid.NewGuid(), "test-flow", "1.0.0", "test-key");

    private static Definitions.Workflow NewWorkflow()
    {
        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference("test-flow", "test-domain", "sys-flows", "1.0.0"));
        return workflow;
    }
}
