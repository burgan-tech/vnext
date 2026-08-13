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
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Unit tests for the single role grant evaluation core reached via
/// <see cref="ITransitionAuthorizationManager.CreateEvaluatorAsync"/>.
/// <para>
/// Two properties are pinned here. First, <b>equivalence</b>: over a matrix of static-only grant sets
/// and caller roles the evaluator must agree with
/// <see cref="TransitionAuthorizationManager.EvaluateRolesStatic"/> — the two remaining decision paths
/// must never drift apart. Second, <b>batching</b>: an evaluator created once for a batch performs the
/// instance-bound fetch at most once no matter how many grant sets are then evaluated.
/// </para>
/// </summary>
public sealed class RoleGrantEvaluatorTests
{
    private readonly ICurrentUser _currentUser;
    private readonly IInstanceTransitionRepository _repo;
    private readonly TransitionAuthorizationManager _sut;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public RoleGrantEvaluatorTests()
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

    private static Transition BuildTransition(string key, string roleValue, string grant = "allow") =>
        JsonSerializer.Deserialize<Transition>($$"""
            {
              "key": "{{key}}",
              "from": null,
              "target": "state2",
              "triggerType": "Manual",
              "versionStrategy": "None",
              "labels": [],
              "onExecutionTasks": [],
              "roles": [{"role": "{{roleValue}}", "grant": "{{grant}}"}]
            }
            """, JsonOptions)!;

    // ── Equivalence with the static path ─────────────────────────────────────────

    /// <summary>
    /// Static-only grant sets covering every shape the canonical rule distinguishes: empty, allowlist,
    /// deny-only blacklist, and mixed sets where DENY must override a matching ALLOW.
    /// </summary>
    public static TheoryData<string, string?> StaticGrantMatrix()
    {
        var grantSets = new[]
        {
            "[]",
            """[{"role":"maker","grant":"allow"}]""",
            """[{"role":"blocked","grant":"deny"}]""",
            """[{"role":"maker","grant":"allow"},{"role":"blocked","grant":"deny"}]""",
            """[{"role":"maker","grant":"allow"},{"role":"maker","grant":"deny"}]""",
            """[{"role":"a","grant":"deny"},{"role":"b","grant":"deny"}]""",
            """[{"role":"maker","grant":"allow"},{"role":"checker","grant":"allow"}]""",
        };
        var roles = new string?[] { null, "", "maker", "MAKER", "blocked", "checker", "someone-else", " maker " };

        var data = new TheoryData<string, string?>();
        foreach (var grantSet in grantSets)
            foreach (var role in roles)
                data.Add(grantSet, role);
        return data;
    }

    [Theory]
    [MemberData(nameof(StaticGrantMatrix))]
    public async Task Evaluator_AgreesWithEvaluateRolesStatic_ForStaticOnlyGrants(string grantsJson, string? role)
    {
        var grants = Grants(grantsJson);
        var expected = TransitionAuthorizationManager.EvaluateRolesStatic(role, grants);

        // With an instance present the evaluator takes the predefined/dynamic-aware path; for static-only
        // grants it must still land on the same decision as the static path.
        var evaluator = await _sut.CreateEvaluatorAsync(
            NewInstance(), null, null, grants, CancellationToken.None);

        evaluator.IsRoleAllowed(role, grants).ShouldBe(expected);
    }

    [Theory]
    [MemberData(nameof(StaticGrantMatrix))]
    public async Task Evaluator_WithoutInstance_AgreesWithEvaluateRolesStatic(string grantsJson, string? role)
    {
        var grants = Grants(grantsJson);
        var expected = TransitionAuthorizationManager.EvaluateRolesStatic(role, grants);

        var evaluator = await _sut.CreateEvaluatorAsync(null, null, null, grants, CancellationToken.None);

        evaluator.IsRoleAllowed(role, grants).ShouldBe(expected);
    }

    // ── Canonical semantics ──────────────────────────────────────────────────────

    [Fact]
    public async Task EmptyGrantSet_IsAllowed()
    {
        var evaluator = await _sut.CreateEvaluatorAsync(NewInstance(), null, null, [], CancellationToken.None);
        evaluator.IsRoleAllowed("anyone", []).ShouldBeTrue();
        evaluator.IsAnyRoleAllowed(["anyone"], []).ShouldBeTrue();
    }

    [Fact]
    public async Task DenyWins_EvenWhenDeclaredAfterMatchingAllow()
    {
        var grants = Grants("""[{"role":"maker","grant":"allow"},{"role":"maker","grant":"deny"}]""");
        var evaluator = await _sut.CreateEvaluatorAsync(NewInstance(), null, null, grants, CancellationToken.None);
        evaluator.IsRoleAllowed("maker", grants).ShouldBeFalse();
    }

    [Fact]
    public async Task DenyOnlySet_IsBlacklist_AllowsUnmatchedCaller()
    {
        var grants = Grants("""[{"role":"blocked","grant":"deny"}]""");
        var evaluator = await _sut.CreateEvaluatorAsync(NewInstance(), null, null, grants, CancellationToken.None);
        evaluator.IsRoleAllowed("someone-else", grants).ShouldBeTrue();
    }

    [Fact]
    public async Task IsAnyRoleAllowed_AnyAllowedRoleGrantsAccess()
    {
        var grants = Grants("""[{"role":"checker","grant":"allow"}]""");
        var evaluator = await _sut.CreateEvaluatorAsync(NewInstance(), null, null, grants, CancellationToken.None);
        evaluator.IsAnyRoleAllowed(["maker", "checker"], grants).ShouldBeTrue();
        evaluator.IsAnyRoleAllowed(["maker", "viewer"], grants).ShouldBeFalse();
    }

    [Fact]
    public async Task IsAnyRoleAllowed_WithNoCallerRoles_StillEvaluatesPredefinedGrants()
    {
        var instance = NewInstance();
        instance.CreatedBy = "actor-alice";
        _currentUser.ActorUserName.Returns("actor-alice");

        var grants = Grants($$"""[{"role":"{{PredefinedInstanceRoles.InstanceStarter}}","grant":"allow"}]""");
        var evaluator = await _sut.CreateEvaluatorAsync(instance, null, null, grants, CancellationToken.None);

        evaluator.IsAnyRoleAllowed(null, grants).ShouldBeTrue();
        evaluator.IsAnyRoleAllowed([], grants).ShouldBeTrue();
    }

    // ── Predefined roles resolve against the documented identity field ───────────

    [Fact]
    public async Task ActorPredefinedRole_MatchesActorUserNameAgainstCreatedBy()
    {
        var instance = NewInstance();
        instance.CreatedBy = "actor-alice";
        _currentUser.ActorUserName.Returns("actor-alice");
        _currentUser.UserName.Returns("behalf-bob");

        var grants = Grants($$"""[{"role":"{{PredefinedInstanceRoles.InstanceStarter}}","grant":"allow"}]""");
        var evaluator = await _sut.CreateEvaluatorAsync(instance, null, null, grants, CancellationToken.None);

        evaluator.IsRoleAllowed(null, grants).ShouldBeTrue();
    }

    [Fact]
    public async Task BehalfOfPredefinedRole_MatchesUserNameAgainstCreatedByBehalfOf()
    {
        var instance = NewInstance();
        instance.CreatedByBehalfOf = "behalf-alice";
        // The actor is somebody else entirely: a behalf-of grant must not be satisfied by ActorUserName.
        _currentUser.ActorUserName.Returns("actor-bob");
        _currentUser.UserName.Returns("behalf-alice");

        var grants = Grants($$"""[{"role":"{{PredefinedInstanceRoles.InstanceBehalfOfStarter}}","grant":"allow"}]""");
        var evaluator = await _sut.CreateEvaluatorAsync(instance, null, null, grants, CancellationToken.None);

        evaluator.IsRoleAllowed(null, grants).ShouldBeTrue();
    }

    [Fact]
    public async Task BehalfOfPredefinedRole_IsNotSatisfiedByActorUserName()
    {
        var instance = NewInstance();
        instance.CreatedByBehalfOf = "actor-alice";
        _currentUser.ActorUserName.Returns("actor-alice");
        _currentUser.UserName.Returns("behalf-bob");

        var grants = Grants($$"""[{"role":"{{PredefinedInstanceRoles.InstanceBehalfOfStarter}}","grant":"allow"}]""");
        var evaluator = await _sut.CreateEvaluatorAsync(instance, null, null, grants, CancellationToken.None);

        evaluator.IsRoleAllowed(null, grants).ShouldBeFalse();
    }

    [Fact]
    public async Task PredefinedDenyOnlySet_AllowsCallerWithoutActorIdentity()
    {
        // No actor identity at all: the grant cannot match, and a deny-only set is a blacklist,
        // so the caller is allowed rather than denied outright.
        _currentUser.ActorUserName.Returns((string?)null);
        _currentUser.UserName.Returns((string?)null);

        var grants = Grants($$"""[{"role":"{{PredefinedInstanceRoles.InstanceStarter}}","grant":"deny"}]""");
        var evaluator = await _sut.CreateEvaluatorAsync(NewInstance(), null, null, grants, CancellationToken.None);

        evaluator.IsRoleAllowed("teller", grants).ShouldBeTrue();
    }

    // ── Dynamic roles ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DynamicRoleGrant_ResolvesAgainstRequestContextHeaders()
    {
        var requestContext = new AuthorizationRequestContext(
            Headers: new Dictionary<string, string?> { ["x-branch"] = "teller" });

        var grants = Grants("""[{"role":"$role.$.context.Headers.x-branch","grant":"allow"}]""");
        var evaluator = await _sut.CreateEvaluatorAsync(
            NewInstance(), null, requestContext, grants, CancellationToken.None);

        evaluator.IsRoleAllowed("teller", grants).ShouldBeTrue();
        evaluator.IsRoleAllowed("auditor", grants).ShouldBeFalse();
    }

    [Fact]
    public async Task DynamicRoleGrant_WithoutRequestContext_CannotMatch()
    {
        // Documents the failure mode Faz 2 closes at the call sites: an absent request context makes the
        // Headers namespace empty, so the grant is inert rather than denied for a specific reason.
        var grants = Grants("""[{"role":"$role.$.context.Headers.x-branch","grant":"allow"}]""");
        var evaluator = await _sut.CreateEvaluatorAsync(
            NewInstance(), null, null, grants, CancellationToken.None);

        evaluator.IsRoleAllowed("teller", grants).ShouldBeFalse();
    }

    [Fact]
    public async Task DynamicRoleGrant_SeesPerTransitionContext()
    {
        // The authorization context is memoized per transition key; two transitions evaluated through the
        // same evaluator must each see their own $.context.Transition.
        var grants = Grants("""[{"role":"$role.$.context.Transition.Key","grant":"allow"}]""");
        var evaluator = await _sut.CreateEvaluatorAsync(
            NewInstance(), null, null, grants, CancellationToken.None);

        var first = BuildTransition("approve", "$role.$.context.Transition.Key");
        var second = BuildTransition("reject", "$role.$.context.Transition.Key");

        evaluator.IsRoleAllowed("approve", grants, first).ShouldBeTrue();
        evaluator.IsRoleAllowed("approve", grants, second).ShouldBeFalse();
        evaluator.IsRoleAllowed("reject", grants, second).ShouldBeTrue();
    }

    // ── Batching: the instance-bound fetch is paid once ──────────────────────────

    [Fact]
    public async Task Evaluator_FetchesPreviousTransitionOnce_ForWholeBatch()
    {
        var instance = NewInstance();
        _currentUser.ActorUserName.Returns("actor-alice");

        var grants = Grants($$"""[{"role":"{{PredefinedInstanceRoles.PreviousUser}}","grant":"allow"}]""");
        var evaluator = await _sut.CreateEvaluatorAsync(instance, null, null, grants, CancellationToken.None);

        for (var i = 0; i < 25; i++)
            evaluator.IsRoleAllowed("teller", grants);

        await _repo.Received(1).GetLastCompletedManualTransitionAsync(instance.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Evaluator_SkipsPreviousTransitionFetch_WhenNoGrantReferencesIt()
    {
        var instance = NewInstance();
        var grants = Grants($$"""[{"role":"maker","grant":"allow"},{"role":"{{PredefinedInstanceRoles.InstanceStarter}}","grant":"allow"}]""");

        var evaluator = await _sut.CreateEvaluatorAsync(instance, null, null, grants, CancellationToken.None);
        evaluator.IsRoleAllowed("maker", grants);

        await _repo.DidNotReceive().GetLastCompletedManualTransitionAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FilterAuthorizedTransitionKeys_FetchesPreviousTransitionOnce()
    {
        var instance = NewInstance();
        _currentUser.ActorUserName.Returns("actor-alice");

        var workflow = JsonSerializer.Deserialize<WorkflowDefinition>($$"""
            {
              "type": "F",
              "timeout": null,
              "labels": [],
              "functions": [],
              "features": [],
              "states": [],
              "sharedTransitions": [
                {
                  "key": "t1", "from": "s1", "target": "s2", "triggerType": "Manual",
                  "versionStrategy": "None", "labels": [], "onExecutionTasks": [],
                  "roles": [{"role": "{{PredefinedInstanceRoles.PreviousUser}}", "grant": "allow"}]
                },
                {
                  "key": "t2", "from": "s1", "target": "s3", "triggerType": "Manual",
                  "versionStrategy": "None", "labels": [], "onExecutionTasks": [],
                  "roles": [{"role": "{{PredefinedInstanceRoles.PreviousUser}}", "grant": "allow"}]
                },
                {
                  "key": "t3", "from": "s1", "target": "s4", "triggerType": "Manual",
                  "versionStrategy": "None", "labels": [], "onExecutionTasks": [],
                  "roles": [{"role": "{{PredefinedInstanceRoles.PreviousUser}}", "grant": "allow"}]
                }
              ],
              "extensions": [],
              "queryRoles": []
            }
            """, JsonOptions)!;

        var state = JsonSerializer.Deserialize<State>("""
            {
              "key": "s1", "stateType": 2, "versionStrategy": "None",
              "labels": [], "transitions": [], "onEntries": [], "onExits": []
            }
            """, JsonOptions)!;

        var result = await _sut.FilterAuthorizedTransitionKeysAsync(
            workflow, state, instance, ["t1", "t2", "t3"], "teller",
            cancellationToken: CancellationToken.None);

        result.ShouldBeEmpty(); // no previous transition → $PreviousUser never matches
        await _repo.Received(1).GetLastCompletedManualTransitionAsync(instance.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsAnyRoleAllowedForGrants_FetchesPreviousTransitionOnce_AcrossAllCallerRoles()
    {
        var instance = NewInstance();
        _currentUser.ActorUserName.Returns("actor-alice");

        var grants = Grants($$"""[{"role":"{{PredefinedInstanceRoles.PreviousUser}}","grant":"allow"}]""");

        await _sut.IsAnyRoleAllowedForGrantsAsync(
            ["r1", "r2", "r3", "r4", "r5"], grants, instance, null, CancellationToken.None);

        await _repo.Received(1).GetLastCompletedManualTransitionAsync(instance.Id, Arg.Any<CancellationToken>());
    }
}
