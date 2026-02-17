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
    /// Applies ordering to the instance query. Instance columns are applied via EF;
    /// attributes.* sort keys are skipped (JSON ordering not yet implemented).
    /// </summary>
    public static IQueryable<Instance> Apply(IQueryable<Instance> query, OrderByRequest? orderBy)
    {
        if (orderBy == null)
            return query;

        var entries = orderBy.GetEntries();
        if (entries.Count == 0)
            return query;

        var index = 0;
        foreach (var (field, direction) in entries)
        {
            var isDesc = direction.Equals("desc", StringComparison.OrdinalIgnoreCase);
            bool applied;
            if (index == 0)
            {
                applied = ApplyFirstOrderBy(query, field, isDesc, out var ordered);
                if (applied && ordered != null)
                    query = ordered;
            }
            else if (query is IOrderedQueryable<Instance> orderedQuery)
            {
                applied = ApplyThenBy(orderedQuery, field, isDesc, out var nextOrdered);
                if (applied && nextOrdered != null)
                    query = nextOrdered;
            }

            index++;
        }

        return query;
    }

    private static bool ApplyFirstOrderBy(IQueryable<Instance> query, string field, bool descending, out IQueryable<Instance>? result)
    {
        result = null;
        var lambda = GetTypedSelector(field);
        if (lambda == null)
            return false;

        var ordered = CallOrderBy(query, lambda, descending, isThenBy: false);
        if (ordered == null)
            return false;

        result = ordered;
        return true;
    }

    private static bool ApplyThenBy(IOrderedQueryable<Instance> query, string field, bool descending, out IQueryable<Instance>? result)
    {
        result = null;
        var lambda = GetTypedSelector(field);
        if (lambda == null)
            return false;

        var ordered = CallOrderBy(query, lambda, descending, isThenBy: true);
        if (ordered == null)
            return false;

        result = ordered;
        return true;
    }

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
    /// Returns a typed LambdaExpression for the given instance column, or null if not supported.
    /// </summary>
    private static LambdaExpression? GetTypedSelector(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            return null;

        var trimmed = fieldName.Trim();
        if (trimmed.StartsWith("attributes.", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!InstanceFieldDiscriminator.IsInstanceColumn(trimmed))
            return null;

        string columnName;
        try
        {
            columnName = InstanceFieldDiscriminator.GetInstanceColumnName(trimmed);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var parameter = Expression.Parameter(typeof(Instance), "i");
        var property = Expression.Property(parameter, columnName);
        return Expression.Lambda(property, parameter);
    }
}
