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
/// Spec §4.1 outcome tables (Definitions A and B) and the deny-by-default edges of §4 steps 1-4.
/// </summary>
public class RowSecurityPlannerTableTests
{
    private const string BranchRule =
        """{"roles":["branch-staff"],"filter":{"attributes":{"branchCode":{"eq":"$.context.Headers.x-branch-code"}}}}""";

    private const string AuditorRule = """{"roles":["hq-auditor"]}""";

    private static readonly RowSecurityDefinition A = Definition(BranchRule, AuditorRule);

    private static readonly RowSecurityDefinition B = Definition(BranchRule, AuditorRule, """{"roles":["$InstanceStarter"]}""");

    // roles: null = no credential (Skipped), "" = identity without roles (Empty), else comma list (Resolved).
    // identity: ActorUserName "alice" when true. branchHeader: the x-branch-code header, null = absent.
    [Theory]
    [InlineData("A", null, false, "BR-001", "none")]                                            // no credential
    [InlineData("A", "", true, "BR-001", "none")]                                               // identity, no roles
    [InlineData("A", "menu.view", true, "BR-001", "none")]
    [InlineData("A", "branch-staff", true, "BR-001", "branchCode eq BR-001")]
    [InlineData("A", "branch-staff", true, null, "none")]                                       // header missing
    [InlineData("A", "branch-staff,menu.view,report.read", true, "BR-001", "branchCode eq BR-001")]
    [InlineData("A", "branch-staff,hq-auditor", true, "BR-001", "all")]
    [InlineData("A", "hq-auditor", true, "BR-001", "all")]
    [InlineData("B", null, false, "BR-001", "none")]                                            // no credential
    [InlineData("B", "", true, "BR-001", "createdBy eq alice")]                                 // own instances
    [InlineData("B", "menu.view", true, "BR-001", "createdBy eq alice")]
    [InlineData("B", "branch-staff", true, "BR-002", "(branchCode eq BR-002) or (createdBy eq alice)")]
    [InlineData("B", "branch-staff", true, null, "createdBy eq alice")]                         // header missing
    [InlineData("B", "branch-staff", false, "BR-001", "branchCode eq BR-001")]                  // no identity
    public void Table(string def, string? roles, bool identity, string? branchHeader, string expected)
    {
        var resolution = roles switch
        {
            null => new CallerRoleResolution([], CallerRoleOutcome.Skipped),
            "" => new CallerRoleResolution([], CallerRoleOutcome.Empty),
            _ => new CallerRoleResolution(roles.Split(','), CallerRoleOutcome.Resolved)
        };

        var decision = Plan(def == "A" ? A : B, resolution, identity ? "alice" : null, branchHeader);

        Label(decision).ShouldBe(expected);
    }

    [Fact]
    public void ResolutionFailed_IsNone_EvenForHqAuditor()
    {
        foreach (var definition in new[] { A, B })
        {
            var decision = Plan(definition, new CallerRoleResolution(["hq-auditor"], CallerRoleOutcome.Failed, "status"),
                "alice", "BR-001");

            decision.Outcome.ShouldBe(RowSecurityOutcome.DeniedResolutionFailed);
            decision.ReturnsNoRows.ShouldBeTrue();
            decision.Predicate.ShouldBeNull();
            decision.MatchedRuleIndexes.ShouldBeEmpty();
        }
    }

    [Fact]
    public void NoStaticMatch_NoIdentityRule_IsNoRule_WithoutPredicate()
    {
        var decision = Plan(A, new CallerRoleResolution(["menu.view"], CallerRoleOutcome.Resolved), "alice", "BR-001");

        decision.Outcome.ShouldBe(RowSecurityOutcome.NoRule);
        decision.Predicate.ShouldBeNull();
        decision.ReturnsNoRows.ShouldBeTrue();
    }

    [Fact]
    public void MatchedRule_WhoseParameterIsMissing_IsNoContribution()
    {
        var decision = Plan(A, new CallerRoleResolution(["branch-staff"], CallerRoleOutcome.Resolved), "alice", null);

        decision.Outcome.ShouldBe(RowSecurityOutcome.NoContribution);
        decision.Predicate.ShouldBeNull();
        decision.MatchedRuleIndexes.ShouldBe([0]);
        var drop = decision.DroppedRules.ShouldHaveSingleItem();
        drop.RuleIndex.ShouldBe(0);
        drop.Reason.ShouldBe(RowRuleDropReasons.Missing);
        drop.ParameterNames.ShouldBe(["x-branch-code"]);
        decision.MissingParameterNames.ShouldBe(["x-branch-code"]);
    }

    [Fact]
    public void BlankActor_AddsNoIdentityTerm()
    {
        var decision = Plan(B, new CallerRoleResolution([], CallerRoleOutcome.Empty), "  ", null);

        decision.Outcome.ShouldBe(RowSecurityOutcome.NoRule);
        decision.Predicate.ShouldBeNull();
    }

    [Fact]
    public void Skipped_IsRoleless_NotFailed()
    {
        var skipped = new CallerRoleResolution([], CallerRoleOutcome.Skipped);

        Plan(A, skipped, "alice", "BR-001").Outcome.ShouldBe(RowSecurityOutcome.NoRule);

        var underB = Plan(B, skipped, "alice", "BR-001");
        underB.Outcome.ShouldBe(RowSecurityOutcome.Filtered);
        Render(underB.Predicate!).ShouldBe("createdBy eq alice");
    }

    [Fact]
    public void RoleNames_MatchIgnoringCase()
    {
        var decision = Plan(A, new CallerRoleResolution(["HQ-Auditor"], CallerRoleOutcome.FromHeader), null, null);

        decision.Outcome.ShouldBe(RowSecurityOutcome.Unrestricted);
        decision.Predicate.ShouldBeNull();
        decision.ReturnsNoRows.ShouldBeFalse();
    }

    [Fact]
    public void IdentityRuleFilter_IsAndedWithTheIdentityPredicate()
    {
        var definition = Definition("""{"roles":["$InstanceStarter"],"filter":{"status":{"eq":"A"}}}""");

        var decision = Plan(definition, new CallerRoleResolution([], CallerRoleOutcome.Empty), " alice ", null);

        Render(decision.Predicate!).ShouldBe("(createdBy eq alice) and (status eq A)");
        decision.MatchedRuleIndexes.ShouldBe([0]);
    }

    [Fact]
    public void BehalfOfRule_ReadsUserName()
    {
        var definition = Definition("""{"roles":["$InstanceBehalfOfStarter"]}""");

        var decision = Plan(definition, new CallerRoleResolution([], CallerRoleOutcome.Empty), "alice", null, userName: "bob");

        Render(decision.Predicate!).ShouldBe("createdByBehalfOf eq bob");
    }

    [Fact]
    public void EveryEmittedNode_IsPolicyExempt()
    {
        var definition = Definition(
            """{"roles":["r"],"filter":{"or":[{"attributes":{"a":{"in":["1","$.context.Headers.x-b"]}}},{"not":{"b":{"eq":"2"}}}]}}""",
            """{"roles":["$InstanceStarter"],"filter":{"status":{"eq":"A"}}}""");

        var decision = Plan(definition, new CallerRoleResolution(["r"], CallerRoleOutcome.Resolved), "alice", null,
            extraHeaders: new() { ["x-b"] = ["7"] });

        Render(decision.Predicate!).ShouldBe(
            "((a in [1,7]) or (not (b eq 2))) or ((createdBy eq alice) and (status eq A))");
        AllNodes(decision.Predicate!).ShouldAllBe(n => n.PolicyExempt);
        AllNodes(decision.Predicate!).ShouldAllBe(n => n.NodeType != FilterNodeType.Empty);
    }

    [Theory]
    [InlineData(RowSecurityOutcome.NotConfigured, null)]
    [InlineData(RowSecurityOutcome.Unrestricted, "unrestricted")]
    [InlineData(RowSecurityOutcome.Filtered, "filtered")]
    [InlineData(RowSecurityOutcome.NoRule, "denied-no-match")]
    [InlineData(RowSecurityOutcome.NoContribution, "denied-no-contribution")]
    [InlineData(RowSecurityOutcome.DeniedResolutionFailed, "denied-resolution-failed")]
    [InlineData(RowSecurityOutcome.DeniedRuleSetUnavailable, "denied-flow-load")]
    public void Outcome_TagVocabulary(RowSecurityOutcome outcome, string? tag) =>
        outcome.ToTagValue().ShouldBe(tag);

    private static RowSecurityDefinition Definition(params string[] rules) =>
        JsonSerializer.Deserialize<RowSecurityDefinition>($$"""{"rules":[{{string.Join(",", rules)}}]}""")!;

    private static RowSecurityDecision Plan(RowSecurityDefinition definition, CallerRoleResolution roles,
        string? actor, string? branchHeader, string? userName = null, Dictionary<string, string[]>? extraHeaders = null)
    {
        var headers = extraHeaders ?? new Dictionary<string, string[]>();
        if (branchHeader is not null)
            headers["x-branch-code"] = [branchHeader];

        var user = Substitute.For<ICurrentUser>();
        user.ActorUserName.Returns(actor);
        user.UserName.Returns(userName);

        var source = new RequestRowParameterSource(headers, null, null, null, user);
        return RowSecurityPlanner.Plan(new RowSecurityPlanInput(
            definition, roles, source, null, SchemaExposureMetadata.Empty, new RowSecurityOptions()));
    }

    private static string Label(RowSecurityDecision decision) =>
        decision.Outcome == RowSecurityOutcome.Unrestricted ? "all"
        : decision.ReturnsNoRows ? "none"
        : Render(decision.Predicate!);

    private static IEnumerable<GraphQLFilterNode> AllNodes(GraphQLFilterNode node)
    {
        yield return node;
        var children = (node.And ?? []).Concat(node.Or ?? []).Concat(node.Not is null ? [] : [node.Not]);
        foreach (var child in children)
        foreach (var descendant in AllNodes(child))
            yield return descendant;
    }

    /// <summary>Compact text of a planned node: <c>field op value</c>, <c>(x) or (y)</c>, <c>not (x)</c>.</summary>
    internal static string Render(GraphQLFilterNode node) => node.NodeType switch
    {
        FilterNodeType.And => string.Join(" and ", node.And!.Select(c => $"({Render(c)})")),
        FilterNodeType.Or => string.Join(" or ", node.Or!.Select(c => $"({Render(c)})")),
        FilterNodeType.Not => $"not ({Render(node.Not!)})",
        FilterNodeType.Condition => string.Join(" and ", node.Attributes!.SelectMany(a =>
            a.Value.GetOperators().Select(o => $"{a.Key} {o.Operator} {Value(o.Value)}"))),
        _ => throw new InvalidOperationException("Empty node emitted")
    };

    private static string Value(object? value) => value switch
    {
        object[] items => $"[{string.Join(",", items.Select(Value))}]",
        null => "null",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!
    };
}
