using System;
using System.Linq;
using System.Text.RegularExpressions;
using BBT.Workflow.Instances;
using BBT.Workflow.Definitions.GraphQL;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Pins that no gateway or task-built input DTO carries a row-security bypass member (vnext#1107). Internal
/// callers that only need an id use <see cref="IInstanceIdResolver"/> instead.
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
}
