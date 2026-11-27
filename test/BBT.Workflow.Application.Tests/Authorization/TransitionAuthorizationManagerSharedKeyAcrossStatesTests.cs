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
/// A transition key may be declared on several states of one workflow, each with its own grants. The
/// grants that decide a call are the ones of the state the instance is in — not the first declaration
/// in definition order (#1334). Covers the lookup, the discovery filter and the authorize decision.
/// </summary>
public sealed class TransitionAuthorizationManagerSharedKeyAcrossStatesTests
{
    private const string RestrictedState = "entry-restricted";
    private const string OpenState = "entry-open";
    private const string Key = "code-timeout";

    private readonly TransitionAuthorizationManager _sut;

    public TransitionAuthorizationManagerSharedKeyAcrossStatesTests()
    {
        var repo = Substitute.For<IInstanceTransitionRepository>();
        repo.GetLastCompletedManualTransitionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((InstanceTransition?)null);
        _sut = new TransitionAuthorizationManager(Substitute.For<ICurrentUser>(), repo);
    }

    /// <summary>The restricted state is declared FIRST, so a first-match lookup always lands on it.</summary>
    private static WorkflowDefinition BuildWorkflow()
    {
        var json = $$"""
        {
            "type": "F",
            "labels": [{"label": "Test", "language": "en"}],
            "states": [
                { "key": "{{RestrictedState}}", "stateType": 2, "versionStrategy": "Minor",
                  "labels": [{"label": "Restricted", "language": "en"}],
                  "transitions": [
                    { "key": "{{Key}}", "target": "$self", "versionStrategy": "Minor", "triggerType": 0,
                      "labels": [{"label": "Timeout", "language": "en"}],
                      "roles": [{"role":"supervisor","grant":"allow"}] }
                  ] },
                { "key": "{{OpenState}}", "stateType": 2, "versionStrategy": "Minor",
                  "labels": [{"label": "Open", "language": "en"}],
                  "transitions": [
                    { "key": "{{Key}}", "target": "$self", "versionStrategy": "Minor", "triggerType": 0,
                      "labels": [{"label": "Timeout", "language": "en"}] }
                  ] }
            ],
            "startTransition": {
                "key": "start", "target": "{{RestrictedState}}", "versionStrategy": "Minor",
                "triggerType": 0, "labels": [{"label": "Start", "language": "en"}]
            }
        }
        """;

        var workflow = JsonSerializer.Deserialize<WorkflowDefinition>(
            json, JsonSerializerConstants.JsonOptions)!;
        workflow.SetReference(new Reference("flow", "domain", "sys-flows", "1.0.0"));
        return workflow;
    }

    private static Instance NewInstance() => Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");

    [Fact]
    public void FindTransitionInState_ReturnsTheCurrentStatesOwnDeclaration()
    {
        var workflow = BuildWorkflow();

        workflow.FindTransitionInState(Key, OpenState)!.Roles.ShouldBeEmpty();
        workflow.FindTransitionInState(Key, RestrictedState)!.Roles.Count.ShouldBe(1);
    }

    [Fact]
    public void FindTransitionInState_FallsBackToTheStateBlindLookup()
    {
        var workflow = BuildWorkflow();

        // No state, and a state that does not exist, keep the previous answer (first declaration).
        workflow.FindTransitionInState(Key, null).ShouldBeSameAs(workflow.FindTransitionInContext(Key));
        workflow.FindTransitionInState(Key, "nope").ShouldBeSameAs(workflow.FindTransitionInContext(Key));
        workflow.FindTransitionInState("start", OpenState)!.Key.ShouldBe("start");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("viewer")]
    public async Task Authorize_InTheRolelessState_AllowsCallerWithoutTheRestrictedState_sRoles(string? role)
    {
        var workflow = BuildWorkflow();
        var transition = workflow.FindTransitionInState(Key, OpenState)!;

        var allowed = await _sut.IsTransitionAllowedInStateAsync(
            workflow, transition, OpenState, NewInstance(), role is null ? null : [role]);

        allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Authorize_InTheRestrictedState_StillDeniesAnUnlistedCaller()
    {
        var workflow = BuildWorkflow();
        var transition = workflow.FindTransitionInState(Key, RestrictedState)!;

        (await _sut.IsTransitionAllowedInStateAsync(
            workflow, transition, RestrictedState, NewInstance(), ["viewer"])).ShouldBeFalse();
        (await _sut.IsTransitionAllowedInStateAsync(
            workflow, transition, RestrictedState, NewInstance(), ["supervisor"])).ShouldBeTrue();
    }

    [Fact]
    public async Task Discovery_FiltersByTheCurrentStatesGrants()
    {
        var workflow = BuildWorkflow();
        var open = workflow.FindState(OpenState)!;
        var restricted = workflow.FindState(RestrictedState)!;

        (await _sut.FilterAuthorizedTransitionKeysAsync(
            workflow, open, NewInstance(), [Key], null)).ShouldBe([Key]);
        (await _sut.FilterAuthorizedTransitionKeysAsync(
            workflow, restricted, NewInstance(), [Key], ["viewer"])).ShouldBeEmpty();
    }
}
