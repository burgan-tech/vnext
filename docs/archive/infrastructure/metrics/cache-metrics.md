# Cache Metrics

## Overview

The cache metrics system provides comprehensive monitoring of workflow component caching operations including hit/miss rates, cache size, entry counts, and eviction patterns.

## Metrics Categories

### Counter Metrics

#### `workflow_cache_hits_total{cache_name}`
- **Description**: Total number of cache hits
- **Labels**: `cache_name` - Type of cached component
- **Example Values**:
  ```yaml
  workflow_cache_hits_total{cache_name="Workflow"} 1250
  workflow_cache_hits_total{cache_name="WorkflowTask"} 3400
  workflow_cache_hits_total{cache_name="Function"} 850
  ```

#### `workflow_cache_misses_total{cache_name}`
- **Description**: Total number of cache misses
- **Labels**: `cache_name` - Type of cached component
- **Example Values**:
  ```yaml
  workflow_cache_misses_total{cache_name="Workflow"} 45
  workflow_cache_misses_total{cache_name="WorkflowTask"} 120
  workflow_cache_misses_total{cache_name="Extension"} 15
  ```

#### `workflow_cache_evictions_total{cache_name, reason}`
- **Description**: Total number of cache evictions
- **Labels**: 
  - `cache_name` - Type of cached component
  - `reason` - Eviction reason (manual, clear, ttl, size, etc.)
- **Example Values**:
  ```yaml
  workflow_cache_evictions_total{cache_name="Workflow", reason="manual"} 5
  workflow_cache_evictions_total{cache_name="WorkflowTask", reason="clear"} 25
  workflow_cache_evictions_total{cache_name="SchemaDefinition", reason="ttl"} 12
  ```

### Gauge Metrics

#### `workflow_cache_size_bytes{cache_name}`
- **Description**: Current cache size in bytes
- **Labels**: `cache_name` - Type of cached component
- **Example Values**:
  ```yaml
  workflow_cache_size_bytes{cache_name="Workflow"} 204800     # ~200KB
  workflow_cache_size_bytes{cache_name="WorkflowTask"} 460800 # ~450KB
  workflow_cache_size_bytes{cache_name="Function"} 30720     # ~30KB
  ```

#### `workflow_cache_entries{cache_name}`
- **Description**: Number of cached entries
- **Labels**: `cache_name` - Type of cached component
- **Example Values**:
  ```yaml
  workflow_cache_entries{cache_name="Workflow"} 25
  workflow_cache_entries{cache_name="WorkflowTask"} 150
  workflow_cache_entries{cache_name="View"} 10
  ```

## Cache Component Types

The system monitors these workflow component caches:

### Core Components
- **`Workflow`** - Workflow definitions
- **`WorkflowTask`** - Task definitions
- **`Function`** - Function definitions
- **`SchemaDefinition`** - Schema definitions
- **`View`** - View definitions
- **`Extension`** - Extension definitions

### Bulk Operations
- **`Extension_Bulk`** - Bulk extension retrievals

## Implementation

### Automatic Metrics Collection

The cache metrics are automatically collected through the `MetricsAwareComponentCacheStore` decorator pattern:

```csharp
// Automatic registration in DI container
services.AddSingleton<ComponentCacheStore>();
services.AddSingleton<IComponentCacheStore>(serviceProvider =>
{
    var originalStore = serviceProvider.GetRequiredService<ComponentCacheStore>();
    var workflowMetrics = serviceProvider.GetRequiredService<IWorkflowMetrics>();
    var logger = serviceProvider.GetRequiredService<ILogger<MetricsAwareComponentCacheStore>>();
    
    return originalStore.WithMetrics(workflowMetrics, logger);
});
```

### Hit/Miss Detection

```csharp
// Cache hit scenario
var workflow = await componentCacheStore.GetFlowAsync("domain", "workflow-key", "v1");
// ↓ Records: workflow_cache_hits_total{cache_name="Workflow"} +1

// Cache miss scenario (entity not found)
try 
{
    var workflow = await componentCacheStore.GetFlowAsync("domain", "missing-workflow", "v1");
}
catch (EntityNotFoundException)
{
    // ↓ Records: workflow_cache_misses_total{cache_name="Workflow"} +1
}
```

### Size and Entry Metrics

The system provides estimated cache size and entry count metrics:

```csharp
// After cache operations, metrics are automatically updated:
workflow_cache_size_bytes{cache_name="Workflow"} 204800
workflow_cache_entries{cache_name="Workflow"} 25
```

## Monitoring and Alerting

### Cache Hit Rate

Calculate cache hit rate using Prometheus queries:

```promql
# Cache hit rate by component type
(
  rate(workflow_cache_hits_total[5m]) / 
  (rate(workflow_cache_hits_total[5m]) + rate(workflow_cache_misses_total[5m]))
) * 100
```

### Cache Efficiency Alerts

```yaml
# Low cache hit rate alert
- alert: LowCacheHitRate
  expr: |
    (
      rate(workflow_cache_hits_total[5m]) / 
      (rate(workflow_cache_hits_total[5m]) + rate(workflow_cache_misses_total[5m]))
    ) * 100 < 80
  for: 5m
  labels:
    severity: warning
  annotations:
    summary: "Cache hit rate is below 80% for {{ $labels.cache_name }}"

# High cache eviction rate alert  
- alert: HighCacheEvictionRate
  expr: rate(workflow_cache_evictions_total[5m]) > 10
  for: 2m
  labels:
    severity: warning
  annotations:
    summary: "High cache eviction rate for {{ $labels.cache_name }}"
```

### Memory Usage Monitoring

```promql
# Total cache memory usage
sum(workflow_cache_size_bytes) by (cache_name)

# Cache memory usage growth rate
rate(workflow_cache_size_bytes[5m])
```

## Cache Performance Analysis

### Dashboard Queries

#### Cache Hit Rate Over Time
```promql
rate(workflow_cache_hits_total[5m]) / 
(rate(workflow_cache_hits_total[5m]) + rate(workflow_cache_misses_total[5m]))
```

#### Cache Size Trends
```promql
workflow_cache_size_bytes
```

#### Most Accessed Components
```promql
topk(10, rate(workflow_cache_hits_total[5m]))
```

#### Cache Eviction Patterns
```promql
rate(workflow_cache_evictions_total[5m]) by (reason)
```

## Best Practices

### Cache Optimization

1. **Monitor Hit Rates**: Maintain >85% hit rate for optimal performance
2. **Size Management**: Monitor memory usage to prevent excessive growth
3. **Eviction Analysis**: Understand eviction patterns to optimize cache policies

### Alerting Strategy

1. **Hit Rate Alerts**: Alert when hit rate drops below threshold
2. **Memory Alerts**: Alert when cache size grows unexpectedly
3. **Eviction Alerts**: Monitor unusual eviction patterns

### Performance Tuning

1. **Preload Critical Components**: Cache frequently accessed workflows
2. **Size Limits**: Configure appropriate cache size limits
3. **TTL Optimization**: Balance cache freshness with hit rates

## Integration Examples

### Custom Cache Monitoring

```csharp
// Manual cache metrics recording (if needed)
public class CustomCacheService
{
    private readonly IWorkflowMetrics _metrics;
    
    public async Task<T> GetWithCustomMetrics<T>(string key)
    {
        try
        {
            var result = await GetFromCache<T>(key);
            _metrics.RecordCacheHit(typeof(T).Name);
            return result;
        }
        catch (NotFoundException)
        {
            _metrics.RecordCacheMiss(typeof(T).Name);
            throw;
        }
    }
}
```

### Cache Size Estimation

The system provides size estimates based on component types:

- **Workflow**: ~8KB per definition
- **WorkflowTask**: ~3KB per task
- **Function**: ~1.5KB per function
- **SchemaDefinition**: ~4KB per schema
- **View**: ~2KB per view
- **Extension**: ~1KB per extension

These estimates help with capacity planning and memory usage monitoring.

The cache metrics system provides comprehensive visibility into caching performance, enabling data-driven optimization of your workflow system's performance! 📊