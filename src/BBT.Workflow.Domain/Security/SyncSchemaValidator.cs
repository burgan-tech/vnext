using System;
using System.Linq;
using System.Security;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Workflow.Security;

/// <summary>
/// Fallback validator for scenarios where async validation is not possible
/// Uses only format validation without database lookup
/// Domain Service - contains only business logic without infrastructure dependencies
/// </summary>
public class SyncSchemaValidator : ISchemaValidator
{
    private static readonly Regex SchemaFormatRegex = new(
        @"^[a-z][a-z0-9_]*$",
        RegexOptions.None,
        TimeSpan.FromMilliseconds(100));

    /// <inheritdoc />
    public Task<string> ValidateSchemaAsync(string? schema, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ValidateSchemaSync(schema));
    }

    /// <inheritdoc />
    public string ValidateSchemaSync(string? schema)
    {
        if (string.IsNullOrWhiteSpace(schema))
            return "public";

        // First check for dangerous characters before any trimming
        if (schema.IndexOfAny(new[] { '\'', '"', ';', '-', '/', '\\', '<', '>', '|', ' ', '\t', '\n', '\r' }) >= 0)
            throw new SecurityException($"Invalid schema name format: {schema}");

        var cleaned = schema.Trim();

        // Validate format (must be lowercase, start with letter, alphanumeric + underscore)
        if (cleaned.Length > 63 || !IsValidFormat(cleaned))
            throw new SecurityException($"Invalid schema name format: {schema}");

        return cleaned;
    }

    /// <inheritdoc />
    public string ValidateTableName(string? tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            return "Instances";

        // First check for dangerous characters before any trimming
        if (tableName.IndexOfAny(new[] { '\'', '"', ';', '-', '/', '\\', '<', '>', '|', ' ', '\t', '\n', '\r' }) >= 0)
            throw new SecurityException($"Invalid table name: {tableName}");

        var cleaned = tableName.Trim();
        var allowedTables = new[] { "Instances", "InstancesData", "Tasks", "Workflows", "InstanceCorrelations" };

        if (!allowedTables.Contains(cleaned, StringComparer.OrdinalIgnoreCase))
            throw new SecurityException($"Invalid table name: {tableName}");

        return cleaned;
    }

    /// <inheritdoc />
    public Task InvalidateCacheAsync(CancellationToken cancellationToken = default)
    {
        // No-op for sync validator (no cache)
        return Task.CompletedTask;
    }

    private static bool IsValidFormat(string schema)
    {
        try
        {
            return SchemaFormatRegex.IsMatch(schema);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}

