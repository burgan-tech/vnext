using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Functions.Contracts;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Caller;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Contracts;

/// <summary>
/// <see cref="ContractEntrySelector"/> with rules that read the caller's roles: a provider that cannot
/// answer fails the selection (never a fallback), whatever the rule returned, threw or swallowed; a rule
/// that does not read roles never starts a resolution and keeps working while the provider is down.
/// </summary>
public sealed class ContractEntrySelectorCallerRolesTests
{
    private readonly ITaskConditionService _conditionService = Substitute.For<ITaskConditionService>();
    private readonly ContractEntrySelector _selector;
    private int _resolutions;

    public ContractEntrySelectorCallerRolesTests() => _selector = new ContractEntrySelector(_conditionService);

    [Fact]
    public async Task RoleRuleMatches_SelectsItsEntry_ResolvingOnce()
    {
        var caller = Caller(["morph.approver"]);
        var (a, b, fallback) = (Candidate("a", true), Candidate("b", true), Candidate("fallback", false));
        RuleReadsRole(a, "morph.maker");
        RuleReadsRole(b, "morph.approver");

        var result = await _selector.SelectAsync([a, b, fallback], Lazy(caller), null);

        result.Value!.Reference.Key.ShouldBe("b");
        _resolutions.ShouldBe(1);
    }

    [Fact]
    public async Task ProviderFailed_RuleReturnsFalse_FailsSelection_InsteadOfFallback()
    {
        var caller = FailingCaller();
        var a = Candidate("a", true);
        RuleReadsRole(a, "morph.approver", swallow: true);

        var result = await _selector.SelectAsync([a, Candidate("fallback", false)], Lazy(caller), null);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.CallerRolesUnavailable);
    }

    [Fact]
    public async Task ProviderFailed_RuleThrows_FailsSelection_AndIsNotReportedAsRuleError()
    {
        var caller = FailingCaller();
        var a = Candidate("a", true);
        RuleReadsRole(a, "morph.approver", swallow: false);
        var reported = new List<string>();

        var result = await _selector.SelectAsync(
            [a, Candidate("fallback", false)], Lazy(caller), (c, _) => reported.Add(c.Reference.Key));

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.CallerRolesUnavailable);
        reported.ShouldBeEmpty();
    }

    [Fact]
    public async Task ProviderFailed_RuleClaimsMatchAfterSwallowing_StillFails()
    {
        var caller = FailingCaller();
        var a = Candidate("a", true);
        _conditionService
            .ExecuteConditionAsync(a.Rule!, Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                try { await ci.Arg<ScriptContext>().Caller.RolesAsync(); }
                catch (CallerRolesUnavailableException) { /* a careless rule */ }
                return Result<bool>.Ok(true);
            });

        var result = await _selector.SelectAsync([a], Lazy(caller), null);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.CallerRolesUnavailable);
    }

    [Fact]
    public async Task ProviderDown_RuleNotReadingRoles_StillSelects_WithoutResolving()
    {
        var caller = FailingCaller();
        var a = Candidate("a", true);
        _conditionService
            .ExecuteConditionAsync(a.Rule!, Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Ok(true));

        var result = await _selector.SelectAsync([a], Lazy(caller), null);

        result.Value!.Reference.Key.ShouldBe("a");
        caller.ResolutionStarted.ShouldBeFalse();
        _resolutions.ShouldBe(0);
    }

    [Fact]
    public async Task OrdinaryRuleThrow_IsStillSkipped()
    {
        var caller = Caller(["x"]);
        var a = Candidate("a", true);
        _conditionService
            .ExecuteConditionAsync(a.Rule!, Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Fail(Error.Failure("script.boom", "compile error")));

        var result = await _selector.SelectAsync([a, Candidate("fallback", false)], Lazy(caller), null);

        result.Value!.Reference.Key.ShouldBe("fallback");
    }

    private void RuleReadsRole(ContractCandidate candidate, string role, bool swallow = false)
    {
        _conditionService
            .ExecuteConditionAsync(candidate.Rule!, Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                try
                {
                    return Result<bool>.Ok(await ci.Arg<ScriptContext>().Caller.HasRoleAsync(role));
                }
                catch (CallerRolesUnavailableException ex) when (swallow)
                {
                    _ = ex;
                    return Result<bool>.Ok(false);
                }
                catch (CallerRolesUnavailableException ex)
                {
                    // What ScriptConditionEvaluator turns a thrown exception into.
                    return Result<bool>.Fail(Error.Failure("script.error", ex.Message));
                }
            });
    }

    private CallerAccessor Caller(string[] roles) => new(() =>
    {
        _resolutions++;
        return Task.FromResult(new CallerRoleSnapshot(roles, Failed: false, FromHeader: false));
    });

    private CallerAccessor FailingCaller() => new(() =>
    {
        _resolutions++;
        return Task.FromResult(new CallerRoleSnapshot([], Failed: true, FromHeader: false));
    });

    private static LazyScriptContext Lazy(ICallerAccessor caller) => new(_ =>
        Task.FromResult(new ScriptContext.Builder(NullLogger<ScriptContext>.Instance).SetCaller(caller).Build()));

    private static ContractCandidate Candidate(string key, bool withRule) =>
        new(withRule ? NewRule(key) : null, new Reference(key, "d", "sys-schemas", "1.0.0"), null);

    private static ScriptCode NewRule(string key) =>
        JsonSerializer.Deserialize<ScriptCode>($$"""{ "location": "{{key}}", "code": "true", "encoding": "NAT" }""")!;
}
