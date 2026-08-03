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
/// Unit tests for SchemaFieldVisibilityService (field-level <c>x-roles</c> visibility by caller roles).
/// Evaluation runs through the shared <see cref="IRoleGrantEvaluator"/>, so these tests also pin that
/// <c>x-roles</c> honors predefined and dynamic grants and that DENY wins across the whole grant set.
/// </summary>
public sealed class SchemaFieldVisibilityServiceTests
{
    private readonly ICurrentUser _currentUser;
    private readonly IInstanceTransitionRepository _repo;
    private readonly TransitionAuthorizationManager _manager;

    public SchemaFieldVisibilityServiceTests()
    {
        _currentUser = Substitute.For<ICurrentUser>();
        _repo = Substitute.For<IInstanceTransitionRepository>();
        _repo.GetLastCompletedManualTransitionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
             .Returns((InstanceTransition?)null);
        _manager = new TransitionAuthorizationManager(_currentUser, _repo);
    }

    private static RoleGrant Grant(string role, string grant) =>
        JsonSerializer.Deserialize<RoleGrant>($@"{{""Role"":""{role}"",""Grant"":""{grant}""}}")!;

    /// <summary>Evaluator with no instance: only static grants can resolve.</summary>
    private Task<IRoleGrantEvaluator> StaticEvaluator() =>
        _manager.CreateEvaluatorAsync(null, null, null, [], CancellationToken.None);

    private Task<IRoleGrantEvaluator> EvaluatorFor(
        Instance instance,
        IEnumerable<RoleGrant> grants,
        AuthorizationRequestContext? requestContext = null) =>
        _manager.CreateEvaluatorAsync(instance, null, requestContext, grants, CancellationToken.None);

    // ── Static grants (unchanged semantics) ──────────────────────────────────────

    [Fact]
    public async Task GetVisiblePaths_WhenNoPathGrants_ReturnsEmpty()
    {
        var pathGrants = new Dictionary<string, IReadOnlyList<RoleGrant>>();
        var visible = SchemaFieldVisibilityService.GetVisiblePaths(
            pathGrants, new[] { "maker" }, await StaticEvaluator());
        visible.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetVisiblePaths_WhenCallerRoleAllowed_IncludesPath()
    {
        var pathGrants = new Dictionary<string, IReadOnlyList<RoleGrant>>
        {
            ["amount"] = new List<RoleGrant> { Grant("morph-idm.maker", "allow") }
        };
        var visible = SchemaFieldVisibilityService.GetVisiblePaths(
            pathGrants, new[] { "morph-idm.maker" }, await StaticEvaluator());
        visible.Count.ShouldBe(1);
        visible.ShouldContain("amount");
    }

    [Fact]
    public async Task GetVisiblePaths_WhenCallerRoleDenied_ExcludesPath()
    {
        var pathGrants = new Dictionary<string, IReadOnlyList<RoleGrant>>
        {
            ["amount"] = new List<RoleGrant>
            {
                Grant("morph-idm.maker", "allow"),
                Grant("morph-idm.maker", "deny")
            }
        };
        var visible = SchemaFieldVisibilityService.GetVisiblePaths(
            pathGrants, new[] { "morph-idm.maker" }, await StaticEvaluator());
        visible.Count.ShouldBe(0);
    }

    [Fact]
    public async Task GetVisiblePaths_WhenMultipleRoles_AnyAllowYieldsVisible()
    {
        var pathGrants = new Dictionary<string, IReadOnlyList<RoleGrant>>
        {
            ["internalNotes"] = new List<RoleGrant> { Grant("morph-idm.approver", "allow") }
        };
        var visible = SchemaFieldVisibilityService.GetVisiblePaths(
            pathGrants, new[] { "morph-idm.maker", "morph-idm.approver" }, await StaticEvaluator());
        visible.ShouldContain("internalNotes");
    }

    [Fact]
    public async Task GetVisiblePaths_WhenCallerRolesNull_AllowlistStaysHidden()
    {
        var pathGrants = new Dictionary<string, IReadOnlyList<RoleGrant>>
        {
            ["amount"] = new List<RoleGrant> { Grant("maker", "allow") }
        };
        var visible = SchemaFieldVisibilityService.GetVisiblePaths(
            pathGrants, null, await StaticEvaluator());
        visible.Count.ShouldBe(0);
    }

    [Fact]
    public async Task IsPathVisibleForCaller_WhenNoGrants_ReturnsTrue()
    {
        SchemaFieldVisibilityService
            .IsPathVisibleForCaller(new List<RoleGrant>(), new[] { "any" }, await StaticEvaluator())
            .ShouldBeTrue();
    }

    [Fact]
    public async Task IsPathVisibleForCaller_WhenNoCallerRoles_AllowlistStaysHidden()
    {
        var grants = new List<RoleGrant> { Grant("maker", "allow") };
        var evaluator = await StaticEvaluator();
        SchemaFieldVisibilityService.IsPathVisibleForCaller(grants, null, evaluator).ShouldBeFalse();
        SchemaFieldVisibilityService.IsPathVisibleForCaller(grants, Array.Empty<string>(), evaluator).ShouldBeFalse();
    }

    [Fact]
    public async Task IsPathVisibleForCaller_WhenNoCallerRolesAndDenyOnlySet_IsVisible()
    {
        // Canonical blacklist rule: a deny-only set allows anyone it does not name, including a
        // role-less caller. Previously the role-less caller was rejected before the rule applied.
        var grants = new List<RoleGrant> { Grant("blocked", "deny") };
        SchemaFieldVisibilityService
            .IsPathVisibleForCaller(grants, null, await StaticEvaluator())
            .ShouldBeTrue();
    }

    // ── Predefined grants resolve on the grant side ──────────────────────────────

    [Fact]
    public async Task PredefinedGrant_IsVisibleToInstanceStarter()
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");
        instance.CreatedBy = "actor-alice";
        _currentUser.ActorUserName.Returns("actor-alice");

        var grants = new List<RoleGrant> { Grant(PredefinedInstanceRoles.InstanceStarter, "allow") };

        SchemaFieldVisibilityService
            .IsPathVisibleForCaller(grants, new[] { "teller" }, await EvaluatorFor(instance, grants))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task PredefinedGrant_IsHiddenFromNonStarter()
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");
        instance.CreatedBy = "actor-alice";
        _currentUser.ActorUserName.Returns("actor-bob");

        var grants = new List<RoleGrant> { Grant(PredefinedInstanceRoles.InstanceStarter, "allow") };

        SchemaFieldVisibilityService
            .IsPathVisibleForCaller(grants, new[] { "teller" }, await EvaluatorFor(instance, grants))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task PredefinedDeny_WinsEvenWhenCallerHoldsAnotherRole()
    {
        // A DENY on a predefined role must hide the field from the matching user regardless of what
        // other roles they hold. Evaluating per caller role in isolation used to let the blacklist
        // fallback re-open the field for any unrelated role the caller happened to have.
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");
        instance.CreatedBy = "actor-alice";
        _currentUser.ActorUserName.Returns("actor-alice");

        var grants = new List<RoleGrant> { Grant(PredefinedInstanceRoles.InstanceStarter, "deny") };

        SchemaFieldVisibilityService
            .IsPathVisibleForCaller(grants, new[] { "teller" }, await EvaluatorFor(instance, grants))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task BehalfOfPredefinedGrant_ResolvesViaUserName()
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");
        instance.CreatedByBehalfOf = "behalf-alice";
        _currentUser.ActorUserName.Returns("actor-bob");
        _currentUser.UserName.Returns("behalf-alice");

        var grants = new List<RoleGrant> { Grant(PredefinedInstanceRoles.InstanceBehalfOfStarter, "allow") };

        SchemaFieldVisibilityService
            .IsPathVisibleForCaller(grants, new[] { "teller" }, await EvaluatorFor(instance, grants))
            .ShouldBeTrue();
    }

    // ── Dynamic grants ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DynamicGrant_ResolvesAgainstRequestContext()
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");
        var requestContext = new AuthorizationRequestContext(
            Headers: new Dictionary<string, string?> { ["x-branch"] = "hq" });

        var grants = new List<RoleGrant> { Grant("$role.$.context.Headers.x-branch", "allow") };

        SchemaFieldVisibilityService
            .IsPathVisibleForCaller(grants, new[] { "hq" }, await EvaluatorFor(instance, grants, requestContext))
            .ShouldBeTrue();

        SchemaFieldVisibilityService
            .IsPathVisibleForCaller(grants, new[] { "branch-42" }, await EvaluatorFor(instance, grants, requestContext))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task DynamicGrant_ResolvesAgainstCurrentUser()
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");
        instance.CreatedBy = "actor-alice";
        _currentUser.ActorUserName.Returns("actor-alice");

        var grants = new List<RoleGrant> { Grant("$user.$.context.Instance.CreatedBy", "allow") };

        SchemaFieldVisibilityService
            .IsPathVisibleForCaller(grants, new[] { "teller" }, await EvaluatorFor(instance, grants))
            .ShouldBeTrue();
    }

    // ── Batching across many guarded paths ───────────────────────────────────────

    [Fact]
    public async Task GetVisiblePaths_OverManyGuardedPaths_PaysOneRepositoryFetch()
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");
        _currentUser.ActorUserName.Returns("actor-alice");

        var pathGrants = new Dictionary<string, IReadOnlyList<RoleGrant>>();
        for (var i = 0; i < 40; i++)
            pathGrants[$"field{i}"] = new List<RoleGrant> { Grant(PredefinedInstanceRoles.PreviousUser, "allow") };

        var allGrants = new List<RoleGrant>();
        foreach (var grants in pathGrants.Values)
            allGrants.AddRange(grants);

        var evaluator = await EvaluatorFor(instance, allGrants);
        SchemaFieldVisibilityService.GetVisiblePaths(pathGrants, new[] { "teller" }, evaluator);

        await _repo.Received(1).GetLastCompletedManualTransitionAsync(instance.Id, Arg.Any<CancellationToken>());
    }
}
