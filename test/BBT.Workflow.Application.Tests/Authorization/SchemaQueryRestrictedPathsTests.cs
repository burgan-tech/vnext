using System.Collections.Generic;
using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Schemas;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Which master-schema paths a caller may not name in a set query — filter, sort, groupBy, aggregations
/// (vnext#1262). The decision is per caller and for EVERY row at once, so a grant that depends on the instance
/// cannot open a path here.
/// </summary>
public sealed class SchemaQueryRestrictedPathsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static List<RoleGrant> Grants(string json) =>
        JsonSerializer.Deserialize<List<RoleGrant>>(json, JsonOptions)!;

    private static SchemaExposureMetadata Exposure(string properties)
    {
        using var doc = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{" + properties + "}}");
        return SchemaRolesParser.ParseExposure(doc.RootElement.Clone());
    }

    private const string Manager = """[{"role":"manager","grant":"allow"}]""";

    // ── EvaluateRolesForRowSet ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Manager, "manager", true)]
    [InlineData(Manager, "branch-user", false)]
    [InlineData(Manager, null, false)]
    [InlineData("""[{"role":"blocked","grant":"deny"}]""", "manager", true)]
    [InlineData("""[{"role":"blocked","grant":"deny"}]""", "blocked", false)]
    // A blacklist cannot be ruled out for a caller with no roles.
    [InlineData("""[{"role":"blocked","grant":"deny"}]""", null, false)]
    [InlineData("""[{"role":"$Everyone","grant":"allow"}]""", null, true)]
    public void StaticGrants_AreDecidedForTheWholeSet(string grants, string? role, bool expected)
    {
        string[] roles = role is null ? [] : [role];

        TransitionAuthorizationManager.EvaluateRolesForRowSet(roles, Grants(grants)).ShouldBe(expected);
    }

    [Fact]
    public void AllRolesOfTheCallerCount_AndADeniedOneIsNotBoughtBack()
    {
        var grants = Grants("""[{"role":"manager","grant":"allow"},{"role":"blocked","grant":"deny"}]""");

        TransitionAuthorizationManager.EvaluateRolesForRowSet(["branch-user", "manager"], grants).ShouldBeTrue();
        TransitionAuthorizationManager.EvaluateRolesForRowSet(["manager", "blocked"], grants).ShouldBeFalse();
    }

    /// <summary>An allow that depends on the row admits nobody for the set: it holds for some rows only.</summary>
    [Theory]
    [InlineData("$InstanceStarter")]
    [InlineData("$PreviousUser")]
    [InlineData("$user.$.context.assignedUser")]
    [InlineData("$userBehalfOf.$.context.assignedUser")]
    [InlineData("$role.$.context.ownerRole")]
    public void AnInstanceDependentAllow_DoesNotAdmit(string leaf)
    {
        var grants = Grants($$"""[{"role":"{{leaf}}","grant":"allow"}]""");

        TransitionAuthorizationManager.EvaluateRolesForRowSet(["manager"], grants).ShouldBeFalse();
    }

    /// <summary>
    /// The reason this is not <c>EvaluateRolesStatic</c>: without an instance that one reads the predefined leaf
    /// as "no match", so the deny never fires and the caller would query a field hidden on the rows it started.
    /// </summary>
    [Theory]
    [InlineData("$InstanceStarter")]
    [InlineData("$user.$.context.assignedUser")]
    [InlineData("$role.$.context.ownerRole")]
    public void AnInstanceDependentDeny_Refuses_WhereTheStaticPathWouldAdmit(string leaf)
    {
        var grants = Grants($$"""[{"role":"manager","grant":"allow"},{"role":"{{leaf}}","grant":"deny"}]""");

        TransitionAuthorizationManager.EvaluateRolesStatic(["manager"], grants).ShouldBeTrue();
        TransitionAuthorizationManager.EvaluateRolesForRowSet(["manager"], grants).ShouldBeFalse();
    }

    [Fact]
    public void AStaticAllowBesideAnInstanceDependentAllow_StillAdmitsItsRole()
    {
        var grants = Grants("""[{"role":"$InstanceStarter","grant":"allow"},{"role":"manager","grant":"allow"}]""");

        TransitionAuthorizationManager.EvaluateRolesForRowSet(["manager"], grants).ShouldBeTrue();
        TransitionAuthorizationManager.EvaluateRolesForRowSet(["branch-user"], grants).ShouldBeFalse();
    }

    [Fact]
    public void AnEmptyGrantSet_Admits() =>
        TransitionAuthorizationManager.EvaluateRolesForRowSet(null, []).ShouldBeTrue();

    // ── Resolve ──────────────────────────────────────────────────────────────────────────────────

    private const string Schema = """
        "assignedUser":{"type":"string","x-roles":[{"role":"manager","grant":"allow"}]},
        "processingBranch":{"type":"string"},
        "customer":{"type":"object","properties":{
            "nationalId":{"type":"string","x-roles":[{"role":"manager","grant":"allow"},{"role":"auditor","grant":"allow"}]},
            "name":{"type":"string"}}},
        "iban":{"type":"string","x-masking":{"operator":"mask","params":{"keepLast":4},"roles":[{"role":"auditor","grant":"allow"}]}},
        "phone":{"type":"string","x-masking":{"operator":"mask"}},
        "taxNo":{"type":"string","x-encryption":{"type":"hash"}},
        "secret":{"type":"string","x-encryption":{"type":"encrypt"}}
        """;

    [Fact]
    public void ACallerWithoutTheRole_IsRestrictedOnEveryGuardedAndMaskedPath()
    {
        var restricted = SchemaQueryRestrictedPaths.Resolve(
            Exposure(Schema), ["branch-user"], rolesResolved: true, maskingEnabled: true);

        restricted.ShouldBe(["assignedUser", "customer.nationalId", "iban", "phone"], ignoreOrder: true);
    }

    [Fact]
    public void RolesOpenExactlyThePathsTheyAreGrantedOrExemptOn()
    {
        var exposure = Exposure(Schema);

        // manager: both x-roles paths; not exempt from either mask.
        SchemaQueryRestrictedPaths.Resolve(exposure, ["manager"], true, true)
            .ShouldBe(["iban", "phone"], ignoreOrder: true);
        // auditor: one x-roles path and the iban exemption. 'phone' has no exemption list: nobody is exempt.
        SchemaQueryRestrictedPaths.Resolve(exposure, ["auditor"], true, true)
            .ShouldBe(["assignedUser", "phone"], ignoreOrder: true);
        SchemaQueryRestrictedPaths.Resolve(exposure, ["manager", "auditor"], true, true)
            .ShouldBe(["phone"]);
    }

    /// <summary>
    /// <c>hash</c> stores the digest every reader gets; <c>encrypt</c> is refused for every caller through
    /// <c>SchemaFilterContext.EncryptedPaths</c>. Neither is a per-caller restriction.
    /// </summary>
    [Fact]
    public void EncryptionPaths_AreNeverListed()
    {
        var restricted = SchemaQueryRestrictedPaths.Resolve(Exposure(Schema), [], true, true);

        restricted.ShouldNotContain("taxNo");
        restricted.ShouldNotContain("secret");
    }

    [Fact]
    public void WithMaskingSwitchedOff_MaskedPathsAreServedClear_AndAreNotRestricted()
    {
        var restricted = SchemaQueryRestrictedPaths.Resolve(Exposure(Schema), ["branch-user"], true, maskingEnabled: false);

        restricted.ShouldBe(["assignedUser", "customer.nationalId"], ignoreOrder: true);
    }

    /// <summary>Fail closed, like the exposure pass that prunes every guarded field when roles cannot be resolved.</summary>
    [Fact]
    public void WhenRolesCouldNotBeResolved_EverythingGuardedIsRestricted_WhateverRolesArePassed()
    {
        var restricted = SchemaQueryRestrictedPaths.Resolve(
            Exposure(Schema), ["manager", "auditor"], rolesResolved: false, maskingEnabled: true);

        restricted.ShouldBe(["assignedUser", "customer.nationalId", "iban", "phone"], ignoreOrder: true);
    }

    [Fact]
    public void ARoleLessCaller_IsRestrictedOnEveryGuardedPath()
    {
        var restricted = SchemaQueryRestrictedPaths.Resolve(Exposure(Schema), null, true, true);

        restricted.ShouldBe(["assignedUser", "customer.nationalId", "iban", "phone"], ignoreOrder: true);
    }

    [Fact]
    public void AnInstanceDependentExemption_DoesNotExemptForASet()
    {
        var exposure = Exposure("""
            "iban":{"type":"string","x-masking":{"operator":"mask","roles":[{"role":"$InstanceStarter","grant":"allow"}]}}
            """);

        SchemaQueryRestrictedPaths.Resolve(exposure, ["manager"], true, true).ShouldBe(["iban"]);
    }

    [Fact]
    public void ASchemaThatGuardsNothing_RestrictsNothing_AndNeedsNoRoles()
    {
        var exposure = Exposure("""
            "amount":{"type":"number"},"taxNo":{"type":"string","x-encryption":{"type":"hash"}}
            """);

        SchemaQueryRestrictedPaths.HasGuardedPaths(exposure, maskingEnabled: true).ShouldBeFalse();
        SchemaQueryRestrictedPaths.Resolve(exposure, null, rolesResolved: false, maskingEnabled: true).ShouldBeEmpty();
    }

    [Fact]
    public void HasGuardedPaths_CountsMasksOnlyWhileMaskingIsEnabled()
    {
        var masksOnly = Exposure("""
            "phone":{"type":"string","x-masking":{"operator":"mask"}}
            """);

        SchemaQueryRestrictedPaths.HasGuardedPaths(masksOnly, maskingEnabled: true).ShouldBeTrue();
        SchemaQueryRestrictedPaths.HasGuardedPaths(masksOnly, maskingEnabled: false).ShouldBeFalse();
        SchemaQueryRestrictedPaths.HasGuardedPaths(Exposure(Schema), maskingEnabled: false).ShouldBeTrue();
    }
}
