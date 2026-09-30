using System.Security.Cryptography;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Workflow.Authorization;
using BBT.Workflow.Data;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BBT.Workflow.Encryption;

/// <summary>The two secrets of one instance, as the cipher uses them.</summary>
public sealed record InstanceSecretMaterial(byte[] EncryptionKey, byte[] HashSalt);

/// <summary>
/// Reads and creates per-instance <c>x-encryption</c> secrets (singleton) and holds them in a private, size-bounded
/// in-process cache — the L1. There is deliberately no L2: a distributed cache would put the secrets into Redis, i.e.
/// expose them outside the database whose security covers them (requester decision). This is the documented exception
/// to "use IDistributedCache": the same private-MemoryCache pattern as <c>ComponentL1Cache</c>/<c>DiscoveryL1Cache</c>.
/// <para>
/// A secret never changes once created, so a cached entry can never be stale; an entry of a deleted instance simply ages
/// out (sliding expiry). The single exception — a first write whose transaction rolled back after the secret was cached —
/// is handled by the protector: a token that fails authentication under a cached secret evicts it and is retried once
/// against the database.
/// </para>
/// </summary>
public sealed class InstanceSecretStore : IDisposable
{
    private readonly MemoryCache _cache;
    private readonly TimeSpan _sliding;
    private readonly string? _connectionString;
    private readonly ILogger _logger;
    private NpgsqlDataSource? _fallbackSource;

    /// <summary>Creates the store.</summary>
    public InstanceSecretStore(
        IOptions<SchemaEncryptionOptions> options,
        IConfiguration? configuration = null,
        ILogger<InstanceSecretStore>? logger = null)
    {
        var value = options.Value;
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = Math.Max(1, value.SecretCacheEntries) });
        _sliding = TimeSpan.FromMinutes(Math.Max(1, value.SecretCacheSlidingMinutes));
        _connectionString = configuration?.GetConnectionString("Default");
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>Cached secret of <paramref name="instanceId"/> in <paramref name="schema"/>, or null.</summary>
    public InstanceSecretMaterial? TryGetCached(string? schema, Guid instanceId) =>
        _cache.TryGetValue(Key(schema, instanceId), out InstanceSecretMaterial? material) ? material : null;

    /// <summary>Puts a secret into the cache (tests, and callers that already hold the row).</summary>
    internal void Put(string? schema, Guid instanceId, InstanceSecretMaterial material) =>
        Remember(schema, new SecretRow { InstanceId = instanceId, EncryptionKey = material.EncryptionKey, HashSalt = material.HashSalt });

    /// <summary>Drops a cached secret (after a token failed to authenticate under it).</summary>
    public void Evict(string? schema, Guid instanceId) => _cache.Remove(Key(schema, instanceId));

    /// <summary>
    /// Returns the instance's secret, creating it if it does not exist yet. Runs on the write funnel's own context, inside
    /// its transaction and under its per-instance row lock, so two first writes cannot create two different secrets.
    /// <para>
    /// One round trip: <c>INSERT … ON CONFLICT DO NOTHING RETURNING</c> in a CTE, unioned with a read of the existing row
    /// when the insert did nothing (every write after the first). The statement's read part sees the snapshot taken at
    /// statement start, so a row committed by a concurrent transaction between that snapshot and the conflict check
    /// would make it return nothing; the row lock rules that out on the funnel path, and the plain read below keeps the
    /// method correct without relying on it.
    /// </para>
    /// </summary>
    public async Task<InstanceSecretMaterial> GetOrCreateAsync(
        WorkflowDbContext context, Guid instanceId, CancellationToken cancellationToken)
    {
        var schema = context.CurrentSchemaName;
        var table = Table(schema);

        // Materialized without LINQ composition: EF would wrap a composed raw query in a subquery, and PostgreSQL only
        // accepts a data-modifying CTE at the top level.
        var rows = await context.Database
            .SqlQueryRaw<SecretRow>(
                "WITH ins AS (" +
                $"INSERT INTO {table} (\"InstanceId\", \"EncryptionKey\", \"HashSalt\", \"CreatedAt\") " +
                "VALUES ({0}, {1}, {2}, {3}) ON CONFLICT (\"InstanceId\") DO NOTHING " +
                "RETURNING \"InstanceId\", \"EncryptionKey\", \"HashSalt\") " +
                "SELECT \"InstanceId\", \"EncryptionKey\", \"HashSalt\" FROM ins " +
                "UNION ALL " +
                $"SELECT \"InstanceId\", \"EncryptionKey\", \"HashSalt\" FROM {table} " +
                "WHERE \"InstanceId\" = {0} AND NOT EXISTS (SELECT 1 FROM ins)",
                instanceId, RandomNumberGenerator.GetBytes(InstanceSecret.SecretSize),
                RandomNumberGenerator.GetBytes(InstanceSecret.SecretSize), DateTime.UtcNow)
            .ToListAsync(cancellationToken);

        var row = rows.Count > 0
            ? rows[0]
            : await ReadCommittedRowAsync(context, table, instanceId, cancellationToken);

        return Remember(schema, row);
    }

    /// <summary>Fallback read of the secret row in a fresh statement snapshot (see <see cref="GetOrCreateAsync"/>).</summary>
    private static async Task<SecretRow> ReadCommittedRowAsync(
        WorkflowDbContext context, string table, Guid instanceId, CancellationToken cancellationToken)
    {
        var rows = await context.Database
            .SqlQueryRaw<SecretRow>(
                $"SELECT \"InstanceId\", \"EncryptionKey\", \"HashSalt\" FROM {table} WHERE \"InstanceId\" = {{0}}",
                instanceId)
            .ToListAsync(cancellationToken);

        return rows.Count == 1
            ? rows[0]
            : throw new InvalidOperationException($"InstanceSecrets row for instance {instanceId} was not found after insert.");
    }

    /// <summary>Loads the secrets of <paramref name="instanceIds"/> not cached yet, in one query on <paramref name="context"/>.</summary>
    public async Task PreloadAsync(
        WorkflowDbContext context, IReadOnlyCollection<Guid> instanceIds, CancellationToken cancellationToken)
    {
        var schema = context.CurrentSchemaName;
        var missing = instanceIds.Where(id => TryGetCached(schema, id) is null).Distinct().ToArray();
        if (missing.Length == 0)
            return;

        var rows = await context.Database
            .SqlQueryRaw<SecretRow>(
                $"SELECT \"InstanceId\", \"EncryptionKey\", \"HashSalt\" FROM {Table(schema)} WHERE \"InstanceId\" = ANY({{0}})",
                missing)
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
            Remember(schema, row);
    }

    /// <summary>
    /// Synchronous lookup for a row opened without a preload (a reader outside the preloaded entry points). Uses its own
    /// pooled connection — the calling context may be in the middle of materializing a query. Returns null when there is
    /// no secret (nothing was ever encrypted for the instance) or the lookup fails; the caller then keeps the tokens.
    /// </summary>
    public InstanceSecretMaterial? TryLoad(string? schema, Guid instanceId)
    {
        if (TryGetCached(schema, instanceId) is { } cached)
            return cached;
        if (string.IsNullOrWhiteSpace(_connectionString))
            return null;

        try
        {
            _fallbackSource ??= NpgsqlDataSource.Create(_connectionString);
            using var command = _fallbackSource.CreateCommand(
                $"SELECT \"EncryptionKey\", \"HashSalt\" FROM {Table(schema)} WHERE \"InstanceId\" = $1");
            command.Parameters.Add(new NpgsqlParameter { Value = instanceId });
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;

            _logger.InstanceSecretLoadedWithoutPreload(instanceId, schema ?? "public");
            return Remember(schema, new SecretRow
            {
                InstanceId = instanceId,
                EncryptionKey = (byte[])reader[0],
                HashSalt = (byte[])reader[1],
            });
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            _logger.InstanceSecretLookupFailed(instanceId, schema ?? "public", ex.GetType().Name);
            return null;
        }
    }

    private InstanceSecretMaterial Remember(string? schema, SecretRow row)
    {
        var material = new InstanceSecretMaterial(row.EncryptionKey, row.HashSalt);
        _cache.Set(Key(schema, row.InstanceId), material, new MemoryCacheEntryOptions
        {
            Size = 1,
            SlidingExpiration = _sliding,
        });
        return material;
    }

    private static string Key(string? schema, Guid instanceId) => $"{schema ?? "public"}|{instanceId:N}";

    private static string Table(string? schema) =>
        $"\"{(schema ?? "public").Replace("\"", "", StringComparison.Ordinal)}\".\"InstanceSecrets\"";

    /// <inheritdoc />
    public void Dispose()
    {
        _cache.Dispose();
        _fallbackSource?.Dispose();
    }

    /// <summary>Raw projection of an <c>InstanceSecrets</c> row.</summary>
    internal sealed class SecretRow
    {
        public Guid InstanceId { get; set; }
        public byte[] EncryptionKey { get; set; } = [];
        public byte[] HashSalt { get; set; } = [];
    }
}

/// <summary>
/// Scoped <see cref="IInstanceSecretPreloader"/>: preloads through the current flow schema's context, so the query runs
/// on the same schema (and connection) the instance was read from.
/// </summary>
public sealed class InstanceSecretPreloader(
    IAetherDbContextProvider<WorkflowDbContext> dbContextProvider,
    InstanceSecretStore store) : IInstanceSecretPreloader
{
    /// <inheritdoc />
    public async Task PreloadAsync(IReadOnlyCollection<Guid> instanceIds, CancellationToken cancellationToken = default)
    {
        if (instanceIds.Count == 0)
            return;

        var context = await dbContextProvider.GetDbContextAsync();
        await store.PreloadAsync(context, instanceIds, cancellationToken);
    }
}
