using System;

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
}

