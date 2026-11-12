using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Contracts;
using BBT.Workflow.Definitions;
using BBT.Workflow.Functions;
using BBT.Workflow.Functions.Contracts;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Contracts;

/// <summary>
/// Unit tests for <see cref="ContractEntrySelector"/>: declaration order, first match wins, a rule-less
/// entry short-circuits without building the script context, and a throwing rule is skipped and reported.
/// </summary>
public sealed class ContractEntrySelectorTests
{
    private readonly ITaskConditionService _conditionService = Substitute.For<ITaskConditionService>();
    private readonly ContractEntrySelector _selector;
    private int _builds;

    public ContractEntrySelectorTests() => _selector = new ContractEntrySelector(_conditionService);

    [Fact]
    public async Task EmptyCandidates_ReturnsNull()
    {
        var result = await _selector.SelectAsync([], Lazy(), null);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
        _builds.ShouldBe(0);
    }

    [Fact]
    public async Task FirstMatchingRuleWins()
    {
        var a = Candidate("a", withRule: true);
        var b = Candidate("b", withRule: true);
        var c = Candidate("c", withRule: false);
        _conditionService
            .ExecuteConditionAsync(a.Rule!, Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Ok(false));
        _conditionService
            .ExecuteConditionAsync(b.Rule!, Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Ok(true));

        var result = await _selector.SelectAsync([a, b, c], Lazy(), null);

        result.Value!.Reference.Key.ShouldBe("b");
        result.Value.MatchedByRule.ShouldBeTrue();
    }

    [Fact]
    public async Task RuleLessEntry_WinsWithoutBuildingContext()
    {
        var lazy = Lazy();

        var result = await _selector.SelectAsync([Candidate("fallback", withRule: false)], lazy, null);

        result.Value!.Reference.Key.ShouldBe("fallback");
        result.Value.MatchedByRule.ShouldBeFalse();
        lazy.IsMaterialized.ShouldBeFalse();
        _builds.ShouldBe(0);
    }

    [Fact]
    public async Task ThrowingRule_IsSkipped_AndReported()
    {
        var a = Candidate("a", withRule: true);
        var b = Candidate("b", withRule: false);
        _conditionService
            .ExecuteConditionAsync(a.Rule!, Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Fail(Error.Failure("script.boom", "compile error")));
        var reported = new List<(ContractCandidate Candidate, string Message)>();

        var result = await _selector.SelectAsync(
            [a, b], Lazy(), (candidate, message) => reported.Add((candidate, message)));

        result.Value!.Reference.Key.ShouldBe("b");
        reported.Count.ShouldBe(1);
        reported[0].Candidate.ShouldBeSameAs(a);
        reported[0].Message.ShouldBe("compile error");
    }

    [Fact]
    public async Task NoMatch_NoFallback_ReturnsNull()
    {
        var a = Candidate("a", withRule: true);
        _conditionService
            .ExecuteConditionAsync(a.Rule!, Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Ok(false));

        var result = await _selector.SelectAsync([a], Lazy(), null);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
    }

    [Fact]
    public async Task ContextBuildFailure_Fails()
    {
        var failing = new LazyScriptContext(_ => throw new InvalidOperationException("no instance"));

        var result = await _selector.SelectAsync([Candidate("a", withRule: true)], failing, null);

        result.IsSuccess.ShouldBeFalse();
    }

    private LazyScriptContext Lazy() => new(_ =>
    {
        _builds++;
        return Task.FromResult(new ScriptContext(NullLogger<ScriptContext>.Instance));
    });

    private static ContractCandidate Candidate(string key, bool withRule) =>
        new(withRule ? NewRule(key) : null, new Reference(key, "d", "sys-schemas", "1.0.0"), null);

    // Distinct instances per key so NSubstitute can tell the rules apart.
    private static ScriptCode NewRule(string key) =>
        JsonSerializer.Deserialize<ScriptCode>($$"""{ "location": "{{key}}", "code": "true", "encoding": "NAT" }""")!;
}
