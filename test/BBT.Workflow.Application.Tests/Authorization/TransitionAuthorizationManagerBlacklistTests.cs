using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Users;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Unit tests for deny-only (blacklist) grant semantics: a grant set with no ALLOW grant allows
/// any caller that is not explicitly denied, while a set containing at least one ALLOW grant keeps
/// strict allowlist (default-deny) behavior.
/// </summary>
public sealed class TransitionAuthorizationManagerBlacklistTests
{
    private readonly ICurrentUser _currentUser;
    private readonly IInstanceTransitionRepository _repo;
    private readonly TransitionAuthorizationManager _sut;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public TransitionAuthorizationManagerBlacklistTests()
    {
        _currentUser = Substitute.For<ICurrentUser>();
        _repo = Substitute.For<IInstanceTransitionRepository>();
        _repo.GetLastCompletedManualTransitionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
             .Returns((InstanceTransition?)null);
        _sut = new TransitionAuthorizationManager(_currentUser, _repo);
    }

    private static List<RoleGrant> Grants(string json) =>
        JsonSerializer.Deserialize<List<RoleGrant>>(json, JsonOptions)!;

    private static Instance NewInstance() => Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");

    // ── EvaluateRolesStatic (no-instance / static path) ──────────────────────────

    [Fact]
    public void Static_DenyOnly_AllowsCallerNotDenied()
    {
        var grants = Grants("""[{"role":"blocked","grant":"deny"}]""");
        TransitionAuthorizationManager.EvaluateRolesStatic("someone-else", grants).ShouldBeTrue();
    }

    [Fact]
    public void Static_DenyOnly_DeniesMatchingCaller()
    {
        var grants = Grants("""[{"role":"blocked","grant":"deny"}]""");
        TransitionAuthorizationManager.EvaluateRolesStatic("blocked", grants).ShouldBeFalse();
    }

    [Fact]
    public void Static_Allowlist_DeniesNonMatchingCaller()
    {
        // At least one ALLOW grant → strict allowlist, default deny preserved.
        var grants = Grants("""[{"role":"maker","grant":"allow"}]""");
        TransitionAuthorizationManager.EvaluateRolesStatic("someone-else", grants).ShouldBeFalse();
    }

    [Fact]
    public void Static_MixedAllowDeny_DenyStillWins()
    {
        var grants = Grants("""[{"role":"maker","grant":"allow"},{"role":"maker","grant":"deny"}]""");
        TransitionAuthorizationManager.EvaluateRolesStatic("maker", grants).ShouldBeFalse();
    }

    [Fact]
    public void Static_DenyOnly_StrictFlag_DeniesNonMatchingCaller()
    {
        // defaultAllowWhenNoAllowGrant:false forces strict allowlist — backs the whole-list gating
        // in the query-filter path (deny-only subset must not auto-pass when the full list has an ALLOW).
        var grants = Grants("""[{"role":"blocked","grant":"deny"}]""");
        TransitionAuthorizationManager.EvaluateRolesStatic("someone-else", grants, defaultAllowWhenNoAllowGrant: false)
            .ShouldBeFalse();
    }

    // ── Instance path (EvaluateRolesWithPredefinedAsync via IsRoleAllowedForGrantsAsync) ──

    [Fact]
    public async Task Instance_DenyOnly_AllowsCallerNotDenied()
    {
        var grants = Grants("""[{"role":"blocked","grant":"deny"}]""");
        (await _sut.IsRoleAllowedForGrantsAsync("someone-else", grants, NewInstance())).ShouldBeTrue();
    }

    [Fact]
    public async Task Instance_DenyOnly_DeniesMatchingCaller()
    {
        var grants = Grants("""[{"role":"blocked","grant":"deny"}]""");
        (await _sut.IsRoleAllowedForGrantsAsync("blocked", grants, NewInstance())).ShouldBeFalse();
    }

    [Fact]
    public async Task Instance_Allowlist_DeniesNonMatchingCaller()
    {
        var grants = Grants("""[{"role":"maker","grant":"allow"}]""");
        (await _sut.IsRoleAllowedForGrantsAsync("someone-else", grants, NewInstance())).ShouldBeFalse();
    }

    // ── Predefined-only grant sets (blacklist semantics via the shared evaluator) ─

    [Fact]
    public async Task Predefined_DenyOnly_AllowsCallerNotDenied()
    {
        _currentUser.ActorUserName.Returns("actor");
        var instance = NewInstance(); // CreatedBy is not "actor" → $InstanceStarter deny does not match
        var grants = Grants($$"""[{"role":"{{PredefinedInstanceRoles.InstanceStarter}}","grant":"deny"}]""");
        (await _sut.IsRoleAllowedForGrantsAsync("teller", grants, instance)).ShouldBeTrue();
    }

    [Fact]
    public async Task Predefined_DenyOnly_DeniesMatchingStarter()
    {
        var instance = NewInstance();
        instance.CreatedBy = "actor";
        _currentUser.ActorUserName.Returns("actor");
        var grants = Grants($$"""[{"role":"{{PredefinedInstanceRoles.InstanceStarter}}","grant":"deny"}]""");
        (await _sut.IsRoleAllowedForGrantsAsync("teller", grants, instance)).ShouldBeFalse();
    }
}
