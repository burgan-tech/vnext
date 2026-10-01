using System.Security.Cryptography;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Workflow.Authorization;
using BBT.Workflow.Data;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.EntityFrameworkCore;
using System.Data.Common;
using BBT.Aether.MultiSchema;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly ILogger _logger;

    /// <summary>
    /// Creates the store. <paramref name="scopeFactory"/> opens the isolated EF context an on-demand load reads through;
    /// without it (unit tests) a secret that is not cached simply cannot be loaded.
    /// </summary>
    public InstanceSecretStore(
        IOptions<SchemaEncryptionOptions> options,
        IServiceScopeFactory? scopeFactory = null,
        ILogger<InstanceSecretStore>? logger = null)
    {
        var value = options.Value;
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = Math.Max(1, value.SecretCacheEntries) });
        _sliding = TimeSpan.FromMinutes(Math.Max(1, value.SecretCacheSlidingMinutes));
        _scopeFactory = scopeFactory;
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
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="OperationCanceledException"></exception>
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
    /// Loads one instance's secret on demand — a row opened without a preload (a script's <c>DecryptAsync</c>, a read of a
    /// single protected row). Reads through EF on an ISOLATED unit of work: a fresh DI scope and a fresh, non-transactional
    /// context bound to <paramref name="schema"/>, so it never touches the caller's DbContext, which a parallel task branch
    /// may be using, and its connection comes from the same pool as every other EF read. One indexed primary-key lookup
    /// of two columns; the cache answers every later call.
    /// <para>
    /// <paramref name="cancellationToken"/> belongs to this call only and the cache is written only on success. Cancellation
    /// is never turned into "no secret": a cancelled token throws <see cref="OperationCanceledException"/>, even when the
    /// provider reported it as another error. Any other failure leaves the value closed (null) and logs.
    /// </para>
    /// </summary>
    public async Task<InstanceSecretMaterial?> TryLoadAsync(string? schema, Guid instanceId, CancellationToken cancellationToken)
    {
        if (TryGetCached(schema, instanceId) is { } cached)
            return cached;
        if (_scopeFactory is null)
            return null;

        try
        {
            var row = await _scopeFactory.ExecuteInIsolatedUnitOfWorkAsync(async (sp, ct) =>
            {
                using var _ = sp.GetRequiredService<ICurrentSchema>().Change(schema ?? "public");
                var context = await sp.GetRequiredService<IAetherDbContextProvider<WorkflowDbContext>>().GetDbContextAsync(ct);
                return await context.InstanceSecrets
                    .AsNoTracking()
                    .Where(s => s.InstanceId == instanceId)
                    .Select(s => new SecretRow { InstanceId = s.InstanceId, EncryptionKey = s.EncryptionKey, HashSalt = s.HashSalt })
                    .FirstOrDefaultAsync(ct);
            }, cancellationToken);

            if (row is null)
                return null;

            _logger.InstanceSecretLoadedWithoutPreload(instanceId, schema ?? "public");
            return Remember(schema, row);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

        var context = await dbContextProvider.GetDbContextAsync(cancellationToken);
        await store.PreloadAsync(context, instanceIds, cancellationToken);
    }
}
