using System.Text.Json;
using BBT.Workflow;
using BBT.Workflow.Definitions.GraphQL;

namespace BBT.Workflow.Monitor.Instances;

/// <summary>
/// Outcome of validating the caller-supplied GraphQL filter for the domain-wide faulted-instances query.
/// </summary>
public enum FaultedFilterValidation
{
    /// <summary>The filter is valid; an effective filter (with injected status) was produced.</summary>
    Valid,

    /// <summary>No filter was supplied; a filter with a bounded createdAt range is mandatory.</summary>
    FilterRequired,

    /// <summary>The filter could not be parsed as GraphQL JSON.</summary>
    FilterInvalid,

    /// <summary>The filter does not contain a bounded createdAt range (lower and upper bound) in its AND context.</summary>
    CreatedAtRangeRequired,

    /// <summary>The caller supplied a status condition, which this endpoint manages itself.</summary>
    StatusNotAllowed
}

/// <summary>
/// Pure validator/builder for the domain-wide faulted-instances filter.
/// Enforces a bounded createdAt range, forbids a caller-supplied status, and produces the
/// effective filter that ANDs the caller filter with <c>status = Faulted</c>.
/// </summary>
public static class FaultedFilterValidator
{
    private const string CreatedAtField = "createdAt";
    private const string StatusField = "status";

    /// <summary>
    /// Validates the caller filter and, when valid, returns the effective filter string
    /// (<c>{"and":[&lt;caller filter&gt;, {"status":{"eq":"Faulted"}}]}</c>).
    /// </summary>
    /// <param name="filter">The caller-supplied GraphQL filter JSON.</param>
    /// <returns>The validation outcome and, on success, the effective filter JSON (null otherwise).</returns>
    public static (FaultedFilterValidation Result, string? EffectiveFilter) BuildEffectiveFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return (FaultedFilterValidation.FilterRequired, null);

        GraphQLFilterNode? node;
        try
        {
            node = GraphQLFilterParser.ParseFilter(filter);
        }
        catch (ArgumentException)
        {
            return (FaultedFilterValidation.FilterInvalid, null);
        }

        if (node is null)
            return (FaultedFilterValidation.FilterInvalid, null);

        if (ContainsStatus(node))
            return (FaultedFilterValidation.StatusNotAllowed, null);

        var hasLower = false;
        var hasUpper = false;
        CollectCreatedAtBoundsInConjunction(node, ref hasLower, ref hasUpper);
        if (!hasLower || !hasUpper)
            return (FaultedFilterValidation.CreatedAtRangeRequired, null);

        var statusNode = new GraphQLFilterNode
        {
            Attributes = new Dictionary<string, FieldCondition>
            {
                [StatusField] = new FieldCondition { Eq = "Faulted" }
            }
        };
        var combined = new GraphQLFilterNode { And = new List<GraphQLFilterNode> { node, statusNode } };
        var effective = JsonSerializer.Serialize(combined, JsonSerializerConstants.JsonOptions);
        return (FaultedFilterValidation.Valid, effective);
    }

    private static bool ContainsStatus(GraphQLFilterNode node)
    {
        switch (node.NodeType)
        {
            case FilterNodeType.And:
                return node.And!.Any(ContainsStatus);
            case FilterNodeType.Or:
                return node.Or!.Any(ContainsStatus);
            case FilterNodeType.Not:
                return ContainsStatus(node.Not!);
            case FilterNodeType.Condition:
                return node.Attributes is not null
                       && node.Attributes.Keys.Any(k => string.Equals(k, StatusField, StringComparison.OrdinalIgnoreCase));
            default:
                return false;
        }
    }

    private static void CollectCreatedAtBoundsInConjunction(GraphQLFilterNode node, ref bool hasLower, ref bool hasUpper)
    {
        switch (node.NodeType)
        {
            case FilterNodeType.And:
                foreach (var child in node.And!)
                    CollectCreatedAtBoundsInConjunction(child, ref hasLower, ref hasUpper);
                break;
            case FilterNodeType.Condition:
                if (node.Attributes is null)
                    break;
                foreach (var (field, condition) in node.Attributes)
                {
                    if (!string.Equals(field, CreatedAtField, StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (var (op, _) in condition.GetOperators())
                    {
                        switch (op)
                        {
                            case "gt":
                            case "ge":
                                hasLower = true;
                                break;
                            case "lt":
                            case "le":
                                hasUpper = true;
                                break;
                            case "between":
                                hasLower = true;
                                hasUpper = true;
                                break;
                        }
                    }
                }
                break;
            default:
                // Or / Not / Empty: createdAt bounds inside a disjunction or negation do not
                // guarantee narrowing, so they are intentionally not counted.
                break;
        }
    }
}
