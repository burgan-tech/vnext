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

    public Expression<Func<T, bool>> ToExpression()
    {
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
            catch
            {
                // If not JSON, try to parse as key=value using regex
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

        return Expression.Lambda<Func<T, bool>>(
            combinedExpression ?? Expression.Constant(true),
            parameter);
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