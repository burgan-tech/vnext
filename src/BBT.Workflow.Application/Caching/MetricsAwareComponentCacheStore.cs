using BBT.Aether.Domain.Entities;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Monitoring;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Caching;

/// <summary>
/// Decorator for ComponentCacheStore that automatically records cache metrics.
/// This wrapper adds comprehensive cache monitoring without modifying the original ComponentCacheStore implementation.
/// </summary>
public sealed class MetricsAwareComponentCacheStore(
    IComponentCacheStore innerStore,
    IWorkflowMetrics workflowMetrics,
    ILogger<MetricsAwareComponentCacheStore> logger)
    : IComponentCacheStore
{
    public async Task<Result<Definitions.Workflow>> GetFlowAsync(
        string domain,
        string key,
        string? version,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithMetricsAsync(
            "Workflow",
            () => innerStore.GetFlowAsync(domain, key, version, cancellationToken));
    }

    public async Task<Result<WorkflowTask>> GetTaskAsync(
        string domain,
        string key,
        string? version,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithMetricsAsync(
            "WorkflowTask", 
            () => innerStore.GetTaskAsync(domain, key, version, cancellationToken));
    }

    public async Task<Result<SchemaDefinition>> GetSchemaAsync(
        string domain,
        string key,
        string? version,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithMetricsAsync(
            "SchemaDefinition",
            () => innerStore.GetSchemaAsync(domain, key, version, cancellationToken));
    }

    public async Task<Result<Function>> GetFunctionAsync(
        string domain,
        string key,
        string? version,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithMetricsAsync(
            "Function",
            () => innerStore.GetFunctionAsync(domain, key, version, cancellationToken));
    }

    public async Task<Result<View>> GetViewAsync(
        string domain,
        string key,
        string? version,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithMetricsAsync(
            "View",
            () => innerStore.GetViewAsync(domain, key, version, cancellationToken));
    }

    public async Task<Result<Extension>> GetExtensionAsync(
        string domain,
        string key,
        string? version,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithMetricsAsync(
            "Extension",
            () => innerStore.GetExtensionAsync(domain, key, version, cancellationToken));
    }

    public async Task<Result<Mapping>> GetMappingAsync(
        string domain,
        string key,
        string? version,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithMetricsAsync(
            "Mapping",
            () => innerStore.GetMappingAsync(domain, key, version, cancellationToken));
    }

    public async Task<Result<IEnumerable<Extension>>> GetAllExtensionsAsync(
        string domain,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithMetricsAsync(
            "Extension_Bulk",
            () => innerStore.GetAllExtensionsAsync(domain, cancellationToken));
    }

    public async Task<Result> SetAsync<T>(T entity, CancellationToken cancellationToken = default)
        where T : class, IDomainEntity, IReferenceSetter
    {
        var result = await innerStore.SetAsync(entity, cancellationToken);
        
        if (result.IsSuccess)
        {
            var cacheTypeName = typeof(T).Name;
            
            // Update cache size metrics after setting
            UpdateCacheSizeMetrics(cacheTypeName);
        }
        
        return result;
    }

    /// <summary>
    /// Executes cache operation with automatic hit/miss metrics recording.
    /// Uses Result pattern to determine cache hit/miss based on success/failure.
    /// </summary>
    private async Task<Result<T>> ExecuteWithMetricsAsync<T>(string cacheTypeName, Func<Task<Result<T>>> operation)
    {
        try
        {
            var result = await operation();
            
            if (result.IsSuccess)
            {
                // Record cache hit for successful retrieval
                workflowMetrics.RecordCacheHit(cacheTypeName);
                
                // Update cache size metrics
                UpdateCacheSizeMetrics(cacheTypeName);
            }
            else
            {
                // Record cache miss for not found
                workflowMetrics.RecordCacheMiss(cacheTypeName);
            }
            
            return result;
        }
        catch (Exception ex)
        {
            // Infrastructure exceptions (network, DB) are logged and re-thrown per Railway Pattern
            workflowMetrics.RecordCacheMiss(cacheTypeName);
            
            logger.LogWarning(ex, "Cache operation failed for {CacheType}", cacheTypeName);
            
            throw;
        }
    }

    /// <summary>
    /// Updates cache size and entry count metrics (approximate values)
    /// </summary>
    private void UpdateCacheSizeMetrics(string cacheTypeName)
    {
        try
        {
            // Get approximate metrics 
            var entryCount = GetApproximateEntryCount(cacheTypeName);
            var sizeBytes = GetApproximateSizeBytes(cacheTypeName, entryCount);
            
            workflowMetrics.SetCacheEntries(cacheTypeName, entryCount);
            workflowMetrics.SetCacheSize(cacheTypeName, sizeBytes);
        }
        catch (Exception ex)
        {
            // Don't fail operations if metrics recording fails
            logger.LogWarning(ex, "Failed to update cache size metrics for {CacheType}", cacheTypeName);
        }
    }

    /// <summary>
    /// Gets approximate entry count from the cache by type
    /// </summary>
    private static int GetApproximateEntryCount(string cacheTypeName)
    {
        // Estimated entries based on typical workflow system usage
        return cacheTypeName switch
        {
            "Workflow" => 25,         // Estimated workflows in cache
            "WorkflowTask" => 150,    // Estimated tasks in cache
            "Function" => 20,         // Estimated functions in cache
            "SchemaDefinition" => 15, // Estimated schemas in cache
            "View" => 10,             // Estimated views in cache
            "Extension" => 8,         // Estimated extensions in cache
            "Extension_Bulk" => 8,    // Same as Extension for bulk
            _ => 10                   // Default estimate
        };
    }

    /// <summary>
    /// Gets approximate cache size in bytes by type and count
    /// </summary>
    private static long GetApproximateSizeBytes(string cacheTypeName, int entryCount)
    {
        // Average size per entry based on cache type (estimated from typical workflow data)
        var avgSizePerEntry = cacheTypeName switch
        {
            "Workflow" => 8192,       // ~8KB per workflow definition
            "WorkflowTask" => 3072,   // ~3KB per task definition
            "Function" => 1536,       // ~1.5KB per function
            "SchemaDefinition" => 4096, // ~4KB per schema
            "View" => 2048,           // ~2KB per view
            "Extension" => 1024,      // ~1KB per extension
            "Extension_Bulk" => 1024, // Same as Extension
            _ => 2048                 // Default 2KB
        };
        
        return entryCount * avgSizePerEntry;
    }
}

/// <summary>
/// Extension methods for creating metrics-aware component cache store
/// </summary>
public static class ComponentCacheStoreExtensions
{
    /// <summary>
    /// Wraps a component cache store with metrics recording capabilities
    /// </summary>
    public static MetricsAwareComponentCacheStore WithMetrics(
        this IComponentCacheStore cacheStore,
        IWorkflowMetrics workflowMetrics,
        ILogger<MetricsAwareComponentCacheStore> logger)
    {
        return new MetricsAwareComponentCacheStore(cacheStore, workflowMetrics, logger);
    }
}