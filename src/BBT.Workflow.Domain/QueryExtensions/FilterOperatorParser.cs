using System.Linq.Expressions;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace BBT.Workflow.Definitions;

public static class FilterOperatorParser
{
    private static readonly Regex OperatorRegex = new(
        @"^([^=]+)=(eq|ne|gt|ge|lt|le|between|match|like|startswith|endswith|in|nin):(.+)$", 
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));
    
    private static readonly Regex BetweenRegex = new(
        @"^(.+),(.+)$", 
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    public static (string Field, string Operator, string Value) ParseOperator(string input)
    {
        // Handle attributes= prefix - remove it since it's just indicating JSON data column
        if (input.StartsWith("attributes=", StringComparison.OrdinalIgnoreCase))
        {
            input = input.Substring("attributes=".Length);
        }

        try
        {
            var match = OperatorRegex.Match(input);
            if (!match.Success)
            {
                // Fallback to simple equals if no operator specified
                var simpleRegex = new Regex(@"^([^=]+)=(.+)$", RegexOptions.None, TimeSpan.FromMilliseconds(100));
                var simpleMatch = simpleRegex.Match(input);
                if (simpleMatch.Success)
                {
                    return (simpleMatch.Groups[1].Value.Trim(), "eq", simpleMatch.Groups[2].Value.Trim());
                }
                throw new ArgumentException($"Invalid filter format: {input}");
            }

            return (match.Groups[1].Value.Trim(), match.Groups[2].Value.ToLowerInvariant(), match.Groups[3].Value.Trim());
        }
        catch (RegexMatchTimeoutException)
        {
            throw new ArgumentException($"Filter parsing timeout (possible ReDoS attack): {input}");
        }
    }

    public static Expression<Func<T, bool>> CreateJsonPropertyExpression<T>(
        Expression<Func<T, string>> jsonSelector,
        string propertyName,
        string operatorType,
        string value)
    {
        var parameter = jsonSelector.Parameters[0];
        var jsonProperty = Expression.Call(
            typeof(EF),
            nameof(EF.Property),
            new[] { typeof(string) },
            jsonSelector.Body,
            Expression.Constant($"$.{propertyName}")
        );

        Expression condition = operatorType.ToLower() switch
        {
            "eq" => CreateEqualsExpression(jsonProperty, value),
            "ne" => Expression.Not(CreateEqualsExpression(jsonProperty, value)),
            "gt" => CreateComparisonExpression(jsonProperty, value, ExpressionType.GreaterThan),
            "ge" => CreateComparisonExpression(jsonProperty, value, ExpressionType.GreaterThanOrEqual),
            "lt" => CreateComparisonExpression(jsonProperty, value, ExpressionType.LessThan),
            "le" => CreateComparisonExpression(jsonProperty, value, ExpressionType.LessThanOrEqual),
            "between" => CreateBetweenExpression(jsonProperty, value),
            "match" or "like" => CreateContainsExpression(jsonProperty, value),
            "startswith" => CreateStartsWithExpression(jsonProperty, value),
            "endswith" => CreateEndsWithExpression(jsonProperty, value),
            "in" => CreateInExpression(jsonProperty, value),
            "nin" => Expression.Not(CreateInExpression(jsonProperty, value)),
            _ => throw new ArgumentException($"Unsupported operator: {operatorType}")
        };

        return Expression.Lambda<Func<T, bool>>(condition, parameter);
    }

    private static Expression CreateEqualsExpression(Expression jsonProperty, string value)
    {
        if (bool.TryParse(value, out var boolValue))
        {
            return Expression.Equal(
                Expression.Call(jsonProperty, typeof(string).GetMethod("ToLower")!),
                Expression.Constant(boolValue.ToString().ToLower())
            );
        }

        if (int.TryParse(value, out var intValue))
        {
            return Expression.Equal(
                Expression.Call(typeof(int), "Parse", null, jsonProperty),
                Expression.Constant(intValue)
            );
        }

        if (decimal.TryParse(value, out var decimalValue))
        {
            return Expression.Equal(
                Expression.Call(typeof(decimal), "Parse", null, jsonProperty),
                Expression.Constant(decimalValue)
            );
        }

        return Expression.Equal(jsonProperty, Expression.Constant(value));
    }

    private static Expression CreateComparisonExpression(Expression jsonProperty, string value, ExpressionType comparison)
    {
        if (int.TryParse(value, out var intValue))
        {
            return Expression.MakeBinary(
                comparison,
                Expression.Call(typeof(int), "Parse", null, jsonProperty),
                Expression.Constant(intValue)
            );
        }

        if (decimal.TryParse(value, out var decimalValue))
        {
            return Expression.MakeBinary(
                comparison,
                Expression.Call(typeof(decimal), "Parse", null, jsonProperty),
                Expression.Constant(decimalValue)
            );
        }

        if (DateTime.TryParse(value, out var dateValue))
        {
            return Expression.MakeBinary(
                comparison,
                Expression.Call(typeof(DateTime), "Parse", null, jsonProperty),
                Expression.Constant(dateValue)
            );
        }

        // String comparison
        return Expression.MakeBinary(
            comparison,
            jsonProperty,
            Expression.Constant(value)
        );
    }

    private static Expression CreateBetweenExpression(Expression jsonProperty, string value)
    {
        var match = BetweenRegex.Match(value);
        if (!match.Success)
            throw new ArgumentException($"Invalid between format: {value}. Expected format: 'min,max'");

        var minValue = match.Groups[1].Value.Trim();
        var maxValue = match.Groups[2].Value.Trim();

        var greaterThanOrEqual = CreateComparisonExpression(jsonProperty, minValue, ExpressionType.GreaterThanOrEqual);
        var lessThanOrEqual = CreateComparisonExpression(jsonProperty, maxValue, ExpressionType.LessThanOrEqual);

        return Expression.AndAlso(greaterThanOrEqual, lessThanOrEqual);
    }

    private static Expression CreateContainsExpression(Expression jsonProperty, string value)
    {
        var containsMethod = typeof(string).GetMethod("Contains", new[] { typeof(string) })!;
        return Expression.Call(jsonProperty, containsMethod, Expression.Constant(value));
    }

    private static Expression CreateStartsWithExpression(Expression jsonProperty, string value)
    {
        var startsWithMethod = typeof(string).GetMethod("StartsWith", new[] { typeof(string) })!;
        return Expression.Call(jsonProperty, startsWithMethod, Expression.Constant(value));
    }

    private static Expression CreateEndsWithExpression(Expression jsonProperty, string value)
    {
        var endsWithMethod = typeof(string).GetMethod("EndsWith", new[] { typeof(string) })!;
        return Expression.Call(jsonProperty, endsWithMethod, Expression.Constant(value));
    }

    private static Expression CreateInExpression(Expression jsonProperty, string value)
    {
        var values = value.Split(',').Select(v => v.Trim()).ToArray();
        var valuesConstant = Expression.Constant(values);
        var containsMethod = typeof(Enumerable).GetMethods()
            .First(m => m.Name == "Contains" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(string));

        return Expression.Call(containsMethod, valuesConstant, jsonProperty);
    }

    public static Expression<Func<T, bool>> CreateSimplePropertyExpression<T>(
        Expression<Func<T, object>> propertySelector,
        string operatorType,
        string value)
    {
        var parameter = propertySelector.Parameters[0];
        var property = propertySelector.Body;

        // Handle boxing/unboxing for value types
        if (property is UnaryExpression unary && unary.NodeType == ExpressionType.Convert)
        {
            property = unary.Operand;
        }

        Expression condition = operatorType.ToLower() switch
        {
            "eq" => CreateSimpleEqualsExpression(property, value),
            "ne" => Expression.Not(CreateSimpleEqualsExpression(property, value)),
            "gt" => CreateSimpleComparisonExpression(property, value, ExpressionType.GreaterThan),
            "ge" => CreateSimpleComparisonExpression(property, value, ExpressionType.GreaterThanOrEqual),
            "lt" => CreateSimpleComparisonExpression(property, value, ExpressionType.LessThan),
            "le" => CreateSimpleComparisonExpression(property, value, ExpressionType.LessThanOrEqual),
            "between" => CreateSimpleBetweenExpression(property, value),
            "match" or "like" => CreateSimpleContainsExpression(property, value),
            "startswith" => CreateSimpleStartsWithExpression(property, value),
            "endswith" => CreateSimpleEndsWithExpression(property, value),
            "in" => CreateSimpleInExpression(property, value),
            "nin" => Expression.Not(CreateSimpleInExpression(property, value)),
            _ => throw new ArgumentException($"Unsupported operator: {operatorType}")
        };

        return Expression.Lambda<Func<T, bool>>(condition, parameter);
    }

    private static Expression CreateSimpleEqualsExpression(Expression property, string value)
    {
        var propertyType = property.Type;
        var convertedValue = ConvertValueToType(value, propertyType);
        return Expression.Equal(property, Expression.Constant(convertedValue, propertyType));
    }

    private static Expression CreateSimpleComparisonExpression(Expression property, string value, ExpressionType comparison)
    {
        var propertyType = property.Type;
        var convertedValue = ConvertValueToType(value, propertyType);
        return Expression.MakeBinary(comparison, property, Expression.Constant(convertedValue, propertyType));
    }

    private static Expression CreateSimpleBetweenExpression(Expression property, string value)
    {
        var match = BetweenRegex.Match(value);
        if (!match.Success)
            throw new ArgumentException($"Invalid between format: {value}. Expected format: 'min,max'");

        var minValue = match.Groups[1].Value.Trim();
        var maxValue = match.Groups[2].Value.Trim();

        var greaterThanOrEqual = CreateSimpleComparisonExpression(property, minValue, ExpressionType.GreaterThanOrEqual);
        var lessThanOrEqual = CreateSimpleComparisonExpression(property, maxValue, ExpressionType.LessThanOrEqual);

        return Expression.AndAlso(greaterThanOrEqual, lessThanOrEqual);
    }

    private static Expression CreateSimpleContainsExpression(Expression property, string value)
    {
        if (property.Type != typeof(string))
            throw new ArgumentException("Contains operation is only supported for string properties");

        var containsMethod = typeof(string).GetMethod("Contains", new[] { typeof(string) })!;
        return Expression.Call(property, containsMethod, Expression.Constant(value));
    }

    private static Expression CreateSimpleStartsWithExpression(Expression property, string value)
    {
        if (property.Type != typeof(string))
            throw new ArgumentException("StartsWith operation is only supported for string properties");

        var startsWithMethod = typeof(string).GetMethod("StartsWith", new[] { typeof(string) })!;
        return Expression.Call(property, startsWithMethod, Expression.Constant(value));
    }

    private static Expression CreateSimpleEndsWithExpression(Expression property, string value)
    {
        if (property.Type != typeof(string))
            throw new ArgumentException("EndsWith operation is only supported for string properties");

        var endsWithMethod = typeof(string).GetMethod("EndsWith", new[] { typeof(string) })!;
        return Expression.Call(property, endsWithMethod, Expression.Constant(value));
    }

    private static Expression CreateSimpleInExpression(Expression property, string value)
    {
        var propertyType = property.Type;
        var values = value.Split(',').Select(v => ConvertValueToType(v.Trim(), propertyType)).ToArray();
        
        var arrayType = propertyType.MakeArrayType();
        var valuesConstant = Expression.Constant(Array.CreateInstance(propertyType, values.Length), arrayType);
        
        // Initialize the array
        var arrayInit = Expression.NewArrayInit(propertyType, values.Select(v => Expression.Constant(v, propertyType)));
        
        var containsMethod = typeof(Enumerable).GetMethods()
            .First(m => m.Name == "Contains" && m.GetParameters().Length == 2)
            .MakeGenericMethod(propertyType);

        return Expression.Call(containsMethod, arrayInit, property);
    }

    private static object ConvertValueToType(string value, Type targetType)
    {
        // Handle nullable types
        if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            if (string.IsNullOrEmpty(value) || value.Equals("null", StringComparison.OrdinalIgnoreCase))
                return null!;
            
            targetType = Nullable.GetUnderlyingType(targetType)!;
        }

        if (targetType == typeof(string))
            return value;

        if (targetType == typeof(int))
            return int.Parse(value);

        if (targetType == typeof(long))
            return long.Parse(value);

        if (targetType == typeof(decimal))
            return decimal.Parse(value);

        if (targetType == typeof(double))
            return double.Parse(value);

        if (targetType == typeof(float))
            return float.Parse(value);

        if (targetType == typeof(bool))
            return bool.Parse(value);

        if (targetType == typeof(DateTime))
            return DateTime.Parse(value);

        if (targetType == typeof(Guid))
            return Guid.Parse(value);

        if (targetType.IsEnum)
            return Enum.Parse(targetType, value, true);

        throw new ArgumentException($"Unsupported type conversion to {targetType.Name}");
    }
} 