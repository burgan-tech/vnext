using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Execution.RuntimeCaller;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using static BBT.Workflow.Authorization.SubEndpoints.RuntimeCallerTestKeys;

namespace BBT.Workflow.Authorization.SubEndpoints;

/// <summary>
/// The sub-endpoint gate's decision (vnext#1265): Off changes nothing, a valid runtime-caller token is
/// admitted under every grant set without a role lookup, and any other caller is decided by the grants
/// with the shared role-grant rule — refused in Enforce, logged and admitted in Observe.
/// </summary>
public sealed class SubEndpointGateTests
{
    private static readonly RuntimeCallTarget SubStart = RuntimeCallTarget.SubStart("core", "child");
    private static readonly RuntimeCallTarget Complete = RuntimeCallTarget.Complete("core", "parent", Guid.NewGuid());
    private static readonly IReadOnlyDictionary<string, string?> Headers = new Dictionary<string, string?> { ["sub"] = "u1" };

    private static readonly SubEndpointGrantOptions RuntimeOnly = new() { Role = "$Everyone", Grant = "deny" };
    private static readonly SubEndpointGrantOptions AllowOps = new() { Role = "vnext.ops", Grant = "allow" };

    private readonly ICallerRoleResolver _roles = Substitute.For<ICallerRoleResolver>();

    private SubEndpointGate Gate(SubEndpointGateMode mode, SubEndpointGateMode? completeMode = null,
        params SubEndpointGrantOptions[] grants)
        => new(Options.Create(new SubEndpointGateOptions
            {
                Mode = mode,
                CompleteMode = completeMode,
                Grants = grants.ToList()
            }),
            _roles,
            NullLogger<SubEndpointGate>.Instance,
            Identity(DefaultRing()));

    private static string Token(RuntimeCallTarget target)
        => RuntimeCallerToken.Create("k1", Key(1), target, "partner", RuntimeCallerClasses.Relay, Now);

    private void CallerHolds(params string[] roles)
        => _roles.ResolveRoleSetAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(new CallerRoleResolution(roles, roles.Length == 0 ? CallerRoleOutcome.Empty : CallerRoleOutcome.FromHeader));

    [Fact]
    public async Task Off_ShouldAdmitEverything_WithoutRoleLookup()
    {
        var gate = Gate(SubEndpointGateMode.Off, null, RuntimeOnly);

        (await gate.AuthorizeAsync(SubStart, "forged", Headers)).IsSuccess.ShouldBeTrue();
        (await gate.AuthorizeAsync(Complete, null, Headers)).IsSuccess.ShouldBeTrue();
        await _roles.DidNotReceiveWithAnyArgs().ResolveRoleSetAsync(default);
    }

    [Theory]
    [InlineData("deny-everyone")]
    [InlineData("allow-ops")]
    [InlineData("deny-customer")]
    public async Task ValidToken_ShouldBeAdmitted_UnderEveryGrantSet_WithoutRoleLookup(string grantSet)
    {
        var grants = grantSet switch
        {
            "deny-everyone" => RuntimeOnly,
            "allow-ops" => AllowOps,
            _ => new SubEndpointGrantOptions { Role = "customer", Grant = "deny" }
        };
        CallerHolds("customer");

        var result = await Gate(SubEndpointGateMode.Enforce, null, grants).AuthorizeAsync(Complete, Token(Complete), Headers);

        result.IsSuccess.ShouldBeTrue();
        await _roles.DidNotReceiveWithAnyArgs().ResolveRoleSetAsync(default);
    }

    [Fact]
    public async Task Enforce_RuntimeOnly_ShouldRefuseTokenlessClient_With403_AndNoRoleLookup()
    {
        var result = await Gate(SubEndpointGateMode.Enforce, null, RuntimeOnly).AuthorizeAsync(SubStart, null, Headers);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.AuthorizationRoleDenied);
        await _roles.DidNotReceiveWithAnyArgs().ResolveRoleSetAsync(default);
    }

    [Theory]
    [InlineData("forged")]
    [InlineData("v1;k=k1;t=1;d=core;c=relay;s=AAAA")]
    [InlineData("expired")]
    [InlineData("other-key")]
    public async Task Enforce_ShouldRefuse_InvalidToken(string token)
    {
        token = token switch
        {
            // Genuine signature, minted an hour before the gate's clock.
            "expired" => RuntimeCallerToken.Create("k1", Key(1), SubStart, "partner", RuntimeCallerClasses.SubFlow, Now.AddHours(-1)),
            "other-key" => RuntimeCallerToken.Create("k1", Key(42), SubStart, "partner", RuntimeCallerClasses.SubFlow, Now),
            _ => token
        };

        var result = await Gate(SubEndpointGateMode.Enforce, null, RuntimeOnly).AuthorizeAsync(SubStart, token, Headers);

        result.Error.Code.ShouldBe(WorkflowErrorCodes.AuthorizationRoleDenied);
    }

    [Fact]
    public async Task Enforce_ShouldRefuse_TokenMintedForAnotherRoute()
    {
        var otherParent = RuntimeCallTarget.Complete("core", "parent", Guid.NewGuid());

        var result = await Gate(SubEndpointGateMode.Enforce, null, RuntimeOnly)
            .AuthorizeAsync(Complete, Token(otherParent), Headers);

        result.Error.Code.ShouldBe(WorkflowErrorCodes.AuthorizationRoleDenied);
    }

    [Fact]
    public async Task Observe_ShouldAdmit_WhatEnforceWouldRefuse()
    {
        var result = await Gate(SubEndpointGateMode.Observe, null, RuntimeOnly).AuthorizeAsync(SubStart, null, Headers);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Grants_ShouldAdmit_TokenlessCallerHoldingAnAllowedRole()
    {
        CallerHolds("vnext.ops");

        var result = await Gate(SubEndpointGateMode.Enforce, null, AllowOps).AuthorizeAsync(SubStart, null, Headers);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Grants_ShouldRefuse_RolelessCaller_UnderAnAllowList()
    {
        // The Inbox backup carries no user: without a token, an allow list refuses it.
        CallerHolds();

        var result = await Gate(SubEndpointGateMode.Enforce, null, AllowOps).AuthorizeAsync(Complete, null, Headers);

        result.Error.Code.ShouldBe(WorkflowErrorCodes.AuthorizationRoleDenied);
    }

    [Fact]
    public async Task Grants_ShouldAnswer503_WhenCallerRolesCannotBeResolved()
    {
        _roles.ResolveRoleSetAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(new CallerRoleResolution([], CallerRoleOutcome.Failed, "unavailable"));

        var enforce = await Gate(SubEndpointGateMode.Enforce, null, AllowOps).AuthorizeAsync(SubStart, null, Headers);
        var observe = await Gate(SubEndpointGateMode.Observe, null, AllowOps).AuthorizeAsync(SubStart, null, Headers);

        enforce.Error.Code.ShouldBe(WorkflowErrorCodes.CallerRolesUnavailable);
        observe.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task CompleteMode_ShouldOverrideMode_ForCompleteOnly()
    {
        var gate = Gate(SubEndpointGateMode.Enforce, SubEndpointGateMode.Observe, RuntimeOnly);

        (await gate.AuthorizeAsync(Complete, null, Headers)).IsSuccess.ShouldBeTrue();
        (await gate.AuthorizeAsync(SubStart, null, Headers)).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task CompleteMode_ShouldGateComplete_WhileModeIsOff()
    {
        var gate = Gate(SubEndpointGateMode.Off, SubEndpointGateMode.Enforce, RuntimeOnly);

        (await gate.AuthorizeAsync(SubStart, null, Headers)).IsSuccess.ShouldBeTrue();
        (await gate.AuthorizeAsync(Complete, null, Headers)).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task NoKeyRing_ShouldTreatEveryCallerAsTokenless()
    {
        var gate = new SubEndpointGate(
            Options.Create(new SubEndpointGateOptions { Mode = SubEndpointGateMode.Enforce, Grants = [RuntimeOnly] }),
            _roles,
            NullLogger<SubEndpointGate>.Instance,
            verifier: null);

        (await gate.AuthorizeAsync(SubStart, Token(SubStart), Headers)).IsSuccess.ShouldBeFalse();
    }
}
