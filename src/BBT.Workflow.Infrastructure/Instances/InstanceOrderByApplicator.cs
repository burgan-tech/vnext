using System.Linq.Expressions;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.GraphQL;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Instances;

/// <summary>
/// Applies OrderByRequest to IQueryable&lt;Instance&gt; for instance list sorting.
/// Supports instance columns (createdAt, modifiedAt, status, key, currentState, etc.) and
/// attributes JSON path (attributes.fieldName) when query allows it.
/// </summary>
public static class InstanceOrderByApplicator
{
    /// <summary>
    /// Applies ordering to the instance query. Instance columns are applied via EF; <c>attributes.*</c>
    /// sort keys are routed to the raw-SQL path by the caller and skipped here.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A sort key is neither an <c>attributes.*</c> path nor a known instance column. Skipping it
    /// would return results in an order the caller never asked for, with an HTTP 200.
    /// </exception>
    public static IQueryable<Instance> Apply(IQueryable<Instance> query, OrderByRequest? orderBy)
    {
        if (orderBy == null)
            return query;

        var entries = orderBy.GetEntries();
        if (entries.Count == 0)
            return query;

        // Count what actually landed rather than the loop position: keying off the position meant a
        // sort key that failed to apply shifted the next one into the ThenBy branch, which needs an
        // IOrderedQueryable that does not exist yet, so that key was silently dropped too.
        var applied = 0;
        foreach (var (field, direction) in entries)
        {
            if (IsAttributesPath(field))
                continue;

            var isDesc = direction.Equals("desc", StringComparison.OrdinalIgnoreCase);
            var lambda = GetTypedSelector(field);

            var ordered = CallOrderBy(query, lambda, isDesc, isThenBy: applied > 0)
                ?? throw new ArgumentException($"Could not apply ordering by '{field}'.", nameof(orderBy));

            query = ordered;
            applied++;
        }

        return query;
    }

    private static bool IsAttributesPath(string field) =>
        field.Trim().StartsWith("attributes.", StringComparison.OrdinalIgnoreCase);

    private static IQueryable<Instance>? CallOrderBy(IQueryable<Instance> query, LambdaExpression keySelector, bool descending, bool isThenBy)
    {
        var keyType = keySelector.ReturnType;
        var methodName = isThenBy
            ? (descending ? "ThenByDescending" : "ThenBy")
            : (descending ? "OrderByDescending" : "OrderBy");
        var methods = typeof(Queryable).GetMethods()
            .Where(m => m.Name == methodName && m.GetParameters().Length == 2)
            .Where(m => m.GetParameters()[1].ParameterType.IsGenericType
                && m.GetParameters()[1].ParameterType.GetGenericTypeDefinition() == typeof(Expression<>))
            .ToList();
        var method = methods.FirstOrDefault(m => m.GetGenericArguments().Length == 2);
        if (method == null)
            return null;
        var genericMethod = method.MakeGenericMethod(typeof(Instance), keyType);
        return genericMethod.Invoke(null, [query, keySelector]) as IQueryable<Instance>;
    }

    /// <summary>
    /// Returns a typed LambdaExpression for the given instance column.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The field is blank or is not a known instance column. Callers must route
    /// <c>attributes.*</c> paths elsewhere before reaching here.
    /// </exception>
    private static LambdaExpression GetTypedSelector(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            throw new ArgumentException("Sort field cannot be empty.", nameof(fieldName));

        var trimmed = fieldName.Trim();

        // Resolving the column name is the validation: GetInstanceColumnName throws for anything
        // outside the known set, which is exactly the signal we want to propagate.
        var columnName = InstanceFieldDiscriminator.GetInstanceColumnName(trimmed);

        var parameter = Expression.Parameter(typeof(Instance), "i");
        var property = Expression.Property(parameter, columnName);
        return Expression.Lambda(property, parameter);
    }
}
