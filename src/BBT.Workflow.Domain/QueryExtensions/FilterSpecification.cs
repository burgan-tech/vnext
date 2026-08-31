using System.Linq.Expressions;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BBT.Workflow.Definitions;

public interface IFilterSpecification<T>
{
    Expression<Func<T, bool>> ToExpression();
    IQueryable<T> Apply(IQueryable<T> query);
}

public class FilterSpecification<T> : IFilterSpecification<T>
{
    private readonly string[]? _filters;
    private readonly Dictionary<string, Func<string, Expression<Func<T, bool>>>> _filterMappings;
    protected static readonly Regex KeyValueRegex = new(@"^\s*([^=]+?)\s*=\s*(.+?)\s*$", RegexOptions.Compiled);

    public FilterSpecification(
        string? filter,
        Dictionary<string, Func<string, Expression<Func<T, bool>>>> filterMappings)
    {
        _filters = string.IsNullOrWhiteSpace(filter) ? null : new[] { filter };
        _filterMappings = filterMappings;
    }

    /// <summary>
    /// Builds the predicate for the configured filter.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A non-empty filter matched none of the configured mappings. Returning <c>x =&gt; true</c> in
    /// that case would turn a filtered query into an unfiltered one — the caller would receive every
    /// row and no indication that their filter was discarded.
    /// </exception>
    public Expression<Func<T, bool>> ToExpression()
    {
        // No filter legitimately means no restriction.
        if (_filters == null || !_filters.Any())
            return x => true;

        var parameter = Expression.Parameter(typeof(T), "x");
        Expression? combinedExpression = null;

        foreach (var filter in _filters)
        {
            try
            {
                // Try to parse as JSON first
                var jsonElement = JsonSerializer.Deserialize<JsonElement>(filter);
                if (jsonElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in jsonElement.EnumerateObject())
                    {
                        var filterMapping = _filterMappings.FirstOrDefault(x => 
                            string.Equals(x.Key, property.Name, StringComparison.OrdinalIgnoreCase));

                        if (filterMapping.Key != null)
                        {
                            var expression = filterMapping.Value(property.Value.ToString()!);
                            var body = expression.Body;
                            
                            var visitor = new ParameterReplacer(expression.Parameters[0], parameter);
                            var newBody = visitor.Visit(body);

                            if (combinedExpression == null)
                            {
                                combinedExpression = newBody;
                            }
                            else
                            {
                                combinedExpression = Expression.AndAlso(combinedExpression, newBody);
                            }
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Not JSON, so try to parse as key=value using regex
                var match = KeyValueRegex.Match(filter);
                if (!match.Success)
                    continue;

                var propertyName = match.Groups[1].Value;
                var value = match.Groups[2].Value;

                var filterMapping = _filterMappings.FirstOrDefault(x => 
                    string.Equals(x.Key, Regex.Match(propertyName, @"^([^.]+)").Groups[1].Value, StringComparison.OrdinalIgnoreCase));

                if (filterMapping.Key != null)
                {
                    var expression = filterMapping.Value(value);
                    var body = expression.Body;
                    
                    var visitor = new ParameterReplacer(expression.Parameters[0], parameter);
                    var newBody = visitor.Visit(body);

                    if (combinedExpression == null)
                    {
                        combinedExpression = newBody;
                    }
                    else
                    {
                        combinedExpression = Expression.AndAlso(combinedExpression, newBody);
                    }
                }
            }
        }

        if (combinedExpression == null)
        {
            throw new ArgumentException(
                "Filter matched none of the supported properties and would apply no restriction. " +
                $"Supported properties: {string.Join(", ", _filterMappings.Keys)}.");
        }

        return Expression.Lambda<Func<T, bool>>(combinedExpression, parameter);
    }

    public IQueryable<T> Apply(IQueryable<T> query)
    {
        return query.Where(ToExpression());
    }

    private class ParameterReplacer : ExpressionVisitor
    {
        private readonly ParameterExpression _oldParameter;
        private readonly ParameterExpression _newParameter;

        public ParameterReplacer(ParameterExpression oldParameter, ParameterExpression newParameter)
        {
            _oldParameter = oldParameter;
            _newParameter = newParameter;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            return node == _oldParameter ? _newParameter : base.VisitParameter(node);
        }
    }
} 