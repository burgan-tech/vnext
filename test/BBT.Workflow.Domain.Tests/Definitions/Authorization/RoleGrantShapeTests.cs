using System;
using System.Text.Json;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Definitions.Authorization;

public sealed class RoleGrantShapeTests
{
    private static RoleGrant Parse(string json) =>
        JsonSerializer.Deserialize<RoleGrant>(json, JsonSerializerConstants.JsonOptions)!;

    [Fact]
    public void Plain_role_grant_still_parses()
    {
        var g = Parse("""{"role":"maker","grant":"allow"}""");
        g.Role.ShouldBe("maker");
        g.AllOf.ShouldBeNull();
        g.LeafRoles.ShouldBe(["maker"]);
    }

    [Fact]
    public void AllOf_grant_parses_and_exposes_leaves()
    {
        var g = Parse("""{"grant":"allow","allOf":[{"role":"customer-role"},{"role":"$InstanceStarter"}]}""");
        g.Role.ShouldBeNull();
        g.IsCombinator.ShouldBeTrue();
        g.LeafRoles.ShouldBe(["customer-role", "$InstanceStarter"]);
    }

    [Theory]
    [InlineData("""{"grant":"allow","role":"a","allOf":[{"role":"b"}]}""")]
    [InlineData("""{"grant":"allow","allOf":[{"role":"a"}],"anyOf":[{"role":"b"}]}""")]
    [InlineData("""{"grant":"allow","allOf":[]}""")]
    [InlineData("""{"grant":"allow"}""")]
    [InlineData("""{"grant":"allow","anyOf":[{"role":""}]}""")]
    public void Invalid_shapes_throw_ArgumentException(string json) =>
        Should.Throw<ArgumentException>(() => Parse(json));

    [Fact]
    public void Serialization_omits_null_members()
    {
        var json = JsonSerializer.Serialize(
            Parse("""{"grant":"deny","anyOf":[{"role":"x"}]}"""), JsonSerializerConstants.JsonOptions);
        json.ShouldNotContain("role\":null", Case.Insensitive);
        json.ShouldNotContain("allOf", Case.Insensitive);
    }
}
