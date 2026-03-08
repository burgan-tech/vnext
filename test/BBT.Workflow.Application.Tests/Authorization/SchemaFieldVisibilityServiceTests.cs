using System;
using System.Collections.Generic;
using System.Text.Json;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Unit tests for SchemaFieldVisibilityService (field-level visibility by caller roles).
/// </summary>
public sealed class SchemaFieldVisibilityServiceTests
{
    private static RoleGrant Grant(string role, string grant)
    {
        return JsonSerializer.Deserialize<RoleGrant>($@"{{""Role"":""{role}"",""Grant"":""{grant}""}}")!;
    }

    [Fact]
    public void GetVisiblePaths_WhenNoPathGrants_ReturnsEmpty()
    {
        var pathGrants = new Dictionary<string, IReadOnlyList<RoleGrant>>();
        var visible = SchemaFieldVisibilityService.GetVisiblePaths(pathGrants, new[] { "maker" });
        visible.ShouldBeEmpty();
    }

    [Fact]
    public void GetVisiblePaths_WhenCallerRoleAllowed_IncludesPath()
    {
        var pathGrants = new Dictionary<string, IReadOnlyList<RoleGrant>>
        {
            ["amount"] = new List<RoleGrant> { Grant("morph-idm.maker", "allow") }
        };
        var visible = SchemaFieldVisibilityService.GetVisiblePaths(pathGrants, new[] { "morph-idm.maker" });
        visible.Count.ShouldBe(1);
        visible.ShouldContain("amount");
    }

    [Fact]
    public void GetVisiblePaths_WhenCallerRoleDenied_ExcludesPath()
    {
        var pathGrants = new Dictionary<string, IReadOnlyList<RoleGrant>>
        {
            ["amount"] = new List<RoleGrant>
            {
                Grant("morph-idm.maker", "allow"),
                Grant("morph-idm.maker", "deny")
            }
        };
        var visible = SchemaFieldVisibilityService.GetVisiblePaths(pathGrants, new[] { "morph-idm.maker" });
        visible.Count.ShouldBe(0);
    }

    [Fact]
    public void GetVisiblePaths_WhenMultipleRoles_AnyAllowYieldsVisible()
    {
        var pathGrants = new Dictionary<string, IReadOnlyList<RoleGrant>>
        {
            ["internalNotes"] = new List<RoleGrant> { Grant("morph-idm.approver", "allow") }
        };
        var visible = SchemaFieldVisibilityService.GetVisiblePaths(pathGrants, new[] { "morph-idm.maker", "morph-idm.approver" });
        visible.ShouldContain("internalNotes");
    }

    [Fact]
    public void GetVisiblePaths_WhenCallerRolesNull_ReturnsEmpty()
    {
        var pathGrants = new Dictionary<string, IReadOnlyList<RoleGrant>>
        {
            ["amount"] = new List<RoleGrant> { Grant("maker", "allow") }
        };
        var visible = SchemaFieldVisibilityService.GetVisiblePaths(pathGrants, null);
        visible.Count.ShouldBe(0);
    }

    [Fact]
    public void IsPathVisibleForCaller_WhenNoGrants_ReturnsTrue()
    {
        SchemaFieldVisibilityService.IsPathVisibleForCaller(new List<RoleGrant>(), new[] { "any" }).ShouldBeTrue();
    }

    [Fact]
    public void IsPathVisibleForCaller_WhenNoCallerRoles_ReturnsFalse()
    {
        var grants = new List<RoleGrant> { Grant("maker", "allow") };
        SchemaFieldVisibilityService.IsPathVisibleForCaller(grants, null).ShouldBeFalse();
        SchemaFieldVisibilityService.IsPathVisibleForCaller(grants, Array.Empty<string>()).ShouldBeFalse();
    }
}
