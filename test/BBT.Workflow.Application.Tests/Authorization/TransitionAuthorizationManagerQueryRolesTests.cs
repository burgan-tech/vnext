using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Uow;
using BBT.Aether.Users;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Unit tests for <see cref="TransitionAuthorizationManager.IsQueryAllowedAsync"/> — the shared queryRoles
/// gate used by the state/data/view/schema instance functions. Verifies state→workflow precedence,
/// empty-grants→allow, deny when no role matches, and multi-role any-allow.
/// </summary>
public sealed class TransitionAuthorizationManagerQueryRolesTests : IDisposable
{
    private readonly ICurrentUser _currentUser;
    private readonly IInstanceTransitionRepository _repo;
    private readonly TransitionAuthorizationManager _sut;
    private readonly IServiceProvider? _previousAmbientServiceProvider;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public TransitionAuthorizationManagerQueryRolesTests()
    {
        _currentUser = Substitute.For<ICurrentUser>();
        _repo = Substitute.For<IInstanceTransitionRepository>();
        _repo.GetLastCompletedManualTransitionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
             .Returns((InstanceTransition?)null);
        _sut = new TransitionAuthorizationManager(_currentUser, _repo);

        var mockUoW = Substitute.For<IUnitOfWork>();
        var mockUoWManager = Substitute.For<IUnitOfWorkManager>();
        mockUoWManager.BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockUoW));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(mockUoWManager);
        services.AddSingleton(Substitute.For<BBT.Workflow.Caching.IComponentCacheStore>());
        _previousAmbientServiceProvider = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = services.BuildServiceProvider();
    }

    public void Dispose() => AmbientServiceProvider.Current = _previousAmbientServiceProvider;

    private static WorkflowDefinition BuildWorkflow(string stateQueryRolesJson, string rootQueryRolesJson) =>
        JsonSerializer.Deserialize<WorkflowDefinition>($$"""
            {
              "type": "F",
              "timeout": null,
              "labels": [],
              "functions": [],
              "features": [],
              "states": [
                {
                  "key": "review",
                  "stateType": "intermediate",
                  "labels": [],
                  "transitions": [],
                  "queryRoles": {{stateQueryRolesJson}}
                }
              ],
              "sharedTransitions": [],
              "extensions": [],
              "queryRoles": {{rootQueryRolesJson}}
            }
            """, JsonOptions)!;

    /// <summary>
    /// An instance whose OWN current state is <c>review</c> — which is what the gate reads.
    /// </summary>
    /// <remarks>
    /// This fixture used to set only <c>EffectiveState</c>, because the gate used to read that. It is
    /// the deepest ACTIVE SUBFLOW's state key, so on any instance that has one it names a state of a
    /// different workflow — one this <c>workflow</c> cannot resolve. The gate then found no state,
    /// fell through to the workflow root's grants, and a state's own <c>queryRoles</c> (and a parent's
    /// stamped narrowing) silently stopped applying for as long as the child had a subflow of its own.
    /// <see cref="EffectiveStateElsewhere_DoesNotChangeTheAnswer"/> pins the distinction that the old
    /// fixture could not express.
    /// </remarks>
    private static Instance InReviewState()
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");
        instance.ChangeState(State.Create("review", StateType.Intermediate, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code));
        return instance;
    }

    [Fact]
    public async Task NoGrants_Allows()
    {
        var wf = BuildWorkflow("[]", "[]");
        (await _sut.IsQueryAllowedAsync(wf, InReviewState(), new[] { "anyone" })).ShouldBeTrue();
    }

    [Fact]
    public async Task NoGrants_NullRoles_Allows()
    {
        var wf = BuildWorkflow("[]", "[]");
        (await _sut.IsQueryAllowedAsync(wf, InReviewState(), null)).ShouldBeTrue();
    }

    [Fact]
    public async Task StateGrant_Allows_WhenCallerMatches()
    {
        var wf = BuildWorkflow("""[{"role":"backoffice","grant":"allow"}]""", "[]");
        (await _sut.IsQueryAllowedAsync(wf, InReviewState(), new[] { "backoffice" })).ShouldBeTrue();
    }

    [Fact]
    public async Task Denies_WhenNoCallerRoleMatches()
    {
        var wf = BuildWorkflow("""[{"role":"backoffice","grant":"allow"}]""", "[]");
        (await _sut.IsQueryAllowedAsync(wf, InReviewState(), new[] { "customer" })).ShouldBeFalse();
    }

    [Fact]
    public async Task StateQueryRoles_OverrideWorkflowQueryRoles()
    {
        // Workflow root would allow "customer", but the state's own grants (override) only allow "backoffice".
        var wf = BuildWorkflow("""[{"role":"backoffice","grant":"allow"}]""", """[{"role":"customer","grant":"allow"}]""");
        (await _sut.IsQueryAllowedAsync(wf, InReviewState(), new[] { "customer" })).ShouldBeFalse();
    }

    /// <summary>
    /// The gate answers for the instance it was asked about, not for whatever is running beneath it.
    /// A mid-level instance parked in <c>review</c> while its own child sits in some other state must
    /// still be governed by <c>review</c>'s grants.
    /// </summary>
    [Fact]
    public async Task EffectiveStateElsewhere_DoesNotChangeTheAnswer()
    {
        var wf = BuildWorkflow("""[{"role":"backoffice","grant":"allow"}]""", """[{"role":"customer","grant":"allow"}]""");
        var instance = InReviewState();
        instance.SetEffectiveState("leaf-waiting");

        (await _sut.IsQueryAllowedAsync(wf, instance, new[] { "customer" })).ShouldBeFalse(
            "the root's grants must not take over just because a descendant is active");
        (await _sut.IsQueryAllowedAsync(wf, instance, new[] { "backoffice" })).ShouldBeTrue(
            "review's own grants still govern the instance sitting in review");
    }

    [Fact]
    public async Task WorkflowQueryRoles_UsedWhenStateHasNone()
    {
        var wf = BuildWorkflow("[]", """[{"role":"customer","grant":"allow"}]""");
        (await _sut.IsQueryAllowedAsync(wf, InReviewState(), new[] { "customer" })).ShouldBeTrue();
    }

    [Fact]
    public async Task MultiRole_AnyAllowed_Allows()
    {
        var wf = BuildWorkflow("""[{"role":"backoffice","grant":"allow"}]""", "[]");
        (await _sut.IsQueryAllowedAsync(wf, InReviewState(), new[] { "x", "backoffice" })).ShouldBeTrue();
    }

    // ── IsAnyRoleAllowedForGrantsAsync (custom function Roles) ──────────────────

    private static List<RoleGrant> Grants(string json) =>
        JsonSerializer.Deserialize<List<RoleGrant>>(json, JsonOptions)!;

    [Fact]
    public async Task AnyRole_EmptyGrants_Allows()
    {
        (await _sut.IsAnyRoleAllowedForGrantsAsync(new[] { "anyone" }, Grants("[]"), instance: null)).ShouldBeTrue();
    }

    [Fact]
    public async Task AnyRole_Allows_WhenAnyCallerRoleMatches()
    {
        var grants = Grants("""[{"role":"fn-runner","grant":"allow"}]""");
        (await _sut.IsAnyRoleAllowedForGrantsAsync(new[] { "x", "fn-runner" }, grants, instance: null)).ShouldBeTrue();
    }

    [Fact]
    public async Task AnyRole_Denies_WhenNoCallerRoleMatches()
    {
        var grants = Grants("""[{"role":"fn-runner","grant":"allow"}]""");
        (await _sut.IsAnyRoleAllowedForGrantsAsync(new[] { "customer" }, grants, instance: null)).ShouldBeFalse();
    }

    [Fact]
    public async Task AnyRole_NullCaller_DeniesStaticOnlyGrant()
    {
        // Null caller roles → only predefined/dynamic grants evaluated; a static grant cannot match.
        var grants = Grants("""[{"role":"fn-runner","grant":"allow"}]""");
        (await _sut.IsAnyRoleAllowedForGrantsAsync(null, grants, instance: null)).ShouldBeFalse();
    }
}
