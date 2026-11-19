using System.Collections.Generic;
using System.Linq;
using Shouldly;
using Xunit;
using static BBT.Workflow.Authorization.SubEndpoints.RuntimeCallerTestKeys;

namespace BBT.Workflow.Authorization.SubEndpoints;

/// <summary>
/// Startup validation of <c>Workflow:SubEndpointGate</c> (vnext#1265): a gate that is on must hold an
/// explicit grant set the gate can evaluate without an instance, and a key ring that can prove runtime calls.
/// </summary>
public sealed class SubEndpointGateOptionsValidatorTests
{
    private static readonly SubEndpointGrantOptions RuntimeOnly = new() { Role = "$Everyone", Grant = "deny" };

    private static IReadOnlyList<string> Failures(SubEndpointGateOptions options, bool keyRing = true)
    {
        var result = new SubEndpointGateOptionsValidator(keyRing ? Identity(DefaultRing()) : Identity(null))
            .Validate(null, options);
        return result.Failed ? result.Failures!.ToList() : [];
    }

    [Fact]
    public void Default_ShouldPass_WithoutAKeyRing()
        => Failures(new SubEndpointGateOptions(), keyRing: false).ShouldBeEmpty();

    [Theory]
    [InlineData(SubEndpointGateMode.Observe, null)]
    [InlineData(SubEndpointGateMode.Enforce, null)]
    [InlineData(SubEndpointGateMode.Off, SubEndpointGateMode.Enforce)]
    public void On_ShouldRequireGrants(SubEndpointGateMode mode, SubEndpointGateMode? completeMode)
        => Failures(new SubEndpointGateOptions { Mode = mode, CompleteMode = completeMode })
            .ShouldContain(f => f.Contains("Grants must be non-empty"));

    [Fact]
    public void On_ShouldRequireAKeyRing()
        => Failures(new SubEndpointGateOptions { Mode = SubEndpointGateMode.Observe, Grants = [RuntimeOnly] }, keyRing: false)
            .ShouldContain(f => f.Contains("no runtime-caller key ring"));

    [Fact]
    public void On_ShouldPass_WithGrantsAndAKeyRing()
        => Failures(new SubEndpointGateOptions { Mode = SubEndpointGateMode.Enforce, Grants = [RuntimeOnly] })
            .ShouldBeEmpty();

    [Theory]
    [InlineData("$InstanceStarter")]
    [InlineData("$PreviousUser")]
    [InlineData("$user.$.context.owner")]
    [InlineData("$role.$.context.team")]
    public void Grants_ShouldReject_LeavesThatNeedAnInstanceOrIdentity(string leaf)
        => Failures(new SubEndpointGateOptions
            {
                Mode = SubEndpointGateMode.Enforce,
                Grants = [new SubEndpointGrantOptions { Role = leaf, Grant = "allow" }]
            })
            .ShouldContain(f => f.Contains(leaf));

    [Fact]
    public void Grants_ShouldReject_MalformedGrants_EvenWhileOff()
        => Failures(new SubEndpointGateOptions
            {
                Grants =
                [
                    new SubEndpointGrantOptions { Grant = "allow" },
                    new SubEndpointGrantOptions { Role = "ops", Grant = "maybe" }
                ]
            })
            .Count.ShouldBe(2);

    [Fact]
    public void Grants_ShouldAccept_Combinators()
        => Failures(new SubEndpointGateOptions
            {
                Mode = SubEndpointGateMode.Enforce,
                Grants =
                [
                    new SubEndpointGrantOptions
                    {
                        Grant = "allow",
                        AllOf = [new SubEndpointGrantConditionOptions { Role = "ops" }, new SubEndpointGrantConditionOptions { Role = "oncall" }]
                    }
                ]
            })
            .ShouldBeEmpty();
}
