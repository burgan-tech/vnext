# Caching Strategy

## Overview

The BBT Workflow Engine uses a multi-level caching strategy for workflow definitions and related components. The cache combines local in-memory snapshots with a distributed cache provider (via Aether SDK) and a runtime backend fallback to keep reads fast while preserving correctness across multiple instances.

## Architecture Overview

```
┌─────────────────────────────────────────────────────────┐
│                Application Layer                        │
│  ┌───────────────────────────────────────────────────┐  │
│  │              ComponentCacheStore                   │  │
│  └───────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────┘
                           │
┌─────────────────────────────────────────────────────────┐
│              Domain Cache Context                       │
│  ┌───────────────────────────────────────────────────┐  │
│  │ CacheSet<T> (Workflows, Tasks, Schemas, ...)      │  │
│  └───────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────┘
                           │
┌─────────────────────────────────────────────────────────┐
│              Multi-Level Cache System                   │
│  ┌──────────────┐ ┌──────────────┐ ┌────────────────┐  │
│  │ Local        │ │ vidx Cache   │ │ Distributed    │  │
│  │ Snapshot     │ │ (Short-TTL   │ │ Cache (Aether) │  │
│  │ (In-Memory)  │ │  In-Memory)  │ │                │  │
│  └──────────────┘ └──────────────┘ └────────────────┘  │
│                          │                              │
│                   ┌──────────────┐                      │
│                   │ Redis vidx   │                      │
│                   │ (Version     │                      │
│                   │  Index)      │                      │
│                   └──────────────┘                      │
└─────────────────────────────────────────────────────────┘
                           │
┌─────────────────────────────────────────────────────────┐
│            Runtime Backend (IRuntimeService)            │
└─────────────────────────────────────────────────────────┘
```

## Core Components

### 1. Domain Cache Context

The central orchestrator for cached workflow definitions and related components. Each `CacheSet<T>` is backed by an `ICacheBackend<T>` for runtime fallback:

```csharp
public class DomainCacheContext : CacheContext, IDomainCacheContext, IDisposable
{
    public ICacheSet<Workflow> Workflows { get; }
    public ICacheSet<WorkflowTask> Tasks { get; }
    public ICacheSet<SchemaDefinition> Schemas { get; }
    public ICacheSet<Function> Functions { get; }
    public ICacheSet<View> Views { get; }
    public ICacheSet<Extension> Extensions { get; }

    public DomainCacheContext(
        IDistributedCacheService distributedCache,
        ICacheBackend<Workflow> workflowBackend,
        ICacheBackend<WorkflowTask> taskBackend,
        ICacheBackend<SchemaDefinition> schemaBackend,
        ICacheBackend<Function> functionBackend,
        ICacheBackend<View> viewBackend,
        ICacheBackend<Extension> extensionBackend,
        ILoggerFactory loggerFactory)
    {
        Workflows = new CacheSet<Workflow>(
            distributedCache,
            workflowBackend,
            loggerFactory.CreateLogger<CacheSet<Workflow>>());
        // ... other cache sets
    }

    public ICacheSet<T> Set<T>() where T : class, IDomainEntity, IReferenceSetter
    {
        if (typeof(T) == typeof(Workflow)) return (ICacheSet<T>)Workflows;
        if (typeof(T) == typeof(WorkflowTask)) return (ICacheSet<T>)Tasks;
        // ... other types
        throw new NotSupportedException($"Type {typeof(T).Name} is not supported");
    }

    public int CleanupAll(TimeSpan? ttl = null, int? maxItemsPerSet = null, CancellationToken cancellationToken = default)
    {
        // Performs cleanup across all cache sets
    }
}
```

### 2. CacheSet<T> Implementation

Provides thread-safe caching using an **immutable snapshot model** with Compare-And-Swap (CAS) updates:

```csharp
public class CacheSet<T>(
    IDistributedCacheService distributedCache,
    ICacheBackend<T> backend,
    ILogger<CacheSet<T>> logger)
    : ICacheSet<T>
    where T : class, IDomainEntity, IReferenceSetter
{
    // Immutable snapshot holding all local cache state
    private CacheSnapshot<T> _snapshot = new(
        ImmutableDictionary<string, CacheItem<T>>.Empty,
        ImmutableDictionary<string, SortedSet<string>>.Empty);

    // Default cache configuration
    private static readonly TimeSpan DefaultItemTtl = TimeSpan.FromHours(12);
    private const int DefaultMaxItems = 10_000;
}
```

**Key Features:**
- **Lock-free reads**: Uses immutable snapshots for fast, concurrent reads
- **CAS-based writes**: Uses `Interlocked.CompareExchange` for thread-safe updates
- **Multi-layer caching**: Local → Distributed → Runtime backend
- **Automatic cleanup**: TTL and capacity-based eviction

### 3. Component Cache Store

Provides high-level API for accessing cached workflow components:

```csharp
public interface IComponentCacheStore
{
    Task<Result<Workflow>> GetFlowAsync(string domain, string key, string? version, CancellationToken cancellationToken = default);
    Task<Result<WorkflowTask>> GetTaskAsync(string domain, string key, string? version, CancellationToken cancellationToken = default);
    Task<Result<SchemaDefinition>> GetSchemaAsync(string domain, string key, string? version, CancellationToken cancellationToken = default);
    Task<Result<Function>> GetFunctionAsync(string domain, string key, string? version, CancellationToken cancellationToken = default);
    Task<Result<View>> GetViewAsync(string domain, string key, string? version, CancellationToken cancellationToken = default);
    Task<Result<Extension>> GetExtensionAsync(string domain, string key, string? version, CancellationToken cancellationToken = default);
    Task<Result<IEnumerable<Extension>>> GetAllExtensionsAsync(string domain, CancellationToken cancellationToken = default);
    Task<Result> SetAsync<T>(T entity, CancellationToken cancellationToken = default)
        where T : class, IDomainEntity, IReferenceSetter;
}
```

## Dependency Injection

Caching services are registered via `AddCacheServices()` inside `AddApplicationModule()`:

```csharp
public static IServiceCollection AddApplicationModule(this IServiceCollection services)
{
    services.AddCacheServices();
    return services;
}

private static void AddCacheServices(this IServiceCollection services)
{
    services.AddSingleton<ComponentCacheStore>();
    services.AddSingleton<IComponentCacheStore>(sp =>
    {
        var store = sp.GetRequiredService<ComponentCacheStore>();
        var metrics = sp.GetRequiredService<IWorkflowMetrics>();
        var logger = sp.GetRequiredService<ILogger<MetricsAwareComponentCacheStore>>();
        return store.WithMetrics(metrics, logger);
    });

    services.AddSingleton<IDomainCacheContext, DomainCacheContext>();
    services.AddSingleton<ICacheBackend<Workflow>, RuntimeCacheBackend<Workflow>>();
    services.AddSingleton<ICacheBackend<WorkflowTask>, RuntimeCacheBackend<WorkflowTask>>();
    services.AddSingleton<ICacheBackend<SchemaDefinition>, RuntimeCacheBackend<SchemaDefinition>>();
    services.AddSingleton<ICacheBackend<Function>, RuntimeCacheBackend<Function>>();
    services.AddSingleton<ICacheBackend<View>, RuntimeCacheBackend<View>>();
    services.AddSingleton<ICacheBackend<Extension>, RuntimeCacheBackend<Extension>>();
}
```

## Cache Key Strategy

### 1. Entity Cache Keys

Cache keys are derived from the entity `ComponentKey`, domain, key, and version:

```csharp
private static string CreateCacheKey(T entity)
    => $"{entity.ComponentKey}:{entity.Domain}:{entity.Key}:{entity.Version}";
```

### 2. Version-Based Indexing

The system maintains version indexes (`domain:key`) for efficient latest version retrieval:

```csharp
private void UpdateIndex(Dictionary<string, SortedSet<string>> index, T entity)
{
    var indexKey = CreateIndexKey(entity); // "domain:key"
    var version = entity.Version;

    if (!index.TryGetValue(indexKey, out var versionSet))
    {
        versionSet = new SortedSet<string>(new SemVersionComparer());
        index[indexKey] = versionSet;
    }

    versionSet.Add(version);
}
```

### 3. Reference Rehydration

When data comes from distributed cache or backend, the cache ensures references are populated if missing:

```csharp
private void EnsureReferenceIsSet(T entity, string cacheKey)
{
    // Parses cache key and sets reference if missing
}
```

## Caching Layers

### 1. Local In-Memory Cache (Immutable Snapshot)

**Purpose**: Ultra-fast, lock-free access for frequently accessed data
**Implementation**: Immutable snapshots with CAS-based updates
**Scope**: Single application instance

```csharp
public async Task<Result<T>> GetAsync(string cacheKey, CancellationToken cancellationToken = default)
{
    // 1) Lock-free snapshot read
    var snap = _snapshot;
    if (snap.Entries.TryGetValue(cacheKey, out var item))
    {
        item.UpdateAccess();
        return Result<T>.Ok(item.Value);
    }

    // 2) Distributed cache - errors are logged and fallback continues
    try
    {
        var fromDistributed = await distributedCache.GetAsync<T>(cacheKey, cancellationToken);
        if (fromDistributed is not null)
        {
            EnsureReferenceIsSet(fromDistributed, cacheKey);
            _ = UpsertLocalAsync(fromDistributed, cancellationToken);
            return Result<T>.Ok(fromDistributed);
        }
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error reading from distributed cache for {CacheKey}", cacheKey);
    }

    // 3) Runtime backend fallback
    var fromDbResult = await backend.LoadAsync(domain, key, version, cancellationToken);
    if (!fromDbResult.IsSuccess)
        return fromDbResult;

    var fromDb = fromDbResult.Value!;
    _ = SetAsync(fromDb, cancellationToken);
    return Result<T>.Ok(fromDb);
}
```

### 2. Distributed Cache (Aether SDK)

**Purpose**: Shared state across application instances  
**Implementation**: `IDistributedCacheService` from Aether SDK  
**Scope**: All application instances  
**Note**: The concrete provider (e.g., Redis) is configured outside this module.

```csharp
public async Task<Result> SetAsync(T entity, CancellationToken cancellationToken = default)
{
    var cacheKey = CreateCacheKey(entity);

    // 1) Update local cache via snapshot (CAS-based)
    SnapshotUpsert(cacheKey, entity);

    // 2) Write to distributed cache
    await distributedCache.SetAsync(cacheKey, entity, cancellationToken: cancellationToken);

    return Result.Ok();
}
```

### 3. Runtime Backend (ICacheBackend<T>)

**Purpose**: Ultimate source of truth when cache misses occur
**Implementation**: `ICacheBackend<T>` interface with runtime service

```csharp
public interface ICacheBackend<T>
{
    Task<Result<T>> LoadAsync(
        string domain,
        string key,
        string? version,
        CancellationToken cancellationToken = default);
}
```

## Cache Initialization

### 1. RuntimeCacheInitializer

Cache warm-up is handled by `RuntimeCacheInitializer`, which loads all definition types and initializes the `IDomainCacheContext`:

```csharp
public sealed class RuntimeCacheInitializer : IRuntimeCacheInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Load workflows, tasks, functions, views, schemas, extensions in parallel
        // Build initialData dictionary and call domainCacheContext.InitializeAsync(...)
        // Optionally register domain via IDomainRegistrationService when discovery is enabled
    }
}
```

### 2. Bulk Loading

```csharp
public Task LoadAllAsync(object data, CancellationToken cancellationToken = default)
{
    if (data is not IEnumerable<T> entities)
        throw new ArgumentException($"Invalid data type for {typeof(T).Name}");

    var entries = new Dictionary<string, CacheItem<T>>();
    var index = new Dictionary<string, SortedSet<string>>();

    foreach (var entity in entities)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cacheKey = CreateCacheKey(entity);
        var item = new CacheItem<T>(entity);
        entries[cacheKey] = item;
        UpdateIndex(index, entity);
    }

    var snapshot = new CacheSnapshot<T>(
        entries.ToImmutableDictionary(),
        index.ToImmutableDictionary(kvp => kvp.Key, kvp => new SortedSet<string>(kvp.Value)));

    Interlocked.Exchange(ref _snapshot, snapshot);
    return Task.CompletedTask;
}
```

## Version Management

### 1. Semantic Version Comparison

`CacheSet<T>` uses `SemVersionComparer` for ordering and `InstanceDataVersionComparer` for smart version matching (latest, full, artifact, partial).

```csharp
public class SemVersionComparer : IComparer<string>
{
    public int Compare(string x, string y)
    {
        if (Version.TryParse(x, out var versionX) && Version.TryParse(y, out var versionY))
        {
            return versionX.CompareTo(versionY);
        }
        
        return string.Compare(x, y, StringComparison.Ordinal);
    }
}
```

### 2. Latest Version Retrieval

`CacheSet<T>` uses the in-memory index to resolve the latest version, and falls back to the backend if needed:

```csharp
public Task<Result<T>> GetLatestByNameAsync(
    string domain,
    string name,
    CancellationToken cancellationToken = default)
{
    // Check snapshot index for latest version, otherwise load from backend
}
```

## Cache Invalidation

### 1. Manual Invalidation (CAS-based)

```csharp
public async Task<Result> InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default)
{
    // Remove from snapshot using CAS
    SnapshotRemove(cacheKey);

    // Remove from distributed cache
    await distributedCache.RemoveAsync(cacheKey, cancellationToken);

    return Result.Ok();
}

private void SnapshotRemove(string cacheKey)
{
    while (true)
    {
        var current = _snapshot;
        if (!current.Entries.ContainsKey(cacheKey))
            return;

        var entries = current.Entries.ToDictionary(k => k.Key, v => v.Value);
        entries.Remove(cacheKey, out _);

        // Rebuild index
        var index = RebuildIndex(entries);

        var newSnapshot = new CacheSnapshot<T>(
            entries.ToImmutableDictionary(),
            index.ToImmutableDictionary());

        var original = Interlocked.CompareExchange(ref _snapshot, newSnapshot, current);
        if (ReferenceEquals(original, current))
            break; // CAS succeeded
    }
}
```

### 2. Automatic Cleanup (TTL and Capacity)

```csharp
public int Cleanup(TimeSpan? ttl = null, int? maxItems = null, CancellationToken cancellationToken = default)
{
    ttl ??= DefaultItemTtl;      // 12 hours
    maxItems ??= DefaultMaxItems; // 10,000

    var now = DateTime.UtcNow;
    var current = _snapshot;
    var keptEntries = new Dictionary<string, CacheItem<T>>();
    var removedCount = 0;

    // 1) TTL filter - remove expired items
    foreach (var (key, item) in current.Entries)
    {
        var age = now - item.CreatedTime;
        var idle = now - item.LastAccessTime;

        if (age > ttl.Value || idle > ttl.Value)
        {
            removedCount++;
            continue;
        }
        keptEntries[key] = item;
    }

    // 2) Capacity filter - remove least-used items
    if (keptEntries.Count > maxItems.Value)
    {
        var ordered = keptEntries
            .OrderBy(kvp => kvp.Value.AccessCount)
            .ThenBy(kvp => kvp.Value.LastAccessTime)
            .ToList();

        var overflow = keptEntries.Count - maxItems.Value;
        for (var i = 0; i < overflow; i++)
        {
            keptEntries.Remove(ordered[i].Key);
            removedCount++;
        }
    }

    // Atomically replace snapshot
    var newSnapshot = new CacheSnapshot<T>(
        keptEntries.ToImmutableDictionary(),
        RebuildIndex(keptEntries));
    
    Interlocked.Exchange(ref _snapshot, newSnapshot);
    return removedCount;
}
```

## Redis Version Index (vidx)

The Redis version index is a lightweight secondary index that tracks which versions exist for each component. It enables "latest" and partial version resolution across pods without DB scans.

### Key Format

```
vidx:{componentKey}:{domain}:{key}  →  ["1.0.0-pkg.1.17.0+account", "1.1.0-pkg.1.18.0+account"]
```

### When vidx Gets Populated

vidx is **demand-driven** — it is never populated during startup. It only gets written via `CacheSet.SetAsync`, which calls `versionIndex.AddVersionAsync`. This happens in three scenarios:

| Scenario | Trigger | Flow |
|----------|---------|------|
| **Publish** | A new component version is deployed | `DefinitionAppService.PublishAsync` → `SetAsync` → `AddVersionAsync` |
| **GetAsync DB fallback** | in-memory miss + distributed cache miss | `GetAsync` → DB load → `SetAsync` → `AddVersionAsync` |
| **GetLatest/ByVersion DB fallback** | Both local snapshot and vidx are empty | `backend.LoadAllByKeyAsync` → `SetAsync` per entity → `AddVersionAsync` |

### When vidx Gets Read

vidx is consulted during version resolution in two methods:

- **`GetLatestByNameAsync`**: Always checks vidx to see if another pod published a newer version
- **`GetByVersionAsync`** (partial/artifact path): Checks vidx for smart version matching

Full-version lookups (`IsFullVersion` = contains `-pkg.`) skip vidx entirely and go directly to exact-key lookup.

### vidx Short-TTL Local Cache (`_vidxCache`)

To avoid redundant Redis roundtrips during execution bursts, vidx query results are cached locally in a `ConcurrentDictionary` with a **10-second TTL**. This is especially important because:

1. **Startup does not populate vidx** — `LoadAllAsync` only fills the in-memory snapshot, so vidx remains empty until the first `SetAsync` call
2. **Normal execution queries latest/null** — a single transition hop triggers 3-5+ component lookups, each of which would query Redis vidx without this cache
3. **Cross-pod freshness is handled by Dapr** — `ComponentPublishedEvent` proactively warms the local snapshot, making vidx consultation a secondary safety net

```csharp
private readonly ConcurrentDictionary<string, (SortedSet<string>? Versions, DateTime FetchedAt)> _vidxCache = new();
private static readonly TimeSpan VidxCacheTtl = TimeSpan.FromSeconds(10);
```

**Cache invalidation**: `SetAsync` and `InvalidateAsync` both call `_vidxCache.TryRemove(...)` after modifying the Redis vidx, ensuring local mutations are immediately visible.

**Null caching**: Both null and non-null results are cached. When vidx is empty (common after startup), caching null prevents repeated Redis roundtrips. The local snapshot still provides the correct version via `ChooseHigher`, and when both sources are empty, the DB fallback populates vidx via `SetAsync`.

### How Version Resolution Works

```
GetLatestByNameAsync / GetByVersionAsync(partial)
  │
  ├─ 1. Local snapshot index → localLatest / localBest
  ├─ 2. GetCachedVersionsAsync → vidx result (from cache or Redis)
  │     └─ indexLatest / indexBest
  ├─ 3. ChooseHigher(index, local) → authoritative version
  │
  ├─ IF authoritative is not empty:
  │     ├─ Fast path: snapshot has it → return from memory
  │     └─ Slow path: GetAsync(resolvedKey) → distributed cache → DB fallback
  │
  └─ IF authoritative is empty (both sources empty):
        └─ DB fallback: backend.LoadAllByKeyAsync → SetAsync (populates vidx + snapshot)
```

## Cross-Pod Cache Synchronization

### Publish Notification (Dapr Pub/Sub)

When a component is published, cross-pod synchronization happens via Dapr:

```
Publishing Pod                          Other Pods
─────────────                          ──────────
DB persist                             
  ↓                                    
SetAsync                               
  ├─ Snapshot upsert                   
  ├─ Redis body cache SET              
  └─ Redis vidx AddVersionAsync        
  ↓                                    
PublishComponentPublishedEventAsync     
  └─ Dapr broadcast ─────────────────→ POST /component-published
      (vnext-pubsub-broadcast)           ├─ Environment check
      topic: definition.component.       ├─ Domain check (workers)
             published                   └─ WarmComponentAsync
                                              └─ LoadFromDistributedCacheAsync
                                                   └─ Redis body GET → Snapshot upsert
```

**Key points:**
- The publishing pod writes to: in-memory snapshot + Redis body cache + Redis vidx
- Other pods receive a Dapr notification and warm their local snapshot from the Redis body cache
- vidx is not directly read during warm-up — the snapshot is populated with the exact version from the notification
- If Dapr notification fails, vidx serves as a safety net: the next `GetLatestByNameAsync` will discover the new version via Redis vidx

### Re-Initialize (Bulk Sync)

Triggered manually via `GET /definitions/re-initialize` or via `DefinitionCacheInvalidationEvent`:

```
Trigger Pod                             All Pods
───────────                             ────────
POST /re-initialize                     
  └─ Dapr broadcast ─────────────────→ POST /utilities/cache/invalidate
      topic: definition.cache.            └─ InitializeFromDistributedCacheAsync
             invalidate                        ├─ LoadAllEntityCacheKeysAsync (DB: keys only)
                                               └─ LoadFromDistributedCacheAsync (parallel)
                                                    └─ Per key: Redis GET → Snapshot upsert
                                                         (DB fallback on Redis miss)
```

## Startup vs Runtime Data Flow

### What Gets Populated at Startup

| Layer | `InitializeAsync` (startup) | `InitializeWithDistributedCacheAsync` | `InitializeFromDistributedCacheAsync` |
|-------|:-:|:-:|:-:|
| In-Memory Snapshot | Yes | Yes | Yes |
| Redis Body Cache | No | Yes | No |
| Redis vidx | No | No | No |
| vidx Local Cache | No | No | No |

The default startup path (`CacheInitializationHostedService`) calls `InitializeAsync(fullLoad: true)`, which **only** populates the in-memory snapshot from the database. Redis body cache and vidx remain empty until demand.

### Runtime Read Flow (Steady State)

For the most common case — `GetByVersionAsync` with `version=null` (latest):

```
GetByVersionAsync(domain, key, null)
  └─ IsRequestingLatest → true
       └─ GetLatestByNameAsync
            ├─ snap.Index → localLatest = "1.0.0-pkg..."  (from startup LoadAllAsync)
            ├─ GetCachedVersionsAsync
            │     └─ _vidxCache HIT (null, cached) → skip Redis
            ├─ ChooseHigher(null, localLatest) = localLatest
            └─ snap.Entries.TryGetValue → HIT → return from memory
                 (0 Redis calls, 0 DB calls)
```

### Runtime Write Flow (Publish)

```
SetAsync(entity)
  ├─ 1. SnapshotUpsert (in-memory, immediate)
  ├─ 2. distributedCache.SetAsync (Redis body cache)
  ├─ 3. versionIndex.AddVersionAsync (Redis vidx)
  └─ 4. _vidxCache.TryRemove (invalidate local vidx cache)
```

### When Distributed Cache (Redis Body) Is Read

The Redis body cache is **not** read during normal steady-state operation when the in-memory snapshot is warm. It is only read in these scenarios:

| Scenario | Method | Trigger |
|----------|--------|---------|
| **Cold miss** | `GetAsync` | Component not in snapshot (e.g., never loaded, evicted by cleanup) |
| **Warm-up after publish** | `LoadSingleKeyAsync` | Dapr `ComponentPublishedEvent` notification |
| **Bulk re-initialize** | `LoadFromDistributedCacheAsync` | `DefinitionCacheInvalidationEvent` or manual trigger |

## Monitoring and Diagnostics

### 1. Cache Performance Metrics

Metrics are collected by `MetricsAwareComponentCacheStore`, which wraps cache reads and records hit/miss:

```csharp
private async Task<Result<T>> ExecuteWithMetricsAsync<T>(
    string cacheTypeName,
    Func<Task<Result<T>>> operation)
{
    var result = await operation();
    if (result.IsSuccess)
    {
        workflowMetrics.RecordCacheHit(cacheTypeName);
    }
    else
    {
        workflowMetrics.RecordCacheMiss(cacheTypeName);
    }

    return result;
}
```

### 2. Metrics Integration

`MetricsAwareComponentCacheStore` decorates cache access to record hit/miss and approximate size metrics without changing cache behavior.

## Result Pattern Integration

The caching system has been fully integrated with the Railway (Result) Pattern to provide consistent, predictable error handling without relying on exceptions for control flow.

### Architecture Alignment

Following the Result Pattern guidelines:

- **Expected Errors (Cache Miss)** → `Result.NotFound` - Domain error, expected scenario
- **Validation Errors (Invalid Key)** → `Result.Validation` - Input validation failure
- **Distributed Cache Errors** → Logged and fallback continues to backend
- **Backend Infrastructure Errors** → Exceptions bubble up to middleware

### Error Handling Strategy

```csharp
// Cache miss is an expected domain condition - returns Result.NotFound
var result = await cacheStore.GetFlowAsync("banking", "loan-approval", "1.0.0", cancellationToken);

if (!result.IsSuccess)
{
    // Handle cache miss gracefully
    logger.LogInformation("Workflow not found in cache: {ErrorCode}", result.Error.Code);
    // Fallback logic here
}

var workflow = result.Value;
```

### Interface Signatures

All cache operations now return `Result<T>` or `Result`:

```csharp
public interface IComponentCacheStore
{
    // Returns Result<T> with NotFound error if entity doesn't exist
    Task<Result<Workflow>> GetFlowAsync(string domain, string key, string? version, CancellationToken cancellationToken = default);
    Task<Result<WorkflowTask>> GetTaskAsync(string domain, string key, string? version, CancellationToken cancellationToken = default);
    Task<Result<SchemaDefinition>> GetSchemaAsync(string domain, string key, string? version, CancellationToken cancellationToken = default);
    Task<Result<Function>> GetFunctionAsync(string domain, string key, string? version, CancellationToken cancellationToken = default);
    Task<Result<View>> GetViewAsync(string domain, string key, string? version, CancellationToken cancellationToken = default);
    Task<Result<Extension>> GetExtensionAsync(string domain, string key, string? version, CancellationToken cancellationToken = default);
    
    // Returns Result<IEnumerable<T>> - empty collection is success, not an error
    Task<Result<IEnumerable<Extension>>> GetAllExtensionsAsync(string domain, CancellationToken cancellationToken = default);
    
    // Returns Result indicating success/failure of cache write
    Task<Result> SetAsync<T>(T entity, CancellationToken cancellationToken = default) where T : class, IDomainEntity, IReferenceSetter;
}
```

### Error Codes

Cache-specific error codes are defined in `WorkflowErrorCodes`:

```csharp
public static class WorkflowErrorCodes
{
    #region Cache Errors (300xxx)
    
    public const string CacheItemNotFound = "Cache:300001";
    public const string CacheInvalidKey = "Cache:300002";
    public const string CacheTypeNotSupported = "Cache:300003";
    
    #endregion
}
```

### Cache Backend Implementation

`RuntimeCacheBackend` loads entities through `IRuntimeService` and applies smart version matching:

```csharp
public async Task<Result<T>> LoadAsync(
    string domain,
    string key,
    string? version,
    CancellationToken cancellationToken = default)
{
    runtimeInfoProvider.Check(domain);

    if (InstanceDataVersionComparer.IsFullVersion(version))
    {
        var entity = await runtimeService.GetAsync<T>(key, version!, cancellationToken);
        return entity is null
            ? Result<T>.Fail(CacheErrors.ItemNotFoundInBackend<T>(domain, key, version))
            : Result<T>.Ok(entity);
    }

    var all = await runtimeService.GetAsync<T>(cancellationToken);
    var bestMatchVersion = InstanceDataVersionComparer.FindBestMatch(
        all.Where(e => e is not null &&
                       string.Equals(e.Domain, domain, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase))
           .Select(e => e!.Version)
           .ToList(),
        version);

    if (string.IsNullOrEmpty(bestMatchVersion))
        return Result<T>.Fail(CacheErrors.ItemNotFoundInBackend<T>(domain, key, version));

    var matched = all.FirstOrDefault(e =>
        e is not null &&
        string.Equals(e.Domain, domain, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(e.Version, bestMatchVersion, StringComparison.OrdinalIgnoreCase));

    return matched is null
        ? Result<T>.Fail(CacheErrors.ItemNotFoundInBackend<T>(domain, key, version))
        : Result<T>.Ok(matched);
}
```

### Multi-Layer Cache Flow with Result Pattern

The cache uses a three-tier lookup strategy with the Result pattern:

```
┌─────────────────────────────────────────────────────────┐
│                    Cache Lookup Flow                     │
├─────────────────────────────────────────────────────────┤
│  1. Local Snapshot (Lock-free)                          │
│     ├─ HIT → Return Result<T>.Ok(value)                 │
│     └─ MISS → Continue to step 2                        │
├─────────────────────────────────────────────────────────┤
│  2. Distributed Cache (Aether IDistributedCacheService) │
│     ├─ HIT → Update local snapshot → Return Ok          │
│     ├─ MISS → Continue to step 3                        │
│     └─ ERROR → Log and continue to step 3               │
├─────────────────────────────────────────────────────────┤
│  3. Runtime Backend (ICacheBackend<T>)                  │
│     ├─ FOUND → Update both caches → Return Ok           │
│     └─ NOT_FOUND → Return Result<T>.Fail(NotFound)      │
└─────────────────────────────────────────────────────────┘
```

**Key Points:**
- Local cache uses immutable snapshots for lock-free reads
- Distributed cache errors are logged but don't fail the request
- Runtime backend is the ultimate source of truth
- Result pattern provides explicit success/failure handling

### Metrics Integration with Result Pattern

The `MetricsAwareComponentCacheStore` decorator uses Result pattern to determine cache hit/miss:

```csharp
private async Task<Result<T>> ExecuteWithMetricsAsync<T>(string cacheTypeName, Func<Task<Result<T>>> operation)
{
    try
    {
        var result = await operation();
        
        if (result.IsSuccess)
        {
            // Record cache hit for successful retrieval
            workflowMetrics.RecordCacheHit(cacheTypeName);
            logger.LogDebug("Cache hit for {CacheType}", cacheTypeName);
        }
        else
        {
            // Record cache miss for not found
            workflowMetrics.RecordCacheMiss(cacheTypeName);
            logger.LogDebug("Cache miss for {CacheType}: {ErrorCode}", cacheTypeName, result.Error.Code);
        }
        
        return result;
    }
    catch (Exception ex)
    {
        // Infrastructure exceptions are logged and re-thrown
        workflowMetrics.RecordCacheMiss(cacheTypeName);
        logger.LogWarning(ex, "Cache operation failed for {CacheType}", cacheTypeName);
        throw;
    }
}
```

### Benefits of Result Pattern in Caching

1. **Explicit Error Handling**: Cache misses are explicit in the API, not hidden behind exceptions
2. **Performance**: No exception overhead for expected cache miss scenarios
3. **Composability**: Cache operations compose naturally with other Result-based operations
4. **Clarity**: Clear distinction between expected (cache miss) and unexpected (network error) failures
5. **Metrics**: Accurate cache hit/miss tracking based on Result success/failure

### Migration Guide

For existing code using exception-based cache API:

**Before (Exception-based):**
```csharp
try
{
    var workflow = await cacheStore.GetFlowAsync("banking", "loan", "1.0", ct);
    // Use workflow
}
catch (EntityNotFoundException ex)
{
    // Handle cache miss
}
```

**After (Result-based):**
```csharp
var workflowResult = await cacheStore.GetFlowAsync("banking", "loan", "1.0", ct);

if (!workflowResult.IsSuccess)
{
    // Handle cache miss (expected scenario)
    return Result<T>.Fail(workflowResult.Error);
}

var workflow = workflowResult.Value;
// Use workflow
```

## Usage Examples

### 1. Retrieving Workflow Definitions

```csharp
// Get latest version with Result pattern
var result = await componentCache.GetFlowAsync("banking", "loan-approval", null, cancellationToken);

if (!result.IsSuccess)
{
    logger.LogDebug("Workflow not found: {Error}", result.Error.Message);
    return Result<WorkflowInstance>.Fail(result.Error);
}

var workflow = result.Value;

// Get specific version
var workflowV2Result = await componentCache.GetFlowAsync("banking", "loan-approval", "2.1.0", cancellationToken);
```

### 2. Caching Custom Data with Result Pattern

```csharp
// Store in cache with Result pattern
var workflow = new Workflow { Domain = "banking", Key = "loan-approval", Version = "1.0.0" };
var setResult = await componentCache.SetAsync(workflow, cancellationToken);

if (!setResult.IsSuccess)
{
    logger.LogError("Failed to cache workflow: {Error}", setResult.Error.Message);
    return setResult;
}

// Retrieve from cache
var cachedResult = await componentCache.GetFlowAsync("banking", "loan-approval", "1.0.0", cancellationToken);

if (cachedResult.IsSuccess)
{
    var cached = cachedResult.Value;
    // Use cached workflow
}
```

### 3. Bulk Operations

```csharp
// Load multiple workflows
var workflows = new List<Workflow> { workflow1, workflow2, workflow3 };
await cacheContext.Workflows.LoadAllAsync(workflows, cancellationToken);
```

## Best Practices

### 1. Cache Key Design
- Use consistent naming conventions
- Include all identifying information (domain, key, version)
- Keep keys readable for debugging

### 2. Expiration Strategy
- Set appropriate TTL based on data volatility
- Use sliding expiration for frequently accessed data
- Implement cache warming for critical data

### 3. Error Handling with Result Pattern
- **Cache Miss (Expected)**: Return `Result.NotFound` - this is a domain condition, not an error to throw
- **Validation Errors**: Return `Result.Validation` for invalid cache keys or entity data
- **Distributed Cache Errors**: Log and continue to backend fallback
- **Backend Infrastructure Errors**: Let exceptions bubble up (DB connection, runtime access) - middleware will handle
- **Never use `Result.Try`** for cache operations - cache miss is expected, not exceptional
- Always check `Result.IsSuccess` before accessing `Result.Value`
- Use Railway Pattern for composing cache operations with business logic
- Log cache misses at Debug level, not Warning (they're expected)

### 4. Memory Management
- Monitor local cache size
- Implement LRU eviction if needed
- Use weak references for large objects

### 5. Result Pattern Guidelines for Cache
- **DO** return `Result<T>` for Get operations
- **DO** return `Result` for Set/Invalidate operations  
- **DO** use `Result.NotFound` for cache misses
- **DO** let backend infrastructure exceptions throw naturally
- **DON'T** wrap cache operations in try-catch unless handling specific infrastructure errors
- **DON'T** use `Result.Try` - cache miss is an expected domain scenario
- **DON'T** treat cache miss as an exception - it's normal flow

The caching strategy provides excellent performance while maintaining data consistency across the distributed workflow engine, enabling high-throughput workflow processing with minimal backend load. The Result Pattern integration ensures predictable, composable error handling aligned with Railway Pattern best practices. 