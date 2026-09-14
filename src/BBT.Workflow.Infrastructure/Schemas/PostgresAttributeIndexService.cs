using BBT.Aether.MultiSchema;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BBT.Workflow.Schemas;

/// <summary>Read-only catalog of projections prepared by DBA-executed CLI SQL. Never executes DDL.</summary>
public sealed class PostgresAttributeIndexService(
    IConfiguration configuration, IOptionsMonitor<AttributeIndexOptions> options,
    ICurrentSchema currentSchema, ISchemaNameFormatter schemaNameFormatter, IMemoryCache cache)
    : IAttributeIndexCatalog
{
    private string ConnectionString => configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("ConnectionStrings:Default is required.");

    private NpgsqlConnection CreateConnection() => new(ConnectionString);

    public async Task<IReadOnlySet<string>> GetReadyAsync(string schema, CancellationToken cancellationToken = default)
    {
        var settings = options.CurrentValue;
        if (!settings.Enabled) return new HashSet<string>();
        schema = new SyncSchemaValidator().ValidateSchemaSync(schemaNameFormatter.Format(schema));
        if (settings.DisabledFlows.Any(f => schemaNameFormatter.Format(f) == schema)) return new HashSet<string>();
        var cacheKey = (typeof(PostgresAttributeIndexService), ConnectionString, schema);
        if (cache.TryGetValue<IReadOnlySet<string>>(cacheKey, out var cached)) return cached!;
        using var scope = currentSchema.Change(schema);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = new NpgsqlCommand($"""
                SELECT c."Key" FROM "{schema}"."AttributeIndexCatalog" c
                JOIN pg_namespace n ON n.nspname = @schema
                JOIN pg_class t ON t.relnamespace = n.oid AND t.relname = 'InstancesData'
                JOIN pg_attribute a ON a.attrelid = t.oid AND a.attname = c."ColumnName"
                WHERE c."Ready" AND a.attgenerated = 's' AND NOT a.attisdropped
                  AND format_type(a.atttypid, a.atttypmod) = c."PgType"
                  AND NOT EXISTS (
                    SELECT 1 FROM unnest(c."Indexes") required(name)
                    WHERE NOT EXISTS (
                      SELECT 1 FROM pg_class ix JOIN pg_index i ON i.indexrelid = ix.oid
                      WHERE ix.relnamespace = n.oid AND ix.relname = required.name
                        AND i.indrelid = t.oid AND i.indisvalid AND i.indisready))
                """, connection);
            command.Parameters.AddWithValue("schema", schema);
            var ready = new HashSet<string>(StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) ready.Add(reader.GetString(0));
            cache.Set<IReadOnlySet<string>>(cacheKey, ready, TimeSpan.FromSeconds(Math.Max(1, settings.CatalogCacheSeconds)));
            return ready;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // Before the first explicit maintenance run the JSON path remains available.
            IReadOnlySet<string> empty = new HashSet<string>();
            cache.Set(cacheKey, empty, TimeSpan.FromSeconds(Math.Max(1, settings.CatalogCacheSeconds)));
            return empty;
        }
    }

}
