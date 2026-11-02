using System;
using System.Linq;
using System.Text.RegularExpressions;
using BBT.Workflow.Instances;
using BBT.Workflow.Definitions.GraphQL;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Pins the row-security scope switch (vnext#1107): row security is opt-in through
/// <c>ApplyRowSecurity</c>, which only the public list / GET endpoint sets. It is never serialized, so a remote
/// call cannot carry it and the receiving endpoint decides for itself; no bypass-named member exists either.
/// </summary>
public sealed class RowSecurityNoBypassMemberTests
{
    [Fact]
    public void NoBypassMember_OnAnyModelBoundGatewayOrTaskBuiltInput()
    {
        var types = new[] { typeof(GetInstanceInput), typeof(GetInstanceListInput), typeof(GetInstanceDataInput), typeof(GraphQLFilterRequest) }
            .Concat(typeof(GetInstanceInput).Assembly.GetTypes()
                .Where(t => t.Name.EndsWith("Input") && t.Namespace?.StartsWith("BBT.Workflow.Instances") == true))
            .Distinct()
            .ToList();

        types.Count.ShouldBeGreaterThan(4);
        var offenders = types
            .SelectMany(t => t.GetProperties().Select(p => $"{t.Name}.{p.Name}"))
            .Where(n => Regex.IsMatch(n[(n.IndexOf('.') + 1)..], "SystemRead|Bypass|Unfiltered|SkipRow|IgnoreRow", RegexOptions.IgnoreCase))
            .ToList();
        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void ApplyRowSecurity_IsOptIn_AndNeverSerialized()
    {
        foreach (var type in new[] { typeof(GetInstanceInput), typeof(GetInstanceListInput) })
        {
            var property = type.GetProperty("ApplyRowSecurity");
            property.ShouldNotBeNull();
            property.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false).ShouldNotBeEmpty();
            property.GetValue(Activator.CreateInstance(type)).ShouldBe(false);
        }

        System.Text.Json.JsonSerializer.Serialize(new GetInstanceInput { ApplyRowSecurity = true })
            .ShouldNotContain("ApplyRowSecurity", Case.Insensitive);
        System.Text.Json.JsonSerializer.Deserialize<GetInstanceListInput>("""{"ApplyRowSecurity":true,"applyRowSecurity":true}""")!
            .ApplyRowSecurity.ShouldBeFalse();
    }
}
