using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Security;
using Npgsql;
using NpgsqlTypes;
namespace BBT.Workflow.Definitions;

/// <summary>Shared parameterized attribute predicates for legacy and GraphQL filters.</summary>
public static class AttributeConditionBuilder
{
    public static string BuildOperatorCondition(
        string field,
        string operatorType,
        object? value,
        string jsonColumnName,
        List<NpgsqlParameter> parameters,
        ref int parameterIndex,
        SchemaFilterContext? schemaContext = null)
    {
        SanitizeFieldName(field);
        InputValidator.ValidateSqlJsonColumnIdentifier(jsonColumnName);
        if (schemaContext != null && (!schemaContext.IsFieldFilterable(field) ||
            !schemaContext.IsOperatorAllowed(field, operatorType)))
            throw new BBT.Workflow.ExceptionHandling.SchemaFilterValidationException(
                $"Operator '{operatorType}' is not allowed for field '{field}'.");
        var sql = operatorType.ToLowerInvariant() switch
        {
            "eq" => BuildEqualsCondition(field, value, jsonColumnName, parameters, ref parameterIndex),
            "ne" => BuildNotEqualsCondition(field, value, jsonColumnName, parameters, ref parameterIndex),
            "gt" => BuildComparisonCondition(field, value, ">", jsonColumnName, parameters, ref parameterIndex, schemaContext),
            "ge" => BuildComparisonCondition(field, value, ">=", jsonColumnName, parameters, ref parameterIndex, schemaContext),
            "lt" => BuildComparisonCondition(field, value, "<", jsonColumnName, parameters, ref parameterIndex, schemaContext),
            "le" => BuildComparisonCondition(field, value, "<=", jsonColumnName, parameters, ref parameterIndex, schemaContext),
            "between" => BuildBetweenCondition(field, value, jsonColumnName, parameters, ref parameterIndex, schemaContext),
            "like" or "match" => BuildLikeCondition(field, value, jsonColumnName, parameters, ref parameterIndex),
            "startswith" => BuildStartsWithCondition(field, value, jsonColumnName, parameters, ref parameterIndex),
            "endswith" => BuildEndsWithCondition(field, value, jsonColumnName, parameters, ref parameterIndex),
            "in" => BuildInCondition(field, value, jsonColumnName, parameters, ref parameterIndex),
            "nin" => BuildNotInCondition(field, value, jsonColumnName, parameters, ref parameterIndex),
            "isnull" => BuildIsNullCondition(field, value, jsonColumnName),
            "includes" => BuildIncludesCondition(field, value, jsonColumnName, parameters, ref parameterIndex),
            _ => throw new ArgumentException($"Unsupported operator: {operatorType}")
        };
        var accessor = BuildJsonTextAccessor(field, jsonColumnName);
        var storage = operatorType.ToLowerInvariant() switch
        {
            "gt" or "ge" or "lt" or "le" or "between" =>
                ResolveFieldType(field, schemaContext) == "string" ? "timestamptz" : "numeric",
            "eq" or "ne" or "includes" => null,
            _ => "text"
        };
        if (storage != null && jsonColumnName == "Data")
        {
            var original = storage == "text" ? accessor : $"{accessor}::{storage}";
            sql = sql.Replace(original, AttributeSqlExpression.Resolve(field, storage, original, schemaContext), StringComparison.Ordinal);
        }
        return sql;
    }

    /// <summary>
    /// Builds <c>jsonb @&gt;</c> for "array at field path contains an element matching partial object".
    /// </summary>
    private static string BuildIncludesCondition(
        string field,
        object? value,
        string jsonColumnName,
        List<NpgsqlParameter> parameters,
        ref int parameterIndex)
    {
        InputValidator.ValidateSqlJsonColumnIdentifier(jsonColumnName);

        if (value is not JsonElement partial || partial.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("includes value must be a JSON object.");

        InputValidator.ValidateIncludesObject(partial);

        var parts = field.Split('.');
        if (parts.Length == 0)
            throw new ArgumentException("Field path cannot be empty.");

        JsonNode inner = JsonNode.Parse(partial.GetRawText())!;
        JsonNode current = new JsonArray(inner);
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            var wrap = new JsonObject { [parts[i]] = current };
            current = wrap;
        }

        var jsonText = current.ToJsonString(new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        if (jsonText.Length > InputValidator.MaxFilterLength)
            throw new ArgumentException($"includes pattern exceeds maximum length ({jsonText.Length} characters).");

        var idx = parameterIndex++;
        parameters.Add(new NpgsqlParameter { Value = jsonText, NpgsqlDbType = NpgsqlDbType.Jsonb });
        return $"\"{jsonColumnName}\" @> {{{idx}}}";
    }

    public static string SanitizeFieldName(string field)
    {
        // Validate field name with comprehensive checks
        InputValidator.ValidateFieldName(field);

        // Must start with a letter
        if (!char.IsLetter(field[0]))
        {
            throw new ArgumentException($"Invalid field name: {field}. Must start with a letter.");
        }

        // Only allow alphanumeric characters, dots, and underscores
        var regex = new System.Text.RegularExpressions.Regex(
            @"^[a-zA-Z][a-zA-Z0-9._]*$",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromMilliseconds(100));

        try
        {
            if (!regex.IsMatch(field))
            {
                throw new ArgumentException($"Invalid field name: {field}. Only alphanumeric, dots, and underscores allowed.");
            }
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            throw new ArgumentException($"Field name validation timeout: {field}");
        }

        return field;
    }

    private static bool IsNestedPath(string field) => field.Contains('.');

    public static string BuildJsonTextAccessor(string field, string jsonColumnName)
    {
        InputValidator.ValidateSqlJsonColumnIdentifier(jsonColumnName);
        if (IsNestedPath(field))
        {
            var parts = field.Split('.');
            var arrayElements = string.Join(",", parts.Select(p =>
                $"'{InputValidator.EscapePostgresSingleQuotedString(p)}'"));
            return $"(\"{jsonColumnName}\" #>> ARRAY[{arrayElements}])";
        }

        return $"(\"{jsonColumnName}\" ->> '{InputValidator.EscapePostgresSingleQuotedString(field)}')";
    }

    private static string BuildNestedJsonContainmentPattern(string field, object? value, bool isNumeric, bool isBoolean)
    {
        var parts = field.Split('.');
        var stringValue = ConvertToString(value);

        // Build the innermost value as proper object
        object innerValue;
        if (isBoolean && bool.TryParse(stringValue, out var boolVal))
        {
            innerValue = boolVal;
        }
        else if (isNumeric && decimal.TryParse(stringValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var numVal))
        {
            innerValue = numVal;
        }
        else
        {
            innerValue = stringValue; // JsonSerializer will properly escape strings
        }

        // Build nested object from inside out using Dictionary for proper JSON serialization
        object currentLevel = new Dictionary<string, object> { [parts[^1]] = innerValue };

        for (int i = parts.Length - 2; i >= 0; i--)
        {
            currentLevel = new Dictionary<string, object> { [parts[i]] = currentLevel };
        }

        // Use JsonSerializer for proper escaping and formatting
        return System.Text.Json.JsonSerializer.Serialize(currentLevel, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    private static string ConvertToString(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string s => s,
            bool b => b.ToString().ToLowerInvariant(),
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            double dbl => dbl.ToString(CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }

    /// <summary>
    /// Converts an operator value to the string form expected by
    /// <see cref="InstanceColumnConditionBuilder"/>. Array values (used by in/nin/between)
    /// are flattened to a comma-separated string by converting each element individually;
    /// scalar values fall back to <see cref="ConvertToString"/>.
    /// </summary>
    public static string ConvertOperatorValueToString(object? value)
    {
        if (value is object[] array)
        {
            return string.Join(",", array.Select(ConvertToString));
        }

        return ConvertToString(value);
    }

    private static string BuildEqualsCondition(
        string field, object? value, string jsonColumnName,
        List<NpgsqlParameter> parameters, ref int parameterIndex)
    {
        var stringValue = ConvertToString(value);
        var conditions = new List<string>();

        // String comparison
        var stringIndex = parameterIndex++;
        var stringJsonPattern = BuildNestedJsonContainmentPattern(field, value, isNumeric: false, isBoolean: false);
        parameters.Add(new NpgsqlParameter { Value = stringJsonPattern, NpgsqlDbType = NpgsqlDbType.Jsonb });
        conditions.Add($"\"{jsonColumnName}\" @> {{{stringIndex}}}");

        // Numeric comparison
        if (decimal.TryParse(stringValue, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        {
            var numIndex = parameterIndex++;
            var numJsonPattern = BuildNestedJsonContainmentPattern(field, value, isNumeric: true, isBoolean: false);
            parameters.Add(new NpgsqlParameter { Value = numJsonPattern, NpgsqlDbType = NpgsqlDbType.Jsonb });
            conditions.Add($"\"{jsonColumnName}\" @> {{{numIndex}}}");
        }

        // Boolean comparison
        if (bool.TryParse(stringValue, out _))
        {
            var boolIndex = parameterIndex++;
            var boolJsonPattern = BuildNestedJsonContainmentPattern(field, value, isNumeric: false, isBoolean: true);
            parameters.Add(new NpgsqlParameter { Value = boolJsonPattern, NpgsqlDbType = NpgsqlDbType.Jsonb });
            conditions.Add($"\"{jsonColumnName}\" @> {{{boolIndex}}}");
        }

        return $"({string.Join(" OR ", conditions)})";
    }

    private static string BuildNotEqualsCondition(
        string field, object? value, string jsonColumnName,
        List<NpgsqlParameter> parameters, ref int parameterIndex)
    {
        var stringValue = ConvertToString(value);
        var conditions = new List<string>();

        // String comparison
        var stringIndex = parameterIndex++;
        var stringJsonPattern = BuildNestedJsonContainmentPattern(field, value, isNumeric: false, isBoolean: false);
        parameters.Add(new NpgsqlParameter { Value = stringJsonPattern, NpgsqlDbType = NpgsqlDbType.Jsonb });
        conditions.Add($"NOT (\"{jsonColumnName}\" @> {{{stringIndex}}})");

        // Numeric comparison
        if (decimal.TryParse(stringValue, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        {
            var numIndex = parameterIndex++;
            var numJsonPattern = BuildNestedJsonContainmentPattern(field, value, isNumeric: true, isBoolean: false);
            parameters.Add(new NpgsqlParameter { Value = numJsonPattern, NpgsqlDbType = NpgsqlDbType.Jsonb });
            conditions.Add($"NOT (\"{jsonColumnName}\" @> {{{numIndex}}})");
        }

        // Boolean comparison
        if (bool.TryParse(stringValue, out _))
        {
            var boolIndex = parameterIndex++;
            var boolJsonPattern = BuildNestedJsonContainmentPattern(field, value, isNumeric: false, isBoolean: true);
            parameters.Add(new NpgsqlParameter { Value = boolJsonPattern, NpgsqlDbType = NpgsqlDbType.Jsonb });
            conditions.Add($"NOT (\"{jsonColumnName}\" @> {{{boolIndex}}})");
        }

        return $"({string.Join(" AND ", conditions)})";
    }

    /// <summary>
    /// Resolves the effective schema type for a field. When schema context is available,
    /// uses the declared type; otherwise falls back to "number" for backward compatibility.
    /// </summary>
    private static string ResolveFieldType(string field, SchemaFilterContext? schemaContext)
    {
        if (schemaContext == null || !schemaContext.EnforceFiltering)
            return "number";

        var metadata = schemaContext.GetFieldMetadata(field);
        return metadata?.Type ?? "number";
    }

    private static string BuildComparisonCondition(
        string field, object? value, string sqlOperator, string jsonColumnName,
        List<NpgsqlParameter> parameters, ref int parameterIndex,
        SchemaFilterContext? schemaContext = null)
    {
        var fieldType = ResolveFieldType(field, schemaContext);
        var accessor = BuildJsonTextAccessor(field, jsonColumnName);

        return fieldType switch
        {
            "number" or "integer" => BuildNumericCompare(accessor, value, sqlOperator, parameters, ref parameterIndex),
            "string" => BuildDateTimeCompare(accessor, value, sqlOperator, parameters, ref parameterIndex),
            _ => BuildNumericCompare(accessor, value, sqlOperator, parameters, ref parameterIndex)
        };
    }

    private static string BuildNumericCompare(
        string accessor, object? value, string sqlOperator,
        List<NpgsqlParameter> parameters, ref int parameterIndex)
    {
        var stringValue = ConvertToString(value);

        if (!decimal.TryParse(stringValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var numValue))
        {
            throw new ArgumentException($"Value '{stringValue}' is not numeric for comparison operator '{sqlOperator}'");
        }

        var paramIndex = parameterIndex++;
        parameters.Add(new NpgsqlParameter { Value = numValue });
        return $"{accessor}::numeric {sqlOperator} {{{paramIndex}}}";
    }

    private static string BuildDateTimeCompare(
        string accessor, object? value, string sqlOperator,
        List<NpgsqlParameter> parameters, ref int parameterIndex)
    {
        var stringValue = ConvertToString(value);

        if (!DateTime.TryParse(stringValue, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dateValue))
        {
            throw new ArgumentException($"Value '{stringValue}' is not a valid date/datetime for comparison operator '{sqlOperator}'");
        }

        var paramIndex = parameterIndex++;
        parameters.Add(new NpgsqlParameter { Value = dateValue, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        return $"{accessor}::timestamptz {sqlOperator} {{{paramIndex}}}";
    }

    private static string BuildBetweenCondition(
        string field, object? value, string jsonColumnName,
        List<NpgsqlParameter> parameters, ref int parameterIndex,
        SchemaFilterContext? schemaContext = null)
    {
        object[] values;
        if (value is object[] arr)
        {
            values = arr;
        }
        else
        {
            var stringValue = ConvertToString(value);
            values = stringValue.Split(',').Select(v => (object)v.Trim()).ToArray();
        }

        if (values.Length != 2)
            throw new ArgumentException($"Invalid between format. Expected 2 values, got {values.Length}");

        var fieldType = ResolveFieldType(field, schemaContext);
        var accessor = BuildJsonTextAccessor(field, jsonColumnName);

        return fieldType switch
        {
            "number" or "integer" => BuildNumericBetween(accessor, values, parameters, ref parameterIndex),
            "string" => BuildDateTimeBetween(accessor, values, parameters, ref parameterIndex),
            _ => BuildNumericBetween(accessor, values, parameters, ref parameterIndex)
        };
    }

    private static string BuildNumericBetween(
        string accessor, object[] values,
        List<NpgsqlParameter> parameters, ref int parameterIndex)
    {
        var minString = ConvertToString(values[0]);
        var maxString = ConvertToString(values[1]);

        if (!decimal.TryParse(minString, NumberStyles.Number, CultureInfo.InvariantCulture, out var minNum) ||
            !decimal.TryParse(maxString, NumberStyles.Number, CultureInfo.InvariantCulture, out var maxNum))
        {
            throw new ArgumentException($"Between values must be numeric: '{minString}', '{maxString}'");
        }

        var minIndex = parameterIndex++;
        var maxIndex = parameterIndex++;
        parameters.Add(new NpgsqlParameter { Value = minNum });
        parameters.Add(new NpgsqlParameter { Value = maxNum });
        return $"{accessor}::numeric BETWEEN {{{minIndex}}} AND {{{maxIndex}}}";
    }

    private static string BuildDateTimeBetween(
        string accessor, object[] values,
        List<NpgsqlParameter> parameters, ref int parameterIndex)
    {
        var minString = ConvertToString(values[0]);
        var maxString = ConvertToString(values[1]);

        if (!DateTime.TryParse(minString, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var minDate) ||
            !DateTime.TryParse(maxString, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var maxDate))
        {
            throw new ArgumentException($"Between values must be valid date/datetime: '{minString}', '{maxString}'");
        }

        var minIndex = parameterIndex++;
        var maxIndex = parameterIndex++;
        parameters.Add(new NpgsqlParameter { Value = minDate, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        parameters.Add(new NpgsqlParameter { Value = maxDate, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        return $"{accessor}::timestamptz BETWEEN {{{minIndex}}} AND {{{maxIndex}}}";
    }

    private static string BuildLikeCondition(
        string field, object? value, string jsonColumnName,
        List<NpgsqlParameter> parameters, ref int parameterIndex)
    {
        var stringValue = ConvertToString(value);
        var paramIndex = parameterIndex++;
        parameters.Add(new NpgsqlParameter { Value = $"%{stringValue}%" });

        var accessor = BuildJsonTextAccessor(field, jsonColumnName);
        return $"{accessor} COLLATE \"tr-TR-x-icu\" ILIKE {{{paramIndex}}}";
    }

    private static string BuildStartsWithCondition(
        string field, object? value, string jsonColumnName,
        List<NpgsqlParameter> parameters, ref int parameterIndex)
    {
        var stringValue = ConvertToString(value);
        var paramIndex = parameterIndex++;
        parameters.Add(new NpgsqlParameter { Value = $"{stringValue}%" });

        var accessor = BuildJsonTextAccessor(field, jsonColumnName);
        return $"{accessor} COLLATE \"tr-TR-x-icu\" ILIKE {{{paramIndex}}}";
    }

    private static string BuildEndsWithCondition(
        string field, object? value, string jsonColumnName,
        List<NpgsqlParameter> parameters, ref int parameterIndex)
    {
        var stringValue = ConvertToString(value);
        var paramIndex = parameterIndex++;
        parameters.Add(new NpgsqlParameter { Value = $"%{stringValue}" });

        var accessor = BuildJsonTextAccessor(field, jsonColumnName);
        return $"{accessor} COLLATE \"tr-TR-x-icu\" ILIKE {{{paramIndex}}}";
    }

    private static string BuildInCondition(
        string field, object? value, string jsonColumnName,
        List<NpgsqlParameter> parameters, ref int parameterIndex)
    {
        object[] values;
        if (value is object[] arr)
        {
            values = arr;
        }
        else
        {
            var stringValue = ConvertToString(value);
            values = stringValue.Split(',').Select(v => (object)v.Trim()).ToArray();
        }

        var paramPlaceholders = new List<string>();

        foreach (var val in values)
        {
            var paramIndex = parameterIndex++;
            parameters.Add(new NpgsqlParameter { Value = ConvertToString(val) });
            paramPlaceholders.Add($"{{{paramIndex}}}");
        }

        var accessor = BuildJsonTextAccessor(field, jsonColumnName);
        return $"{accessor} IN ({string.Join(", ", paramPlaceholders)})";
    }

    private static string BuildNotInCondition(
        string field, object? value, string jsonColumnName,
        List<NpgsqlParameter> parameters, ref int parameterIndex)
    {
        object[] values;
        if (value is object[] arr)
        {
            values = arr;
        }
        else
        {
            var stringValue = ConvertToString(value);
            values = stringValue.Split(',').Select(v => (object)v.Trim()).ToArray();
        }

        var paramPlaceholders = new List<string>();

        foreach (var val in values)
        {
            var paramIndex = parameterIndex++;
            parameters.Add(new NpgsqlParameter { Value = ConvertToString(val) });
            paramPlaceholders.Add($"{{{paramIndex}}}");
        }

        var accessor = BuildJsonTextAccessor(field, jsonColumnName);
        return $"{accessor} IS NOT NULL AND {accessor} NOT IN ({string.Join(", ", paramPlaceholders)})";
    }

    private static string BuildIsNullCondition(
        string field, object? value, string jsonColumnName)
    {
        var isNull = value switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => true
        };

        var accessor = BuildJsonTextAccessor(field, jsonColumnName);
        return isNull
            ? $"{accessor} IS NULL"
            : $"{accessor} IS NOT NULL";
    }
}
