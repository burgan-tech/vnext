using System;
using System.Collections.Generic;
using System.Linq;
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
/// Unit tests for per-state role narrowing via <c>availableIn</c>. The transition's own grants are the
/// global gate and a matching <c>availableIn</c> entry's grants narrow it for that state — both must
/// allow (AND). Covers the discovery surface (<see cref="ITransitionAuthorizationManager.FilterAuthorizedTransitionKeysAsync"/>)
/// and the authorize surface (<see cref="ITransitionAuthorizationManager.IsTransitionAllowedInStateAsync"/>),
/// which must agree.
/// </summary>
public sealed class TransitionAuthorizationManagerAvailableInTests
{
    private const string ReviewState = "review";
    private const string ApprovalState = "approval";

    private readonly ICurrentUser _currentUser;
    private readonly IInstanceTransitionRepository _repo;
    private readonly TransitionAuthorizationManager _sut;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public TransitionAuthorizationManagerAvailableInTests()
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

    private static State NewState(string key) =>
        State.Create(key, StateType.Intermediate, StateSubType.None, VersionStrategy.IncreasePatch.Code);

    /// <summary>
    /// Builds a workflow from component JSON with one shared transition carrying the given
    /// transition-level grants and an optional per-state narrowing on <see cref="ApprovalState"/>.
    /// Deserializing rather than constructing keeps the test honest about the authored shape — it
    /// exercises <see cref="AvailableInJsonConverter"/> on the way in.
    /// </summary>
    private static (WorkflowDefinition Workflow, Transition Transition) BuildWorkflow(
        string? transitionRolesJson,
        string? approvalStateRolesJson,
        bool restrictAvailableIn = true)
    {
        var approvalEntry = approvalStateRolesJson != null
            ? $$"""{"state":"{{ApprovalState}}","roles":{{approvalStateRolesJson}}}"""
            : $"\"{ApprovalState}\"";

        var availableIn = restrictAvailableIn
            ? $"""[ "{ReviewState}", {approvalEntry} ]"""
            : "[]";

        var json = $$"""
        {
            "type": "F",
            "labels": [{"label": "Test", "language": "en"}],
            "states": [
                { "key": "{{ReviewState}}", "stateType": 2, "versionStrategy": "Minor",
                  "labels": [{"label": "Review", "language": "en"}], "transitions": [] },
                { "key": "{{ApprovalState}}", "stateType": 2, "versionStrategy": "Minor",
                  "labels": [{"label": "Approval", "language": "en"}], "transitions": [] }
            ],
            "sharedTransitions": [
                {
                    "key": "escalate",
                    "target": "$self",
                    "versionStrategy": "Minor",
                    "triggerType": 0,
                    "labels": [{"label": "Escalate", "language": "en"}],
                    "availableIn": {{availableIn}},
                    "roles": {{transitionRolesJson ?? "[]"}}
                }
            ],
            "startTransition": {
                "key": "start", "target": "{{ReviewState}}", "versionStrategy": "Minor",
                "triggerType": 0, "labels": [{"label": "Start", "language": "en"}]
            }
        }
        """;

        var workflow = JsonSerializer.Deserialize<WorkflowDefinition>(
            json, JsonSerializerConstants.JsonOptions)!;
        workflow.SetReference(new Reference("flow", "domain", "sys-flows", "1.0.0"));

        return (workflow, workflow.FindSharedTransition("escalate")!);
    }

    private async Task<IReadOnlyList<string>> FilterAsync(WorkflowDefinition workflow, string stateKey, string? role) =>
        await _sut.FilterAuthorizedTransitionKeysAsync(
            workflow, NewState(stateKey), NewInstance(), ["escalate"], role,
            cancellationToken: CancellationToken.None);

    // ── AND semantics ───────────────────────────────────────────────────────────

    [Fact]
    public async Task StateNarrowing_DeniesCaller_AllowedAtTransitionLevel()
    {
        // Transition level allows "maker"; the approval state narrows to supervisors only.
        var (workflow, _) = BuildWorkflow(
            """[{"role":"maker","grant":"allow"}]""",
            """[{"role":"supervisor","grant":"allow"}]""");

        (await FilterAsync(workflow, ApprovalState, "maker")).ShouldBeEmpty();
        // The unrestricted state keeps transition-level semantics
        (await FilterAsync(workflow, ReviewState, "maker")).ShouldBe(["escalate"]);
    }

    [Fact]
    public async Task TransitionLevel_DeniesCaller_AllowedByStateNarrowing()
    {
        // The reverse direction: the state entry must not widen what the transition denies.
        var (workflow, _) = BuildWorkflow(
            """[{"role":"maker","grant":"allow"}]""",
            """[{"role":"supervisor","grant":"allow"}]""");

        (await FilterAsync(workflow, ApprovalState, "supervisor")).ShouldBeEmpty();
    }

    [Fact]
    public async Task BothLevelsAllow_IsAllowed()
    {
        var (workflow, _) = BuildWorkflow(
            """[{"role":"supervisor","grant":"allow"}]""",
            """[{"role":"supervisor","grant":"allow"}]""");

        (await FilterAsync(workflow, ApprovalState, "supervisor")).ShouldBe(["escalate"]);
    }

    [Fact]
    public async Task StateEntryWithoutRoles_LeavesTransitionLevelUnchanged()
    {
        // The legacy bare-string form must behave exactly as before.
        var (workflow, _) = BuildWorkflow("""[{"role":"maker","grant":"allow"}]""", approvalStateRolesJson: null);

        (await FilterAsync(workflow, ApprovalState, "maker")).ShouldBe(["escalate"]);
        (await FilterAsync(workflow, ApprovalState, "other")).ShouldBeEmpty();
    }

    [Fact]
    public async Task StateNarrowingDeny_WinsOverTransitionAllow()
    {
        // DENY-wins applies within the state-level set too.
        var (workflow, _) = BuildWorkflow(
            """[{"role":"maker","grant":"allow"}]""",
            """[{"role":"maker","grant":"deny"}]""");

        (await FilterAsync(workflow, ApprovalState, "maker")).ShouldBeEmpty();
    }

    [Fact]
    public async Task StateNarrowingOnly_AppliesWithNoTransitionLevelGrants()
    {
        var (workflow, _) = BuildWorkflow(transitionRolesJson: null,
            """[{"role":"supervisor","grant":"allow"}]""");

        (await FilterAsync(workflow, ApprovalState, "supervisor")).ShouldBe(["escalate"]);
        (await FilterAsync(workflow, ApprovalState, "maker")).ShouldBeEmpty();
        // Unrestricted state: no grants at either level → allowed
        (await FilterAsync(workflow, ReviewState, "maker")).ShouldBe(["escalate"]);
    }

    // ── prefetch hint ───────────────────────────────────────────────────────────

    [Fact]
    public async Task PrefetchHint_CoversStateLevelGrants()
    {
        // A $PreviousUser grant that lives only on the availableIn entry must still cause the previous
        // manual transition to be loaded, otherwise it could never match.
        var (workflow, _) = BuildWorkflow(transitionRolesJson: null,
            """[{"role":"$PreviousUser","grant":"allow"}]""");

        await FilterAsync(workflow, ApprovalState, "maker");

        await _repo.Received(1).GetLastCompletedManualTransitionAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // ── IsTransitionAllowedInStateAsync (authorize surface) ─────────────────────

    [Fact]
    public async Task AuthorizeSurface_DeniesWhenStateNotInAvailableIn()
    {
        var (workflow, transition) = BuildWorkflow(transitionRolesJson: null, approvalStateRolesJson: null);

        var allowed = await _sut.IsTransitionAllowedInStateAsync(
            workflow, transition, "some-other-state", NewInstance(), "maker");

        allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task AuthorizeSurface_AgreesWithDiscoverySurface()
    {
        var (workflow, transition) = BuildWorkflow(
            """[{"role":"maker","grant":"allow"}]""",
            """[{"role":"supervisor","grant":"allow"}]""");

        foreach (var (state, role) in new[]
                 {
                     (ApprovalState, "maker"), (ApprovalState, "supervisor"),
                     (ReviewState, "maker"), (ReviewState, "supervisor")
                 })
        {
            var discovery = (await FilterAsync(workflow, state, role)).Any();
            var authorize = await _sut.IsTransitionAllowedInStateAsync(
                workflow, transition, state, NewInstance(), role);

            authorize.ShouldBe(discovery, $"state '{state}', role '{role}'");
        }
    }

    [Fact]
    public async Task AuthorizeSurface_WithoutState_FallsBackToTransitionLevelOnly()
    {
        // Workflow-scoped authorize has no state in scope: the state gate and narrowing are skipped.
        var (workflow, transition) = BuildWorkflow(
            """[{"role":"maker","grant":"allow"}]""",
            """[{"role":"supervisor","grant":"allow"}]""");

        (await _sut.IsTransitionAllowedInStateAsync(workflow, transition, null, instance: null, "maker"))
            .ShouldBeTrue();
        (await _sut.IsTransitionAllowedInStateAsync(workflow, transition, null, instance: null, "nobody"))
            .ShouldBeFalse();
    }
}
