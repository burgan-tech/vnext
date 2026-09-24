using System;
using System.Collections.Generic;
using System.Text.Json;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Pins the two child-side resolvers of parent-supplied SubFlow overrides. They are the ONLY
/// readers of the long-poll and view override fields: the pipeline arm, the interaction gate, the
/// state body and the view function all ask them, so the four can never disagree about which
/// window or which grants are in force.
/// </summary>
public class InstanceSubFlowOverrideResolutionTests : DomainTestBase<DomainEntryPoint>
{
    private static State LongPollState(string longPollJson) =>
        JsonSerializer.Deserialize<State>($$"""
        {
            "key": "otp-wait",
            "stateType": "intermediate",
            "subType": "none",
            "versionStrategy": "Patch",
            "interaction": { "longPoll": {{longPollJson}} }
        }
        """, JsonSerializerConstants.JsonOptions)!;

    private static State PlainState() =>
        State.Create("otp-wait", StateType.Intermediate, StateSubType.None, VersionStrategy.IncreasePatch.Code);

    private static Instance StampedChild(string? statesJson = null, string? transitionsJson = null)
    {
        var instance = Instance.Create(Guid.NewGuid(), "child-flow", "1.0.0", "k");
        if (statesJson is not null)
            instance.ExtraProperties[DomainConsts.MetaDataKeys.StateRoleOverrides] = statesJson;
        if (transitionsJson is not null)
            instance.ExtraProperties[DomainConsts.MetaDataKeys.TransitionRoleOverrides] = transitionsJson;
        return instance;
    }

    private const string ChildRoles = """{ "terminate": true, "fallbackTimeoutSeconds": 30, "roles": [ { "role": "retail", "grant": "allow" } ] }""";

    [Fact]
    public void NoStamp_ReturnsTheChildsOwnLongPoll()
    {
        var resolution = StampedChild().ResolveEffectiveLongPoll(LongPollState(ChildRoles));

        resolution.LongPoll!.FallbackTimeoutSeconds.ShouldBe(30);
        resolution.LongPoll.Roles.ShouldHaveSingleItem().Role.ShouldBe("retail");
        resolution.LongPoll.Terminate.ShouldBeTrue();
        resolution.OverridePresent.ShouldBeFalse();
    }

    [Fact]
    public void DurationOnlyOverride_ReplacesDurationAndKeepsTheChildsRoles()
    {
        var child = StampedChild("""{"otp-wait":{"interaction":{"longPoll":{"fallbackTimeoutSeconds":180}}}}""");

        var resolution = child.ResolveEffectiveLongPoll(LongPollState(ChildRoles));

        resolution.LongPoll!.FallbackTimeoutSeconds.ShouldBe(180);
        resolution.LongPoll.Roles.ShouldHaveSingleItem().Role.ShouldBe("retail");
        resolution.OverridePresent.ShouldBeTrue();
    }

    [Fact]
    public void RolesOnlyOverride_ReplacesTheWholeListAndKeepsTheChildsDuration()
    {
        var child = StampedChild("""{"otp-wait":{"interaction":{"longPoll":{"roles":[{"role":"corp","grant":"allow"},{"role":"ops","grant":"allow"}]}}}}""");

        var resolution = child.ResolveEffectiveLongPoll(LongPollState(ChildRoles));

        resolution.LongPoll!.FallbackTimeoutSeconds.ShouldBe(30);
        resolution.LongPoll.Roles.Count.ShouldBe(2);
        resolution.LongPoll.Roles.ShouldNotContain(g => g.Role == "retail");
    }

    [Fact]
    public void ExplicitEmptyRoles_ReplaceTheChildsRolesWithDefaultAllow()
    {
        var child = StampedChild("""{"otp-wait":{"interaction":{"longPoll":{"roles":[]}}}}""");

        child.ResolveEffectiveLongPoll(LongPollState(ChildRoles)).LongPoll!.Roles.ShouldBeEmpty();
    }

    [Fact]
    public void ChildWithoutLongPoll_IgnoresTheOverrideAndAddsNothing()
    {
        var child = StampedChild("""{"otp-wait":{"interaction":{"longPoll":{"fallbackTimeoutSeconds":180}}}}""");

        var resolution = child.ResolveEffectiveLongPoll(PlainState());

        resolution.LongPoll.ShouldBeNull();
        resolution.OverrideIgnoredNoLongPoll.ShouldBeTrue();
    }

    [Fact]
    public void ChildOnTheRuleArm_KeepsTheRuleIgnoresRolesAndStillAppliesDuration()
    {
        var child = StampedChild("""{"otp-wait":{"interaction":{"longPoll":{"fallbackTimeoutSeconds":180,"roles":[{"role":"corp","grant":"allow"}]}}}}""");
        var ruleState = LongPollState("""{ "terminate": true, "rule": { "location": "./r.csx", "code": "cmV0dXJuIHRydWU7" } }""");

        var resolution = child.ResolveEffectiveLongPoll(ruleState);

        resolution.LongPoll!.Rule.ShouldNotBeNull();
        resolution.LongPoll.Roles.ShouldBeEmpty();
        resolution.LongPoll.FallbackTimeoutSeconds.ShouldBe(180);
        resolution.RolesOverrideIgnoredRuleArm.ShouldBeTrue();
    }

    [Fact]
    public void OverrideForAnotherState_DoesNotApply()
    {
        var child = StampedChild("""{"other-state":{"interaction":{"longPoll":{"fallbackTimeoutSeconds":180}}}}""");

        var resolution = child.ResolveEffectiveLongPoll(LongPollState(ChildRoles));

        resolution.LongPoll!.FallbackTimeoutSeconds.ShouldBe(30);
        resolution.OverridePresent.ShouldBeFalse();
    }

    [Fact]
    public void MalformedStamp_FallsBackToTheChildAndSaysSo()
    {
        var resolution = StampedChild("{not json").ResolveEffectiveLongPoll(LongPollState(ChildRoles));

        resolution.LongPoll!.FallbackTimeoutSeconds.ShouldBe(30);
        resolution.StampMalformed.ShouldBeTrue();
    }

    [Fact]
    public void NoDeclaredDuration_UsesTheDefaultWindow()
    {
        var resolution = StampedChild().ResolveEffectiveLongPoll(LongPollState("""{ "terminate": true }"""));

        resolution.LongPoll!.FallbackTimeoutSeconds.ShouldBe(LongPollInteraction.DefaultFallbackTimeoutSeconds);
    }

    [Fact]
    public void ViewOverride_IsKeyedByStateAndViewKey()
    {
        var child = StampedChild(
            """{"otp-wait":{"views":{"otp-view":{"key":"corp-otp-view","domain":"core","flow":"sys-views","version":"1.0.0"}}}}""");

        child.ResolveViewOverride("otp-wait", null, "otp-view")!.Key.ShouldBe("corp-otp-view");
        child.ResolveViewOverride("otp-wait", null, "other-view").ShouldBeNull();
        child.ResolveViewOverride("another-state", null, "otp-view").ShouldBeNull();
    }

    [Fact]
    public void TransitionViewOverride_IsKeyedByTransitionAndViewKey_AndIgnoresStateEntries()
    {
        var child = StampedChild(
            statesJson: """{"otp-wait":{"views":{"confirm-modal":{"key":"state-level","domain":"core","flow":"sys-views","version":"1.0.0"}}}}""",
            transitionsJson: """{"confirm":{"views":{"confirm-modal":{"key":"corp-confirm-modal","domain":"core","flow":"sys-views","version":"1.0.0"}}}}""");

        child.ResolveViewOverride("otp-wait", "confirm", "confirm-modal")!.Key.ShouldBe("corp-confirm-modal");
        child.ResolveViewOverride("otp-wait", "reject", "confirm-modal").ShouldBeNull();
    }
}
