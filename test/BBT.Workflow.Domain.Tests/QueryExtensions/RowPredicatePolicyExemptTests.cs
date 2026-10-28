using System.Collections.Generic;
using System.Text.Json;
using BBT.Workflow.Definitions.GraphQL;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.ExceptionHandling;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.QueryExtensions;

/// <summary>
/// The row-security markers on the filter model (<see cref="GraphQLFilterNode.PolicyExempt"/>,
/// <see cref="GraphQLFilterRequest.RowPredicate"/>) are runtime-only: a client can never bind them.
/// </summary>
public class RowPredicatePolicyExemptTests
{
    [Fact]
    public void PolicyExempt_IsNotBindableFromJson()
    {
        var n = JsonSerializer.Deserialize<GraphQLFilterNode>("""{"policyExempt":true,"attributes":{"a":{"eq":"1"}}}""")!;

        n.PolicyExempt.ShouldBeFalse();
        n.UnrecognizedProperties.ShouldNotBeNull().ShouldContain("policyExempt");
    }

    [Fact]
    public void RowPredicate_IsNotBindableFromJson()
    {
        var r = JsonSerializer.Deserialize<GraphQLFilterRequest>(
            """{"filter":{"attributes":{"a":{"eq":"1"}}},"rowPredicate":{"attributes":{"b":{"eq":"2"}}}}""")!;

        r.RowPredicate.ShouldBeNull();
        r.Filter.ShouldNotBeNull();
    }

    [Fact]
    public void PolicyExempt_IsNotWrittenToJson()
    {
        var json = JsonSerializer.Serialize(new GraphQLFilterNode
        {
            PolicyExempt = true,
            Attributes = new() { ["a"] = new FieldCondition { Eq = "1" } }
        });

        json.ShouldNotContain("policyExempt", Case.Insensitive);
    }

    // ── U-9: a planner-emitted subtree is runtime policy, not caller input ─────────────────────

    /// <summary>Enforcing context in which only <c>amount</c> is filterable; <c>branchCode</c> is undeclared.</summary>
    private static SchemaFilterContext EnforcingWithoutBranchCode(params string[] encrypted) =>
        new(new Dictionary<string, SchemaFieldMetadata>
        {
            ["amount"] = new() { Type = "number", FilterOperators = ["eq", "gt"] },
            ["score"] = new() { Type = "number" }
        })
        { EncryptedPaths = new HashSet<string>(encrypted) };

    private static GraphQLFilterNode Leaf(string field, FieldCondition condition, bool exempt) =>
        new() { Attributes = new() { [field] = condition }, PolicyExempt = exempt };

    private static GraphQLFilterNode ExemptEq(string field) => Leaf(field, new FieldCondition { Eq = "BR-001" }, true);

    private static (string Sql, Npgsql.NpgsqlParameter[] Parameters) Compile(GraphQLFilterNode node, SchemaFilterContext context) =>
        GraphQLJsonFilterService.BuildInstanceQuery(node, "Data", "Instances", "s", null, schemaContext: context);

    // eq compiles to jsonb containment: the path travels in the parameter, not in the SQL text.
    private static string ParameterText(Npgsql.NpgsqlParameter[] parameters) =>
        string.Join("|", System.Linq.Enumerable.Select(parameters, p => p.Value?.ToString()));

    [Fact]
    public void ExemptSubtree_OnNonFilterableField_CompilesUnderEnforcement()
    {
        var (_, parameters) = Compile(ExemptEq("branchCode"), EnforcingWithoutBranchCode());

        ParameterText(parameters).ShouldContain("branchCode");
    }

    [Fact]
    public void ExemptAncestor_CoversUnmarkedDescendants_AndOnlyItsOwnSubtree()
    {
        // The flag is inherited from the first PolicyExempt ancestor; a sibling caller leaf stays enforced.
        var exemptOr = new GraphQLFilterNode
        {
            PolicyExempt = true,
            Or = [Leaf("branchCode", new FieldCondition { Eq = "BR-001" }, false), Leaf("region", new FieldCondition { In = ["N"] }, false)]
        };
        var callerAndRow = new GraphQLFilterNode { And = [Leaf("amount", new FieldCondition { Gt = 5L }, false), exemptOr] };

        Should.NotThrow(() => Compile(callerAndRow, EnforcingWithoutBranchCode()));

        var callerEscapes = new GraphQLFilterNode { And = [Leaf("branchCode", new FieldCondition { Eq = "BR-002" }, false), exemptOr] };
        Should.Throw<SchemaFilterValidationException>(() => Compile(callerEscapes, EnforcingWithoutBranchCode()));
    }

    [Fact]
    public void ExemptMixedColumnDataOr_UnifiedPredicate_CompilesUnderEnforcement()
    {
        // Definition B shape: data leaf OR column leaf forces the unified-predicate compiler path.
        var definitionB = new GraphQLFilterNode
        {
            PolicyExempt = true,
            Or = [ExemptEq("branchCode"), Leaf("createdBy", new FieldCondition { Eq = "alice" }, true)]
        };
        var callerAndRow = new GraphQLFilterNode { And = [Leaf("amount", new FieldCondition { Eq = 1L }, false), definitionB] };

        var (sql, parameters) = Compile(callerAndRow, EnforcingWithoutBranchCode());

        ParameterText(parameters).ShouldContain("branchCode");
        sql.ShouldContain("\"CreatedBy\"");
        sql.ShouldContain(" OR ");
    }

    [Fact]
    public void NonExemptCallerLeaf_SameField_StillThrows()
    {
        Should.Throw<SchemaFilterValidationException>(() =>
            Compile(Leaf("branchCode", new FieldCondition { Eq = "BR-001" }, false), EnforcingWithoutBranchCode()));
    }

    [Fact]
    public void NonExemptCallerLeaf_InMixedOr_StillThrows()
    {
        var callerMixed = new GraphQLFilterNode
        {
            Or = [Leaf("branchCode", new FieldCondition { Eq = "BR-001" }, false), Leaf("status", new FieldCondition { Eq = "A" }, false)]
        };

        Should.Throw<SchemaFilterValidationException>(() => Compile(callerMixed, EnforcingWithoutBranchCode()));
    }

    [Fact]
    public void ExemptLeaf_OnEncryptedPath_StillRefused()
    {
        Should.Throw<SchemaFilterValidationException>(() =>
            Compile(ExemptEq("branchCode"), EnforcingWithoutBranchCode("branchCode")));
        Should.Throw<SchemaFilterValidationException>(() =>
            Compile(ExemptEq("customer"), EnforcingWithoutBranchCode("customer.taxNo")));
    }

    [Fact]
    public void ExemptLeaf_OnEncryptedPath_InUnifiedPredicate_StillRefused()
    {
        var mixed = new GraphQLFilterNode
        {
            PolicyExempt = true,
            Or = [ExemptEq("branchCode"), Leaf("createdBy", new FieldCondition { Eq = "alice" }, true)]
        };

        Should.Throw<SchemaFilterValidationException>(() => Compile(mixed, EnforcingWithoutBranchCode("branchCode")));
    }

    [Fact]
    public void ExemptLeaf_KeepsSchemaTypeCast()
    {
        // score is declared number but not filterable: exemption skips the whitelist, not the cast.
        var (sql, _) = Compile(Leaf("score", new FieldCondition { Gt = 5L }, true), EnforcingWithoutBranchCode());

        sql.ShouldContain("numeric");
    }
}
