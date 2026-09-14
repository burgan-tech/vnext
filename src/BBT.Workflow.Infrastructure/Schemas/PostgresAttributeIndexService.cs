using System.Security.Cryptography;
using System.Text;
using BBT.Aether.DistributedCache;
using BBT.Aether.DistributedLock;
using BBT.Aether.MultiSchema;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BBT.Workflow.Schemas;

/// <summary>Read-only catalog of projections prepared by DBA-executed CLI SQL. Never executes DDL.</summary>
public sealed class PostgresAttributeIndexService(
    IConfiguration configuration, IOptionsMonitor<AttributeIndexOptions> options,
    ICurrentSchema currentSchema, ISchemaNameFormatter schemaNameFormatter,
    IDistributedCacheService cache, IDistributedLockService locks)
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
        var cacheKey = BuildCacheKey(ConnectionString, schema);
        var cached = await cache.GetAsync<ReadyIndexSnapshot>(cacheKey, cancellationToken);
        if (cached != null && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Keys.ToHashSet(StringComparer.Ordinal);

        // Coalesce catalog refresh across replicas. Contenders retain the JSON query path
        // until the owner publishes the shared snapshot; never cache that temporary fallback.
        await using var lease = await locks.TryAcquireLockAsync(cacheKey + ":refresh", 60, cancellationToken);
        cached = await cache.GetAsync<ReadyIndexSnapshot>(cacheKey, cancellationToken);
        if (cached != null && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Keys.ToHashSet(StringComparer.Ordinal);
        if (lease == null) return new HashSet<string>();

        var ready = await ReadReadyAsync(schema, cancellationToken);
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, settings.CatalogCacheSeconds));
        await cache.SetAsync(cacheKey, new ReadyIndexSnapshot(ready.ToArray(), expiresAt), new DistributedCacheEntryOptions
        {
            AbsoluteExpiration = expiresAt
        }, cancellationToken);
        return ready;
    }

    // Enforce freshness even when a cache provider rounds or ignores its TTL metadata.
    internal sealed record ReadyIndexSnapshot(string[] Keys, DateTimeOffset ExpiresAt);

    // Scope shared entries to database and role, without putting credentials in cache keys/traces.
    internal static string BuildCacheKey(string connectionString, string schema)
    {
        var database = new NpgsqlConnectionStringBuilder(connectionString);
        var identity = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            database.Host, database.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            database.Database, database.Username
        });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return $"vnext:attribute-indexes:v1:{hash}:{schema}";
    }

    private async Task<IReadOnlySet<string>> ReadReadyAsync(string schema, CancellationToken cancellationToken)
    {
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
            return ready;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // Before the first explicit maintenance run the JSON path remains available.
            return new HashSet<string>();
        }
    }

}
