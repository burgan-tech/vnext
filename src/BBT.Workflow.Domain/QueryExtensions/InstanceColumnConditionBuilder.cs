using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Builds PostgreSQL WHERE conditions for Instance table columns
/// Supports all filter operators with proper type handling and SQL injection prevention
/// </summary>
public static class InstanceColumnConditionBuilder
{
    /// <summary>
    /// Build PostgreSQL WHERE condition for an Instance table column
    /// </summary>
    /// <param name="columnName">Instance column name (e.g., "Key", "Status", "CreatedAt")</param>
    /// <param name="operatorType">Filter operator (eq, ne, gt, ge, lt, le, between, like, startswith, endswith, in, nin)</param>
    /// <param name="value">Filter value (string representation)</param>
    /// <param name="parameterIndex">Parameter index counter (ref for auto-increment)</param>
    /// <returns>Tuple of (SQL condition string, list of NpgsqlParameters)</returns>
    public static (string condition, List<NpgsqlParameter> parameters) BuildCondition(
        string columnName,
        string operatorType,
        string value,
        ref int parameterIndex)
    {
        // Validate column name against whitelist
        if (!InstanceFieldDiscriminator.IsInstanceColumn(columnName))
        {
            throw new ArgumentException($"Invalid Instance column name: {columnName}", nameof(columnName));
        }

        // Get properly cased column name
        var properColumnName = InstanceFieldDiscriminator.GetInstanceColumnName(columnName);

        // Special handling for Status column - resolve names to codes
        if (properColumnName.Equals("Status", StringComparison.OrdinalIgnoreCase))
        {
            return BuildStatusCondition(properColumnName, operatorType, value, ref parameterIndex);
        }

        // Route to appropriate condition builder based on operator
        return operatorType.ToLowerInvariant() switch
        {
            "eq" => BuildEqualsCondition(properColumnName, value, ref parameterIndex),
            "ne" => BuildNotEqualsCondition(properColumnName, value, ref parameterIndex),
            "gt" => BuildComparisonCondition(properColumnName, value, ">", ref parameterIndex),
            "ge" => BuildComparisonCondition(properColumnName, value, ">=", ref parameterIndex),
            "lt" => BuildComparisonCondition(properColumnName, value, "<", ref parameterIndex),
            "le" => BuildComparisonCondition(properColumnName, value, "<=", ref parameterIndex),
            "between" => BuildBetweenCondition(properColumnName, value, ref parameterIndex),
            "like" or "match" => BuildLikeCondition(properColumnName, value, ref parameterIndex),
            "startswith" => BuildStartsWithCondition(properColumnName, value, ref parameterIndex),
            "endswith" => BuildEndsWithCondition(properColumnName, value, ref parameterIndex),
            "in" => BuildInCondition(properColumnName, value, ref parameterIndex),
            "nin" => BuildNotInCondition(properColumnName, value, ref parameterIndex),
            _ => throw new ArgumentException($"Unsupported operator: {operatorType}", nameof(operatorType))
        };
    }

    /// <summary>
    /// Build equals condition with type inference
    /// </summary>
    private static (string, List<NpgsqlParameter>) BuildEqualsCondition(
        string columnName, string value, ref int parameterIndex)
    {
        var parameters = new List<NpgsqlParameter>();
        var paramIndex = parameterIndex++;

        var columnType = GetColumnType(columnName);
        var parameter = CreateTypedParameter(value, columnType);
        parameters.Add(parameter);

        var condition = $"s.\"{columnName}\" = {{{paramIndex}}}";
        return (condition, parameters);
    }

    /// <summary>
    /// Build not equals condition
    /// </summary>
    private static (string, List<NpgsqlParameter>) BuildNotEqualsCondition(
        string columnName, string value, ref int parameterIndex)
    {
        var parameters = new List<NpgsqlParameter>();
        var paramIndex = parameterIndex++;

        var columnType = GetColumnType(columnName);
        var parameter = CreateTypedParameter(value, columnType);
        parameters.Add(parameter);

        var condition = $"s.\"{columnName}\" != {{{paramIndex}}}";
        return (condition, parameters);
    }

    /// <summary>
    /// Build comparison condition (>, >=, <, <=)
    /// </summary>
    private static (string, List<NpgsqlParameter>) BuildComparisonCondition(
        string columnName, string value, string sqlOperator, ref int parameterIndex)
    {
        var parameters = new List<NpgsqlParameter>();
        var paramIndex = parameterIndex++;

        var columnType = GetColumnType(columnName);
        
        // Validate that column type supports comparison
        if (columnType == ColumnType.Boolean)
        {
            throw new ArgumentException($"Column '{columnName}' of type Boolean does not support comparison operator '{sqlOperator}'");
        }

        var parameter = CreateTypedParameter(value, columnType);
        parameters.Add(parameter);

        var condition = $"s.\"{columnName}\" {sqlOperator} {{{paramIndex}}}";
        return (condition, parameters);
    }

    /// <summary>
    /// Build BETWEEN condition (for DateTime and numeric types)
    /// </summary>
    private static (string, List<NpgsqlParameter>) BuildBetweenCondition(
        string columnName, string value, ref int parameterIndex)
    {
        var parts = value.Split(',');
        if (parts.Length != 2)
            throw new ArgumentException($"Invalid BETWEEN format: {value}. Expected format: 'min,max'");

        var minValue = parts[0].Trim();
        var maxValue = parts[1].Trim();

        var parameters = new List<NpgsqlParameter>();
        var minIndex = parameterIndex++;
        var maxIndex = parameterIndex++;

        var columnType = GetColumnType(columnName);
        
        // Validate that column type supports BETWEEN
        if (columnType == ColumnType.Boolean)
        {
            throw new ArgumentException($"Column '{columnName}' of type Boolean does not support BETWEEN operator");
        }

        var minParam = CreateTypedParameter(minValue, columnType);
        var maxParam = CreateTypedParameter(maxValue, columnType);
        parameters.Add(minParam);
        parameters.Add(maxParam);

        var condition = $"s.\"{columnName}\" BETWEEN {{{minIndex}}} AND {{{maxIndex}}}";
        return (condition, parameters);
    }

    /// <summary>
    /// Escape LIKE pattern wildcards (% and _) in user input
    /// </summary>
    private static string EscapeLikePattern(string value)
    {
        return value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    }

    /// <summary>
    /// Build LIKE condition (case-insensitive)
    /// </summary>
    private static (string, List<NpgsqlParameter>) BuildLikeCondition(
        string columnName, string value, ref int parameterIndex)
    {
        var parameters = new List<NpgsqlParameter>();
        var paramIndex = parameterIndex++;

        var escapedValue = EscapeLikePattern(value);
        parameters.Add(new NpgsqlParameter { Value = $"%{escapedValue}%" });

        var condition = $"s.\"{columnName}\" ILIKE {{{paramIndex}}}";
        return (condition, parameters);
    }

    /// <summary>
    /// Build STARTSWITH condition
    /// </summary>
    private static (string, List<NpgsqlParameter>) BuildStartsWithCondition(
        string columnName, string value, ref int parameterIndex)
    {
        var parameters = new List<NpgsqlParameter>();
        var paramIndex = parameterIndex++;

        var escapedValue = EscapeLikePattern(value);
        parameters.Add(new NpgsqlParameter { Value = $"{escapedValue}%" });

        var condition = $"s.\"{columnName}\" ILIKE {{{paramIndex}}}";
        return (condition, parameters);
    }

    /// <summary>
    /// Build ENDSWITH condition
    /// </summary>
    private static (string, List<NpgsqlParameter>) BuildEndsWithCondition(
        string columnName, string value, ref int parameterIndex)
    {
        var parameters = new List<NpgsqlParameter>();
        var paramIndex = parameterIndex++;

        var escapedValue = EscapeLikePattern(value);
        parameters.Add(new NpgsqlParameter { Value = $"%{escapedValue}" });

        var condition = $"s.\"{columnName}\" ILIKE {{{paramIndex}}}";
        return (condition, parameters);
    }

    /// <summary>
    /// Build IN condition
    /// </summary>
    private static (string, List<NpgsqlParameter>) BuildInCondition(
        string columnName, string value, ref int parameterIndex)
    {
        var values = value.Split(',').Select(v => v.Trim()).ToArray();
        var parameters = new List<NpgsqlParameter>();
        var paramPlaceholders = new List<string>();

        var columnType = GetColumnType(columnName);

        foreach (var val in values)
        {
            var paramIndex = parameterIndex++;
            var parameter = CreateTypedParameter(val, columnType);
            parameters.Add(parameter);
            paramPlaceholders.Add($"{{{paramIndex}}}");
        }

        var condition = $"s.\"{columnName}\" IN ({string.Join(", ", paramPlaceholders)})";
        return (condition, parameters);
    }

    /// <summary>
    /// Build NOT IN condition
    /// </summary>
    private static (string, List<NpgsqlParameter>) BuildNotInCondition(
        string columnName, string value, ref int parameterIndex)
    {
        var values = value.Split(',').Select(v => v.Trim()).ToArray();
        var parameters = new List<NpgsqlParameter>();
        var paramPlaceholders = new List<string>();

        var columnType = GetColumnType(columnName);

        foreach (var val in values)
        {
            var paramIndex = parameterIndex++;
            var parameter = CreateTypedParameter(val, columnType);
            parameters.Add(parameter);
            paramPlaceholders.Add($"{{{paramIndex}}}");
        }

        var condition = $"s.\"{columnName}\" NOT IN ({string.Join(", ", paramPlaceholders)})";
        return (condition, parameters);
    }

    /// <summary>
    /// Build Status column condition with name-to-code resolution
    /// </summary>
    private static (string, List<NpgsqlParameter>) BuildStatusCondition(
        string columnName, string operatorType, string value, ref int parameterIndex)
    {
        // Resolve status name to code
        var resolvedValue = operatorType.ToLowerInvariant() switch
        {
            "in" or "nin" => string.Join(",", InstanceFieldDiscriminator.ResolveStatusValues(
                value.Split(',').Select(v => v.Trim()).ToArray())),
            _ => InstanceFieldDiscriminator.ResolveStatusValue(value)
        };

        // Build condition with resolved value
        return operatorType.ToLowerInvariant() switch
        {
            "eq" => BuildEqualsCondition(columnName, resolvedValue, ref parameterIndex),
            "ne" => BuildNotEqualsCondition(columnName, resolvedValue, ref parameterIndex),
            "in" => BuildInCondition(columnName, resolvedValue, ref parameterIndex),
            "nin" => BuildNotInCondition(columnName, resolvedValue, ref parameterIndex),
            _ => throw new ArgumentException($"Status column does not support operator: {operatorType}")
        };
    }

    /// <summary>
    /// Get the data type of an Instance column
    /// </summary>
    private static ColumnType GetColumnType(string columnName)
    {
        return columnName switch
        {
            "Key" => ColumnType.String,
            "Flow" => ColumnType.String,
            "CurrentState" => ColumnType.String,
            "EffectiveState" => ColumnType.String,
            "EffectiveStateType" => ColumnType.Integer,
            "EffectiveStateSubType" => ColumnType.Integer,
            "Status" => ColumnType.String,
            "CreatedAt" => ColumnType.DateTime,
            "ModifiedAt" => ColumnType.DateTime,
            "CompletedAt" => ColumnType.DateTime,
            "IsTransient" => ColumnType.Boolean,
            _ => ColumnType.String
        };
    }

    /// <summary>
    /// Create a typed NpgsqlParameter based on column type
    /// </summary>
    private static NpgsqlParameter CreateTypedParameter(string value, ColumnType columnType)
    {
        return columnType switch
        {
            ColumnType.String => new NpgsqlParameter { Value = value },
            ColumnType.DateTime => CreateDateTimeParameter(value),
            ColumnType.Boolean => CreateBooleanParameter(value),
            ColumnType.Integer => CreateIntegerParameter(value),
            _ => new NpgsqlParameter { Value = value }
        };
    }

    /// <summary>
    /// Create DateTime parameter with proper parsing
    /// </summary>
    private static NpgsqlParameter CreateDateTimeParameter(string value)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dateValue))
        {
            return new NpgsqlParameter { Value = dateValue, NpgsqlDbType = NpgsqlDbType.TimestampTz };
        }

        throw new ArgumentException($"Invalid DateTime value: {value}");
    }

    /// <summary>
    /// Create Boolean parameter with proper parsing
    /// </summary>
    private static NpgsqlParameter CreateBooleanParameter(string value)
    {
        if (bool.TryParse(value, out var boolValue))
        {
            return new NpgsqlParameter { Value = boolValue, NpgsqlDbType = NpgsqlDbType.Boolean };
        }

        throw new ArgumentException($"Invalid Boolean value: {value}");
    }
    
    /// <summary>
    /// Create Integer parameter with proper parsing
    /// </summary>
    private static NpgsqlParameter CreateIntegerParameter(string value)
    {
        if (int.TryParse(value, out var intValue))
        {
            return new NpgsqlParameter { Value = intValue, NpgsqlDbType = NpgsqlDbType.Integer };
        }

        throw new ArgumentException($"Invalid Integer value: {value}");
    }

    /// <summary>
    /// Column data type enumeration
    /// </summary>
    private enum ColumnType
    {
        String,
        DateTime,
        Boolean,
        Integer
    }
}

