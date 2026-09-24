using System.Collections.Generic;
using System.Text.Json;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Definitions;

/// <summary>
/// Pins the stamp round-trip the SubFlow overrides travel on: written by <c>SubflowStarter</c> with
/// DEFAULT serializer options, read back with <see cref="JsonSerializerConstants.JsonOptions"/>.
/// Field-level replace depends on an absent field surviving as absent — if it came back as a
/// default value, a duration-only override would silently wipe the child's roles.
/// </summary>
public class SubFlowOverrideStampRoundTripTests : DomainTestBase<DomainEntryPoint>
{
    private static List<RoleGrant> Grants(string json) =>
        JsonSerializer.Deserialize<List<RoleGrant>>(json, JsonSerializerConstants.JsonOptions)!;

    private static Dictionary<string, T> RoundTrip<T>(Dictionary<string, T> map) =>
        JsonSerializer.Deserialize<Dictionary<string, T>>(
            JsonSerializer.Serialize(map), JsonSerializerConstants.JsonOptions)!;

    [Fact]
    public void DurationOnlyLongPollOverride_KeepsRolesAbsent()
    {
        var read = RoundTrip(new Dictionary<string, SubFlowStateOverride>
        {
            ["otp-wait"] = SubFlowStateOverride.Create(
                interaction: SubFlowStateInteractionOverride.Create(
                    SubFlowLongPollOverride.Create(fallbackTimeoutSeconds: 180)))
        });

        var longPoll = read["otp-wait"].Interaction!.LongPoll!;
        longPoll.FallbackTimeoutSeconds.ShouldBe(180);
        longPoll.Roles.ShouldBeNull();
        read["otp-wait"].QueryRoles.ShouldBeNull();
        read["otp-wait"].Views.ShouldBeNull();
    }

    [Fact]
    public void RolesOnlyLongPollOverride_KeepsDurationAbsent()
    {
        var read = RoundTrip(new Dictionary<string, SubFlowStateOverride>
        {
            ["otp-wait"] = SubFlowStateOverride.Create(
                interaction: SubFlowStateInteractionOverride.Create(
                    SubFlowLongPollOverride.Create(roles: Grants("""[{"role":"corp.teller","grant":"allow"}]"""))))
        });

        var longPoll = read["otp-wait"].Interaction!.LongPoll!;
        longPoll.FallbackTimeoutSeconds.ShouldBeNull();
        longPoll.Roles!.ShouldHaveSingleItem().Role.ShouldBe("corp.teller");
    }

    [Fact]
    public void ExplicitEmptyRoles_SurviveAsEmptyNotNull()
    {
        var read = RoundTrip(new Dictionary<string, SubFlowStateOverride>
        {
            ["otp-wait"] = SubFlowStateOverride.Create(
                interaction: SubFlowStateInteractionOverride.Create(SubFlowLongPollOverride.Create(roles: [])))
        });

        read["otp-wait"].Interaction!.LongPoll!.Roles.ShouldNotBeNull();
        read["otp-wait"].Interaction!.LongPoll!.Roles!.ShouldBeEmpty();
    }

    [Fact]
    public void StateAndTransitionViewOverrides_RoundTrip()
    {
        var reference = new Reference("corp-otp-view", "core", "sys-views", "1.0.0");

        var states = RoundTrip(new Dictionary<string, SubFlowStateOverride>
        {
            ["otp-wait"] = SubFlowStateOverride.Create(views: new() { ["otp-view"] = reference })
        });
        var transitions = RoundTrip(new Dictionary<string, SubFlowTransitionOverride>
        {
            ["confirm"] = SubFlowTransitionOverride.Create(views: new() { ["confirm-modal"] = reference })
        });

        var stateView = states["otp-wait"].Views!["otp-view"];
        stateView.Key.ShouldBe("corp-otp-view");
        stateView.Domain.ShouldBe("core");
        stateView.Flow.ShouldBe("sys-views");
        stateView.Version.ShouldBe("1.0.0");
        transitions["confirm"].Views!["confirm-modal"].Key.ShouldBe("corp-otp-view");
        transitions["confirm"].Roles.ShouldBeNull();
    }

    [Fact]
    public void StampWrittenByAnEarlierRuntime_ReadsWithNewFieldsAbsent()
    {
        const string legacy = """{"otp-wait":{"queryRoles":[{"Role":"ops","Grant":"allow"}]}}""";

        var read = JsonSerializer.Deserialize<Dictionary<string, SubFlowStateOverride>>(
            legacy, JsonSerializerConstants.JsonOptions)!;

        read["otp-wait"].QueryRoles!.ShouldHaveSingleItem().Role.ShouldBe("ops");
        read["otp-wait"].Interaction.ShouldBeNull();
        read["otp-wait"].Views.ShouldBeNull();
    }
}
