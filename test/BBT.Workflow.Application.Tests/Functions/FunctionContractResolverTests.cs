using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Functions.Contracts;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
using BBT.Workflow.Selection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Functions;

/// <summary>
/// Unit tests for <see cref="FunctionContractResolver"/>: entries are evaluated in declaration order,
/// the first match wins, a rule-less entry short-circuits, a failed rule is skipped rather than fatal,
/// and a slot that matches nothing is "no contract" rather than an error.
/// </summary>
public sealed class FunctionContractResolverTests
{
    private readonly ITaskConditionService _conditionService = Substitute.For<ITaskConditionService>();
    private readonly FunctionContractResolver _resolver;

    private int _scriptContextBuilds;

    public FunctionContractResolverTests()
    {
        _resolver = new FunctionContractResolver(
            new RuleBasedSelectionResolver(_conditionService),
            NullLogger<FunctionContractResolver>.Instance);
    }

    [Theory]
    [InlineData(FunctionContractSlot.InputSchema)]
    [InlineData(FunctionContractSlot.OutputSchema)]
    [InlineData(FunctionContractSlot.InputView)]
    [InlineData(FunctionContractSlot.OutputView)]
    public async Task UndeclaredSlot_ResolvesToNoContract(FunctionContractSlot slot)
    {
        var function = FunctionTestFactory.FromJson(FunctionTestFactory.Attributes());

        var result = await _resolver.ResolveAsync(function, slot, Lazy());

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
        _scriptContextBuilds.ShouldBe(0);
    }

    [Fact]
    public async Task SingleReference_WinsWithoutEvaluatingAnything()
    {
        var function = Function($$""" "inputView": {{Ref("v1", "sys-views")}} """);

        var result = await _resolver.ResolveAsync(function, FunctionContractSlot.InputView, Lazy());

        result.Value.ShouldNotBeNull();
        result.Value.Reference.Key.ShouldBe("v1");
        result.Value.MatchedByRule.ShouldBeFalse();
        _scriptContextBuilds.ShouldBe(0);
        await _conditionService.DidNotReceiveWithAnyArgs()
            .ExecuteConditionAsync(default!, default!, default);
    }

    [Fact]
    public async Task FirstMatchingRuleWins_AndLaterEntriesAreNotEvaluated()
    {
        var function = Function($$"""
            "inputView": [
                { "rule": {{Rule()}}, "view": {{Ref("v1", "sys-views")}} },
                { "rule": {{Rule()}}, "view": {{Ref("v2", "sys-views")}} },
                { "view": {{Ref("v3", "sys-views")}} }
            ]
            """);
        Condition(true);

        var result = await _resolver.ResolveAsync(function, FunctionContractSlot.InputView, Lazy());

        result.Value!.Reference.Key.ShouldBe("v1");
        result.Value.MatchedByRule.ShouldBeTrue();
        await _conditionService.Received(1)
            .ExecuteConditionAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RuleLessEntry_ActsAsTheFallback()
    {
        var function = Function($$"""
            "inputView": [
                { "rule": {{Rule()}}, "view": {{Ref("v1", "sys-views")}} },
                { "view": {{Ref("v2", "sys-views")}} }
            ]
            """);
        Condition(false);

        var result = await _resolver.ResolveAsync(function, FunctionContractSlot.InputView, Lazy());

        result.Value!.Reference.Key.ShouldBe("v2");
        result.Value.MatchedByRule.ShouldBeFalse();
    }

    [Fact]
    public async Task NoRuleMatches_AndNoFallback_ResolvesToNoContract()
    {
        var function = Function($$"""
            "inputView": [
                { "rule": {{Rule()}}, "view": {{Ref("v1", "sys-views")}} },
                { "rule": {{Rule()}}, "view": {{Ref("v2", "sys-views")}} }
            ]
            """);
        Condition(false);

        var result = await _resolver.ResolveAsync(function, FunctionContractSlot.InputView, Lazy());

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
    }

    [Fact]
    public async Task FailedRule_IsSkipped_NotFatal()
    {
        var function = Function($$"""
            "inputSchema": [
                { "rule": {{Rule()}}, "schema": {{Ref("s1", "sys-schemas")}} },
                { "schema": {{Ref("s2", "sys-schemas")}} }
            ]
            """);
        _conditionService
            .ExecuteConditionAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Fail(Error.Failure("script.boom", "compile error")));

        var result = await _resolver.ResolveAsync(function, FunctionContractSlot.InputSchema, Lazy());

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Reference.Key.ShouldBe("s2");
    }

    [Fact]
    public async Task ScriptContextBuildFailure_Fails()
    {
        var function = Function($$"""
            "inputView": [ { "rule": {{Rule()}}, "view": {{Ref("v1", "sys-views")}} } ]
            """);
        var failing = new LazyScriptContext(_ => throw new InvalidOperationException("no instance"));

        var result = await _resolver.ResolveAsync(function, FunctionContractSlot.InputView, failing);

        result.IsSuccess.ShouldBeFalse();
        await _conditionService.DidNotReceiveWithAnyArgs()
            .ExecuteConditionAsync(default!, default!, default);
    }

    /// <summary>
    /// Caller cancellation must propagate, not be reported as an application failure: a client that
    /// hung up has not hit an error, and converting it would log noise and hide the real outcome.
    /// </summary>
    [Fact]
    public async Task CallerCancellation_PropagatesInsteadOfBecomingAFailure()
    {
        var function = Function($$"""
            "inputView": [ { "rule": {{Rule()}}, "view": {{Ref("v1", "sys-views")}} } ]
            """);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var lazy = new LazyScriptContext(ct =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ScriptContext(NullLogger<ScriptContext>.Instance));
        });

        await Should.ThrowAsync<OperationCanceledException>(() =>
            _resolver.ResolveAsync(function, FunctionContractSlot.InputView, lazy, cts.Token));
    }

    /// <summary>
    /// An <c>OperationCanceledException</c> raised while the caller's token is still live — an internal
    /// timeout, for instance — is a genuine failure and must stay on the Result railway rather than
    /// masquerading as caller cancellation.
    /// </summary>
    [Fact]
    public async Task InternalCancellation_WithALiveCallerToken_StaysAFailure()
    {
        var function = Function($$"""
            "inputView": [ { "rule": {{Rule()}}, "view": {{Ref("v1", "sys-views")}} } ]
            """);
        var lazy = new LazyScriptContext(_ =>
            throw new OperationCanceledException("inner timeout"));

        var result = await _resolver.ResolveAsync(
            function, FunctionContractSlot.InputView, lazy, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task ScriptContextIsBuiltOnce_AcrossAllFourSlots()
    {
        var function = Function($$"""
            "inputSchema":  [ { "rule": {{Rule()}}, "schema": {{Ref("s1", "sys-schemas")}} } ],
            "outputSchema": [ { "rule": {{Rule()}}, "schema": {{Ref("s2", "sys-schemas")}} } ],
            "inputView":    [ { "rule": {{Rule()}}, "view":   {{Ref("v1", "sys-views")}} } ],
            "outputView":   [ { "rule": {{Rule()}}, "view":   {{Ref("v2", "sys-views")}} } ]
            """);
        Condition(true);
        var lazy = Lazy();

        foreach (var slot in Enum.GetValues<FunctionContractSlot>())
            (await _resolver.ResolveAsync(function, slot, lazy)).IsSuccess.ShouldBeTrue();

        _scriptContextBuilds.ShouldBe(1);
    }

    [Fact]
    public async Task ViewSlot_CarriesLoadDataFromTheWinningEntry()
    {
        var function = Function($$"""
            "outputView": [ { "view": {{Ref("v1", "sys-views")}}, "loadData": true } ]
            """);

        var result = await _resolver.ResolveAsync(function, FunctionContractSlot.OutputView, Lazy());

        result.Value!.LoadData.ShouldBe(true);
    }

    [Fact]
    public async Task SchemaSlot_NeverCarriesLoadData()
    {
        var function = Function($$""" "inputSchema": {{Ref("s1", "sys-schemas")}} """);

        var result = await _resolver.ResolveAsync(function, FunctionContractSlot.InputSchema, Lazy());

        result.Value!.LoadData.ShouldBeNull();
    }

    private void Condition(bool matches) =>
        _conditionService
            .ExecuteConditionAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Ok(matches));

    private LazyScriptContext Lazy() => new(_ =>
    {
        _scriptContextBuilds++;
        return Task.FromResult(new ScriptContext(NullLogger<ScriptContext>.Instance));
    });

    private static Function Function(string slots) =>
        FunctionTestFactory.FromJson(FunctionTestFactory.Attributes(slots));

    private static string Ref(string key, string flow) => FunctionTestFactory.Ref(key, flow);

    private static string Rule() => FunctionTestFactory.Rule("true");
}
