using System.Linq;
using System.Text.Json;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Definitions.Authorization;

public sealed class RoleGrantDefinitionRulesTests
{
    private static RoleGrant Parse(string json) =>
        JsonSerializer.Deserialize<RoleGrant>(json, JsonSerializerConstants.JsonOptions)!;

    [Theory]
    [InlineData("""{"role":"$everyone","grant":"allow"}""")]
    [InlineData("""{"role":"$EVERYONE","grant":"allow"}""")]
    [InlineData("""{"grant":"allow","allOf":[{"role":"$everyone"}]}""")]
    public void Validate_MiscasedEveryone_IsAnError(string json)
    {
        var messages = RoleGrantDefinitionRules.Validate(Parse(json), "schema.x-roles").ToList();

        messages.Count.ShouldBe(1);
        messages[0].ShouldContain("case-sensitive");
        messages[0].ShouldContain("write '$Everyone'");
    }

    [Theory]
    [InlineData("""{"role":"$Everyone","grant":"allow"}""")]
    [InlineData("""{"grant":"allow","anyOf":[{"role":"$Everyone"},{"role":"maker"}]}""")]
    public void Validate_Everyone_IsAccepted(string json) =>
        RoleGrantDefinitionRules.Validate(Parse(json), "schema.x-roles").ShouldBeEmpty();

    [Fact]
    public void ValidateExemption_Everyone_IsRefused()
    {
        var messages = RoleGrantDefinitionRules
            .ValidateExemption(Parse("""{"role":"$Everyone","grant":"allow"}"""), "schema.x-masking.roles").ToList();

        messages.Count.ShouldBe(1);
        messages[0].ShouldContain("would exempt every caller");
    }
}
