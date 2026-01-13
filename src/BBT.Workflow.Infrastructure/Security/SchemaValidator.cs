using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Workflow.Data;
using BBT.Workflow.Instances;
using BBT.Workflow.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace BBT.Workflow.Infrastructure.Security;

/// <summary>
/// Dynamic schema validator that validates against active flows in the system
/// Caches valid schemas for performance
/// Infrastructure Service - contains infrastructure dependencies (DB, Cache)
/// </summary>
public class SchemaValidator : ISchemaValidator
{
    private readonly IDistributedCache _cache;
    private readonly IDbContextProvider<WorkflowDbContext> _dbContextProvider;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private const string CacheKey = "valid_schemas_list";
    
    // System schemas that are always valid
    private static readonly HashSet<string> SystemSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        "public",
        "sys_flows",
        "sys_extensions", 
        "sys_functions",
        "sys_schemas",
        "sys_tasks",
        "sys_views"
    };

    // Valid table names whitelist
    private static readonly HashSet<string> AllowedTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "Instances",
        "InstancesData", 
        "Tasks",
        "Workflows",
        "InstanceCorrelations"
    };

    // Regex with timeout for schema format validation
    private static readonly Regex SchemaFormatRegex = new(
        @"^[a-z][a-z0-9_]*$",
        RegexOptions.None,
        TimeSpan.FromMilliseconds(100));

    public SchemaValidator(
        IDistributedCache cache,
        IDbContextProvider<WorkflowDbContext> dbContextProvider)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _dbContextProvider = dbContextProvider ?? throw new ArgumentNullException(nameof(dbContextProvider));
    }

    /// <inheritdoc />
    public async Task<string> ValidateSchemaAsync(string? schema, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(schema))
            return "public";

        // First check for dangerous characters before any trimming
        if (schema.IndexOfAny(new[] { '\'', '"', ';', '-', '/', '\\', '<', '>', '|', ' ', '\t', '\n', '\r' }) >= 0)
            throw new SecurityException($"Invalid schema name format: {schema}");

        var cleaned = CleanSchemaName(schema);

        // Check system schemas first (no DB lookup needed)
        if (SystemSchemas.Contains(cleaned))
            return cleaned;

        // Validate naming convention
        if (!IsValidSchemaFormat(cleaned))
            throw new SecurityException($"Invalid schema name format: {schema}");

        // Check cache for valid schemas
        var validSchemas = await GetValidSchemasAsync(cancellationToken);
        
        if (validSchemas.Contains(cleaned, StringComparer.OrdinalIgnoreCase))
            return cleaned;

        throw new SecurityException($"Schema not found or not authorized: {schema}");
    }

    /// <inheritdoc />
    public string ValidateSchemaSync(string? schema)
    {
        if (string.IsNullOrWhiteSpace(schema))
            return "public";

        // First check for dangerous characters before any trimming
        if (schema.IndexOfAny(new[] { '\'', '"', ';', '-', '/', '\\', '<', '>', '|', ' ', '\t', '\n', '\r' }) >= 0)
            throw new SecurityException($"Invalid schema name format: {schema}");

        var cleaned = CleanSchemaName(schema);

        // Validate format first (must be lowercase)
        if (!IsValidSchemaFormat(cleaned))
            throw new SecurityException($"Invalid schema name format: {schema}");

        // Check system schemas (after format validation)
        if (SystemSchemas.Contains(cleaned))
            return cleaned;

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

        if (!AllowedTables.Contains(cleaned))
            throw new SecurityException($"Invalid table name: {tableName}");

        return cleaned;
    }

    /// <inheritdoc />
    public async Task InvalidateCacheAsync(CancellationToken cancellationToken = default)
    {
        await _cache.RemoveAsync(CacheKey, cancellationToken);
    }

    /// <summary>
    /// Gets list of valid schemas from cache or database
    /// </summary>
    private async Task<HashSet<string>> GetValidSchemasAsync(CancellationToken cancellationToken)
    {
        // Try to get from cache
        var cachedJson = await _cache.GetStringAsync(CacheKey, cancellationToken);
        if (!string.IsNullOrEmpty(cachedJson))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<HashSet<string>>(cachedJson);
                if (cached != null && cached.Count > 0)
                    return cached;
            }
            catch (JsonException)
            {
                // Cache corrupted, continue to load from DB
            }
        }

        // Load from database (sys_flows schema)
        var validSchemas = await LoadValidSchemasFromDbAsync(cancellationToken);

        // Cache for 5 minutes
        try
        {
            var json = JsonSerializer.Serialize(validSchemas);
            await _cache.SetStringAsync(
                CacheKey, 
                json, 
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheDuration },
                cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Cache write failed, continue without caching
        }
        catch (System.IO.IOException)
        {
            // Cache write failed due to IO error, continue without caching
        }

        return validSchemas;
    }

    /// <summary>
    /// Loads valid schemas from sys_flows.Instances table
    /// Converts flow keys to schema names (e.g., "sys-flows" → "sys_flows")
    /// </summary>
    private async Task<HashSet<string>> LoadValidSchemasFromDbAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Get DbContext using the provider's GetDbContext method
            var dbContext = _dbContextProvider.GetDbContext();
            
            // Query sys_flows schema to get all active flow keys
            var flowKeys = await dbContext.Instances
                .FromSqlRaw(@"SELECT * FROM ""sys_flows"".""Instances"" WHERE ""Status"" = 'A'")
                .Select(i => i.Key)
                .Distinct()
                .ToListAsync(cancellationToken);

            var schemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            // Add system schemas
            foreach (var systemSchema in SystemSchemas)
                schemas.Add(systemSchema);

            // Convert flow keys to schema names (replace hyphen with underscore)
            foreach (var key in flowKeys)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    var schemaName = ConvertKeyToSchemaName(key);
                    if (IsValidSchemaFormat(schemaName))
                        schemas.Add(schemaName);
                }
            }

            return schemas;
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            // On database error, return only system schemas
            return new HashSet<string>(SystemSchemas, StringComparer.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            // On operation error, return only system schemas
            return new HashSet<string>(SystemSchemas, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Converts flow key to schema name (e.g., "sys-flows" → "sys_flows")
    /// </summary>
    private static string ConvertKeyToSchemaName(string key)
    {
        return key.Replace('-', '_').ToLowerInvariant();
    }

    /// <summary>
    /// Cleans schema name by removing quotes and extra whitespace
    /// </summary>
    private static string CleanSchemaName(string schema)
    {
        return schema.Trim().Trim('"').Trim('\'').Trim();
    }

    /// <summary>
    /// Validates schema name format
    /// Format: lowercase letters, numbers, underscores only
    /// Must start with letter, max 63 chars (PostgreSQL limit)
    /// </summary>
    private static bool IsValidSchemaFormat(string schema)
    {
        if (string.IsNullOrWhiteSpace(schema))
            return false;

        if (schema.Length > 63) // PostgreSQL identifier limit
            return false;

        try
        {
            // Must start with letter, contain only lowercase letters, numbers, and underscores
            return SchemaFormatRegex.IsMatch(schema);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}

