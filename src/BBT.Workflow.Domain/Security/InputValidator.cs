using System;
using System.Text.Json;

namespace BBT.Workflow.Security;

/// <summary>
/// Validates input parameters to prevent injection attacks and resource exhaustion
/// Domain Service - stateless validation logic
/// </summary>
public static class InputValidator
{
    /// <summary>
    /// Maximum length of a single filter string (5KB)
    /// </summary>
    public const int MaxFilterLength = 5000;

    /// <summary>
    /// Maximum number of filters in a request
    /// </summary>
    public const int MaxFiltersCount = 50;

    /// <summary>
    /// Maximum length of a field name
    /// </summary>
    public const int MaxFieldNameLength = 100;

    /// <summary>
    /// Maximum length of a filter value
    /// </summary>
    public const int MaxValueLength = 1000;

    /// <summary>
    /// Maximum nesting depth for JSON field paths (e.g., a.b.c.d.e = depth 5)
    /// </summary>
    public const int MaxFieldDepth = 10;

    /// <summary>
    /// Maximum serialized length of the <c>includes</c> operator JSON object payload.
    /// </summary>
    public const int MaxIncludesPayloadJsonLength = 4096;

    /// <summary>
    /// Maximum nesting depth inside an <c>includes</c> payload (objects/arrays).
    /// </summary>
    public const int MaxIncludesPayloadNestingDepth = 8;

    /// <summary>
    /// Maximum number of object property names across the entire <c>includes</c> payload tree.
    /// </summary>
    public const int MaxIncludesPayloadPropertyCount = 40;

    /// <summary>
    /// Validates a single filter string
    /// </summary>
    /// <param name="filter">Filter to validate</param>
    /// <exception cref="ArgumentException">Thrown when validation fails</exception>
    public static void ValidateFilters(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return;
        ValidateFilters(new[] { filter });
    }

    /// <summary>
    /// Validates an array of filters
    /// </summary>
    /// <param name="filters">Filters to validate</param>
    /// <exception cref="ArgumentException">Thrown when validation fails</exception>
    public static void ValidateFilters(string[]? filters)
    {
        if (filters == null || filters.Length == 0)
            return;

        if (filters.Length > MaxFiltersCount)
            throw new ArgumentException($"Too many filters: {filters.Length}. Maximum allowed: {MaxFiltersCount}");

        for (int i = 0; i < filters.Length; i++)
        {
            var filter = filters[i];
            if (string.IsNullOrEmpty(filter))
                continue;

            if (filter.Length > MaxFilterLength)
                throw new ArgumentException($"Filter at index {i} is too long: {filter.Length} characters. Maximum allowed: {MaxFilterLength}");
        }
    }

    /// <summary>
    /// Validates a field name
    /// </summary>
    /// <param name="fieldName">Field name to validate</param>
    /// <exception cref="ArgumentException">Thrown when validation fails</exception>
    public static void ValidateFieldName(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            throw new ArgumentException("Field name cannot be null or empty");

        if (fieldName.Length > MaxFieldNameLength)
            throw new ArgumentException($"Field name too long: {fieldName.Length} characters. Maximum allowed: {MaxFieldNameLength}");

        // Check for dangerous characters
        if (fieldName.IndexOfAny(new[] { '\'', '"', ';', '-', '/', '\\', '<', '>', '|', '&', '$', '`', '{', '}', '[', ']', '(', ')', '!', '@', '#', '%', '^', '*', '=', '+', '~', '?', ':', ' ' }) >= 0)
            throw new ArgumentException($"Field name contains invalid characters: {fieldName}");

        var parts = fieldName.Split('.');
        if (parts.Length > MaxFieldDepth)
            throw new ArgumentException($"Field path too deep: {parts.Length} levels. Maximum allowed: {MaxFieldDepth}");

        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
                throw new ArgumentException($"Invalid field path: empty part in '{fieldName}'");

            if (part.Length > 50)
                throw new ArgumentException($"Field part too long: '{part}' ({part.Length} characters). Maximum allowed: 50");
                
            // Each part must start with letter and contain only alphanumeric and underscore
            if (!char.IsLetter(part[0]))
                throw new ArgumentException($"Field part must start with a letter: '{part}' in '{fieldName}'");
                
            foreach (var ch in part)
            {
                if (!char.IsLetterOrDigit(ch) && ch != '_')
                    throw new ArgumentException($"Field part contains invalid character '{ch}' in '{part}'");
            }
        }
    }

    /// <summary>
    /// Validates a filter value
    /// </summary>
    /// <param name="value">Value to validate</param>
    /// <exception cref="ArgumentException">Thrown when validation fails</exception>
    public static void ValidateValue(string? value)
    {
        if (value == null)
            return;

        if (value.Length > MaxValueLength)
            throw new ArgumentException($"Value too long: {value.Length} characters. Maximum allowed: {MaxValueLength}");
    }

    /// <summary>
    /// Validates the JSON object used as the <c>includes</c> filter payload (size, depth, property count).
    /// </summary>
    /// <param name="payload">Root element; must be a JSON object.</param>
    /// <exception cref="ArgumentException">Thrown when validation fails</exception>
    public static void ValidateIncludesObject(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("includes payload must be a JSON object.");

        var raw = payload.GetRawText();
        if (raw.Length > MaxIncludesPayloadJsonLength)
            throw new ArgumentException(
                $"includes payload too large: {raw.Length} characters. Maximum allowed: {MaxIncludesPayloadJsonLength}");

        var propertyCount = 0;
        ValidateIncludesSubtree(payload, depth: 0, ref propertyCount);
    }

    private static void ValidateIncludesSubtree(JsonElement element, int depth, ref int propertyCount)
    {
        if (depth > MaxIncludesPayloadNestingDepth)
            throw new ArgumentException(
                $"includes payload nesting too deep (>{MaxIncludesPayloadNestingDepth}).");

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    propertyCount++;
                    if (propertyCount > MaxIncludesPayloadPropertyCount)
                        throw new ArgumentException(
                            $"includes payload has too many properties (>{MaxIncludesPayloadPropertyCount}).");

                    if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        ValidateIncludesSubtree(prop.Value, depth + 1, ref propertyCount);
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    index++;
                    if (index > MaxIncludesPayloadPropertyCount)
                        throw new ArgumentException("includes payload array has too many elements.");
                    if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        ValidateIncludesSubtree(item, depth + 1, ref propertyCount);
                }
                break;
        }
    }

    /// <summary>
    /// Validates a JSON string length
    /// </summary>
    /// <param name="json">JSON string to validate</param>
    /// <exception cref="ArgumentException">Thrown when validation fails</exception>
    public static void ValidateJsonLength(string? json)
    {
        if (json == null)
            return;

        if (json.Length > MaxFilterLength)
            throw new ArgumentException($"JSON too long: {json.Length} characters. Maximum allowed: {MaxFilterLength}");
    }

    /// <summary>
    /// Escapes content for PostgreSQL single-quoted string literals (SQL: double each apostrophe).
    /// </summary>
    public static string EscapePostgresSingleQuotedString(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    /// <summary>
    /// Validates a safe identifier for use as a double-quoted SQL column name (e.g. JSONB column "Data").
    /// </summary>
    public static void ValidateSqlJsonColumnIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("JSON column name cannot be null or empty.", nameof(name));
        if (name.Length > 63)
            throw new ArgumentException("JSON column name exceeds PostgreSQL identifier length.", nameof(name));
        if (!char.IsLetter(name[0]) && name[0] != '_')
            throw new ArgumentException($"Invalid JSON column name: {name}", nameof(name));
        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
                throw new ArgumentException($"Invalid JSON column name: {name}", nameof(name));
        }
    }
}

