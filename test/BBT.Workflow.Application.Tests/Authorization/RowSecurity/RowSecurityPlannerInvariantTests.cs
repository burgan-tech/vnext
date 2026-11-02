using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BBT.Aether.Users;
using BBT.Workflow.Definitions.GraphQL;
using BBT.Workflow.Definitions.RowSecurity;
using BBT.Workflow.Definitions.Schemas;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Authorization.RowSecurity;

/// <summary>
/// U-4 / SG-1: whatever the rule set and the request, the planner never emits an empty node or an
/// operator-less condition (both mean "no restriction" to the list engine), every emitted node is policy
/// exempt, and a predicate exists only for <see cref="RowSecurityOutcome.Filtered"/>. FsCheck is not referenced
/// by the test project, so this is a seeded deterministic generator (500 cases, seed 1107).
/// </summary>
public class RowSecurityPlannerInvariantTests
{
    private const int Cases = 500;
    private const int Seed = 1107;

    private static readonly string[] Headers = ["x-a", "x-b", "x-c"];
    private static readonly string[] Fields = ["branchCode", "amount", "flag", "status", "iban", "region"];
    private static readonly string[] ScalarOps = ["eq", "ne", "gt", "ge", "lt", "le", "like", "startswith"];
    private static readonly string[] Roles = ["r1", "r2", "r3", "$InstanceStarter"];

    private static readonly SchemaFilterContext Schema = SchemaFilterMetadataResolver.Resolve(JsonDocument.Parse(
        """{"type":"object","properties":{"amount":{"type":"number"},"flag":{"type":"boolean"},"branchCode":{"type":"string"}}}""")
        .RootElement)!;

    private static readonly SchemaExposureMetadata Exposure = SchemaRolesParser.ParseExposure(JsonDocument.Parse(
        """{"type":"object","properties":{"iban":{"type":"string","x-masking":{"operator":"mask"}},"region":{"type":"string","x-roles":[{"role":"officer","grant":"allow"}]}}}""")
        .RootElement);

    [Fact]
    public void Planner_NeverEmitsEmptyNodeOrOperatorlessCondition()
    {
        var random = new Random(Seed);
        var filtered = 0;

        for (var i = 0; i < Cases; i++)
        {
            var rules = RandomRuleSet(random);
            var decision = Plan(rules, random);
            var context = $"case {i}: {rules}";

            if (decision.Outcome == RowSecurityOutcome.Filtered)
            {
                filtered++;
                decision.Predicate.ShouldNotBeNull(context);
                foreach (var node in AllNodes(decision.Predicate!))
                {
                    node.PolicyExempt.ShouldBeTrue(context);
                    node.NodeType.ShouldNotBe(FilterNodeType.Empty, context);
                    if (node.NodeType == FilterNodeType.Condition)
                    {
                        node.Attributes!.Count.ShouldBeGreaterThan(0, context);
                        node.Attributes.Values.ShouldAllBe(c => c.GetOperators().Any(), context);
                        node.Attributes.Values.SelectMany(c => c.GetOperators())
                            .Any(o => o.Value is object[] { Length: 0 }).ShouldBeFalse(context);
                    }
                    else if (node.NodeType is FilterNodeType.And or FilterNodeType.Or)
                    {
                        (node.And ?? node.Or)!.Count.ShouldBeGreaterThan(0, context);
                    }
                }
            }
            else
            {
                decision.Predicate.ShouldBeNull(context);
            }
        }

        // The generator must actually reach the emitting path, or the property proves nothing.
        filtered.ShouldBeGreaterThan(Cases / 10);
    }

    private static string RandomRuleSet(Random random)
    {
        var count = random.Next(1, 4);
        var rules = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var role = Roles[random.Next(Roles.Length)];
            var filter = random.Next(6) == 0 ? "" : $",\"filter\":{RandomNode(random, 0)}";
            rules.Add($$"""{"roles":["{{role}}"]{{filter}}}""");
        }

        return $$"""{"rules":[{{string.Join(",", rules)}}]}""";
    }

    private static string RandomNode(Random random, int depth)
    {
        var kind = depth >= 3 ? 3 : random.Next(4);
        return kind switch
        {
            0 => $$"""{"and":[{{string.Join(",", Enumerable.Range(0, random.Next(1, 3)).Select(_ => RandomNode(random, depth + 1)))}}]}""",
            1 => $$"""{"or":[{{string.Join(",", Enumerable.Range(0, random.Next(1, 3)).Select(_ => RandomNode(random, depth + 1)))}}]}""",
            2 => $$"""{"not":{{RandomNode(random, depth + 1)}}}""",
            _ => RandomLeaf(random)
        };
    }

    private static string RandomLeaf(Random random)
    {
        var field = Fields[random.Next(Fields.Length)];
        string condition = random.Next(7) switch
        {
            0 or 1 => "{\"" + ScalarOps[random.Next(ScalarOps.Length)] + "\":" + RandomScalar(random) + "}",
            2 => $$"""{"in":[{{RandomScalar(random)}},{{RandomScalar(random)}}]}""",
            3 => $$"""{"nin":[{{RandomScalar(random)}}]}""",
            4 => "{\"" + (random.Next(2) == 0 ? "in" : "nin") + "\":{\"$param\":\"$.context.Headers." + RandomHeader(random) + "\""
                 + (random.Next(2) == 0 ? ",\"split\":\",\"" : "") + "}}",
            5 => $$"""{"between":[{{RandomScalar(random)}},{{RandomScalar(random)}}]}""",
            _ => """{"isNull":false}"""
        };
        return "{\"" + field + "\":" + condition + "}";
    }

    private static string RandomScalar(Random random) => random.Next(4) switch
    {
        0 => "\"lit\"",
        1 => "7",
        _ => $"\"$.context.Headers.{RandomHeader(random)}\""
    };

    private static string RandomHeader(Random random) => Headers[random.Next(Headers.Length)];

    private static RowSecurityDecision Plan(string rules, Random random)
    {
        var definition = JsonSerializer.Deserialize<RowSecurityDefinition>(rules)!;

        var headers = new Dictionary<string, string[]>();
        foreach (var header in Headers)
        {
            if (random.Next(4) == 0)
                continue;
            headers[header] = Enumerable.Range(0, random.Next(0, 3)).Select(_ => RandomValue(random)).ToArray();
        }

        var user = Substitute.For<ICurrentUser>();
        user.ActorUserName.Returns(random.Next(3) == 0 ? null : random.Next(2) == 0 ? " " : "alice");

        var callerRoles = Roles.Take(3).Where(_ => random.Next(2) == 0).ToArray();
        var outcome = random.Next(10) == 0 ? CallerRoleOutcome.Failed
            : callerRoles.Length == 0 ? CallerRoleOutcome.Empty
            : CallerRoleOutcome.Resolved;

        return RowSecurityPlanner.Plan(new RowSecurityPlanInput(definition,
            new CallerRoleResolution(callerRoles, outcome),
            new RequestRowParameterSource(headers, null, null, null, user),
            random.Next(2) == 0 ? Schema : null,
            random.Next(2) == 0 ? Exposure : SchemaExposureMetadata.Empty,
            new RowSecurityOptions { MaxElements = 3, MaxElementLength = 12 }));
    }

    private static string RandomValue(Random random) => random.Next(8) switch
    {
        0 => "",
        1 => "   ",
        2 => ",,",
        3 => "1, ,2",
        4 => "abc,def,ghi,jkl",
        5 => new string('z', 13),
        6 => "42",
        _ => " BR-001 "
    };

    private static IEnumerable<GraphQLFilterNode> AllNodes(GraphQLFilterNode node)
    {
        yield return node;
        var children = (node.And ?? []).Concat(node.Or ?? []).Concat(node.Not is null ? [] : [node.Not]);
        foreach (var child in children)
        foreach (var descendant in AllNodes(child))
            yield return descendant;
    }
}
