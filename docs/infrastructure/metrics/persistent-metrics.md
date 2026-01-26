# Persistent Metrics System

## Overview

The persistent metrics system ensures workflow metrics are preserved across application restarts by storing metric values in distributed cache (Redis). This provides continuous monitoring without data loss during deployments or server restarts.

## Architecture

### Components

#### **MetricsStateManager** 
- Manages persistent storage of metric values
- Tracks counters and gauges in distributed cache
- Provides restore and persistence operations

#### **CacheBackedPrometheusWorkflowMetrics**
- Decorator that wraps PrometheusWorkflowMetrics
- Automatically persists metric updates to cache
- Restores metrics from cache on startup

#### **MetricsPersistenceService**
- Hosted service for periodic metric persistence
- Runs every 5 minutes to sync metrics to cache
- Handles final persistence on application shutdown

#### **MetricsInitializationHostedService**
- Restores metrics from cache on application startup
- Ensures metric continuity across restarts

## How It Works

### **Metric Recording Flow**
```csharp
// 1. Application records metric
workflowMetrics.RecordInstanceCreated("UserWorkflow", "Production");

// 2. CacheBackedPrometheusWorkflowMetrics processes:
innerMetrics.RecordInstanceCreated(workflow, domain);           // ← Prometheus
stateManager.IncrementCounterAsync("instances_created", labels); // ← Cache

// 3. MetricsStateManager persists to Redis
await distributedCache.SetAsync("workflow_metrics_state_counter:instances_created:UserWorkflow,Production", "42");
```

### **Application Startup Flow**
```csharp
// 1. MetricsInitializationHostedService starts
// 2. Loads metric values from Redis cache
// 3. Restores Prometheus counters/gauges to cached values
WorkflowMetrics.InstancesCreated.WithLabels("UserWorkflow", "Production").IncTo(42);
```

### **Periodic Persistence**
```csharp
// Every 5 minutes, MetricsPersistenceService:
// 1. Collects all current metric values
// 2. Persists to distributed cache
// 3. Ensures metric state is always fresh
```

## Supported Metrics

### **Persistent Counters**
These counters maintain their values across restarts:

```yaml
# Instance metrics
workflow_instances_created_total{workflow, domain}
workflow_instances_completed_total{workflow, domain}
workflow_instances_timeout_total{workflow, domain}

# Task metrics
workflow_tasks_executed_total{task_type, workflow}
workflow_tasks_completed_total{task_type, workflow}
workflow_tasks_failed_total{task_type, workflow}
workflow_tasks_retried_total{task_type, workflow}

# State metrics
workflow_state_transitions_total{workflow, from_state, to_state}
workflow_state_entries_total{workflow, state}

# Cache metrics
workflow_cache_hits_total{cache_name}
workflow_cache_misses_total{cache_name}
workflow_cache_evictions_total{cache_name, reason}

# HTTP metrics
http_requests_total{method, endpoint, status_code}
http_request_errors_total{method, endpoint, error_type}

# Database metrics
workflow_db_queries_total{query_type, table, status}
workflow_db_errors_total{operation, table, error_type}
workflow_db_connections_total{connection_type, status}

# Task factory metrics
task_factory_pool_rentals_total{task_type}
task_factory_pool_returns_total{task_type}
task_factory_pool_creates_total{task_type}

# Background job metrics
background_jobs_executed_total{job_type, status}

# Error metrics
workflow_errors_total{error_type, severity, component}
```

### **Persistent Gauges**
These gauges maintain their current state across restarts:

```yaml
# Instance gauges
workflow_instances_active{workflow}
workflow_instances_suspended{workflow}
workflow_instances_pending{workflow}

# Task gauges
workflow_tasks_pending{task_type, workflow}
workflow_tasks_running{task_type, workflow}

# Pool gauges
task_factory_pool_size{task_type}
task_factory_pool_available{task_type}
task_factory_pool_in_use{task_type}

# Cache gauges
workflow_cache_size_bytes{cache_name}
workflow_cache_entries{cache_name}

# Background job gauges
background_jobs_pending{job_type}
```

### **Non-Persistent Histograms**
Histograms are not persisted as they represent statistical distributions:

- `workflow_instance_duration_seconds`
- `workflow_task_duration_seconds`
- `workflow_state_duration_seconds`
- `http_request_duration_seconds`
- `http_response_size_bytes`
- `workflow_db_query_duration_seconds`
- `workflow_db_transaction_duration_seconds`

## Configuration

### **Automatic Registration**
```csharp
// WorkflowInfrastructureModuleServiceCollectionExtensions.cs
services.AddSingleton<PrometheusWorkflowMetrics>();
services.AddSingleton<MetricsStateManager>();
services.AddSingleton<IWorkflowMetrics, CacheBackedPrometheusWorkflowMetrics>();
services.AddHostedService<MetricsPersistenceService>();
services.AddHostedService<MetricsInitializationHostedService>();
```

### **Cache Settings**
- **Cache Duration**: 7 days sliding expiration
- **Persistence Interval**: 5 minutes
- **Cache Key Prefix**: `workflow_metrics_state_`

## Benefits

### **📊 Continuous Monitoring**
- Metrics survive application restarts
- Long-term trend analysis possible
- No data loss during deployments

### **🚀 Performance**
- Async persistence doesn't block operations
- In-memory tracking for fast access
- Periodic batching reduces cache load

### **🛡️ Reliability**
- Graceful degradation if cache is unavailable
- Error handling prevents operation failures
- Fallback to non-persistent mode

## Usage Examples

### **Before Restart**
```yaml
workflow_instances_created_total{workflow="UserRegistration", domain="Production"} 1500
workflow_tasks_executed_total{task_type="DaprServiceTask", workflow="UserRegistration"} 4200
workflow_cache_hits_total{cache_name="Workflow"} 2800
```

### **After Restart**
```yaml
# Same values restored from cache
workflow_instances_created_total{workflow="UserRegistration", domain="Production"} 1500
workflow_tasks_executed_total{task_type="DaprServiceTask", workflow="UserRegistration"} 4200
workflow_cache_hits_total{cache_name="Workflow"} 2800
```

### **Monitoring Dashboard Continuity**
- Grafana dashboards show continuous trends
- No gaps in metrics during restarts
- Historical data preserved for analysis

## Troubleshooting

### **Cache Unavailable**
If Redis cache is unavailable:
- System falls back to non-persistent metrics
- Application continues to function normally
- Metrics resume persistence when cache is restored

### **Metrics Restoration Issues**
- Check Redis connectivity
- Verify cache key format consistency
- Monitor logs for restoration errors

### **Performance Impact**
- Async operations don't block request processing
- Periodic persistence batches reduce overhead
- In-memory tracking provides fast access

The persistent metrics system ensures your workflow monitoring data survives restarts while maintaining high performance! 🚀📊