# Database Metrics Interceptor

## Overview

The `WorkflowDatabaseInterceptor` provides comprehensive, automatic database metrics collection for all Entity Framework Core operations across the entire workflow system. This eliminates the need for manual metrics recording in individual repositories.

## Features

### ✅ **Automatic Coverage**
- **All Repositories**: Every EF Core repository automatically gets metrics
- **All Operations**: SELECT, INSERT, UPDATE, DELETE, transactions
- **Zero Code Changes**: No manual instrumentation required
- **Consistent Metrics**: Same metrics format across all database operations

### 📊 **Collected Metrics**

#### **Query Metrics**
- `workflow_db_queries_total{query_type, table, status}` - Total query count by type and status
- `workflow_db_query_duration_seconds{query_type, table}` - Query execution time

#### **Transaction Metrics**  
- `workflow_db_connections_total{connection_type, status}` - Connection and transaction events
- `workflow_db_transaction_duration_seconds{operation}` - Transaction duration

#### **Error Metrics**
- `workflow_db_errors_total{operation, table, error_type}` - Database errors by type

## How It Works

### **EF Core Interceptor Pattern**
The interceptor hooks into EF Core's command pipeline and automatically captures:

```csharp
// Before query execution
ReaderExecutingAsync() → Records "started" event
↓
// After successful execution  
ReaderExecutedAsync() → Records "success" + duration
↓
// On error
CommandFailedAsync() → Records "error" + error type
```

### **SQL Analysis**
The interceptor intelligently parses SQL commands to extract:
- **Query Type**: SELECT, INSERT, UPDATE, DELETE
- **Table Name**: Primary table being accessed
- **Operation Context**: For metrics labeling

## Repository Coverage

### **Covered Repositories**
All EF Core repositories automatically get metrics:

- ✅ `EfCoreInstanceRepository` - All CRUD operations
- ✅ `EfCoreInstanceTransitionRepository` - All CRUD operations  
- ✅ `EfCoreInstanceTaskRepository` - All CRUD operations
- ✅ `EfCoreInstanceCorrelationRepository` - All CRUD operations
- ✅ `EfCoreInstanceJobRepository` - All CRUD operations
- ✅ **Any New Repository** - Automatic coverage

### **Operation Coverage**

#### **Query Operations**
```csharp
// SELECT operations
var instance = await repository.FindAsync(id);
var instances = await repository.GetListAsync();
// ↓ Metrics: workflow_db_queries_total{query_type="SELECT", table="Instances", status="success"}

// INSERT operations  
await repository.InsertAsync(instance);
// ↓ Metrics: workflow_db_queries_total{query_type="INSERT", table="Instances", status="success"}

// UPDATE operations
await repository.UpdateAsync(instance); 
// ↓ Metrics: workflow_db_queries_total{query_type="UPDATE", table="Instances", status="success"}

// DELETE operations
await repository.DeleteAsync(instance);
// ↓ Metrics: workflow_db_queries_total{query_type="DELETE", table="Instances", status="success"}
```

#### **Transaction Operations**
```csharp
using var transaction = await dbContext.Database.BeginTransactionAsync();
try
{
    // Multiple operations...
    await transaction.CommitAsync();
    // ↓ Metrics: workflow_db_connections_total{connection_type="transaction", status="committed"}
}
catch
{
    await transaction.RollbackAsync();
    // ↓ Metrics: workflow_db_connections_total{connection_type="transaction", status="rollback"}
}
```

## Example Metrics Output

```yaml
# Query Performance
workflow_db_query_duration_seconds_bucket{query_type="SELECT",table="Instances",le="0.005"} 1250
workflow_db_query_duration_seconds_bucket{query_type="INSERT",table="InstanceTransitions",le="0.01"} 420
workflow_db_query_duration_seconds_bucket{query_type="UPDATE",table="Instances",le="0.025"} 180

# Query Volume  
workflow_db_queries_total{query_type="SELECT",table="Instances",status="success"} 15420
workflow_db_queries_total{query_type="INSERT",table="InstanceTransitions",status="success"} 2100
workflow_db_queries_total{query_type="UPDATE",table="Instances",status="error"} 12

# Transaction Health
workflow_db_connections_total{connection_type="transaction",status="committed"} 1850
workflow_db_connections_total{connection_type="transaction",status="rollback"} 45

# Error Analysis
workflow_db_errors_total{operation="INSERT",table="Instances",error_type="SqlException"} 5
workflow_db_errors_total{operation="SELECT",table="InstanceTransitions",error_type="TimeoutException"} 2
```

## Benefits

### **🚀 Performance Monitoring**
- Identify slow queries across all repositories
- Track query patterns and optimization opportunities
- Monitor transaction performance

### **📈 Volume Analysis** 
- Understand database load patterns
- Identify high-traffic tables
- Plan capacity and indexing

### **🛡️ Error Tracking**
- Catch database issues early
- Identify problematic queries
- Monitor connection stability

### **🔧 Zero Maintenance**
- No manual instrumentation needed
- Automatic coverage for new repositories
- Consistent metrics across the system

## Configuration

The interceptor is automatically registered and requires no configuration:

```csharp
// Automatic registration in WorkflowInfrastructureModuleServiceCollectionExtensions
services.AddScoped<WorkflowDatabaseInterceptor>();

// Automatic integration in WorkflowDbContextFactory  
builder.AddInterceptors(databaseInterceptor);
```

## Migration from Manual Metrics

When migrating from manual database metrics:

### **Before** (Manual, Repository-Specific)
```csharp
public override async Task<Instance> InsertAsync(Instance entity, ...)
{
    var stopwatch = Stopwatch.StartNew();
    try 
    {
        workflowMetrics.RecordDbQuery("INSERT", "Instances", "started");
        var result = await base.InsertAsync(entity, ...);
        stopwatch.Stop();
        workflowMetrics.RecordDbQuery("INSERT", "Instances", "success");
        workflowMetrics.RecordDbQueryDuration("INSERT", "Instances", stopwatch.Elapsed.TotalSeconds);
        return result;
    }
    catch (Exception ex)
    {
        // Manual error tracking...
    }
}
```

### **After** (Automatic, All Repositories)
```csharp
public override async Task<Instance> InsertAsync(Instance entity, ...)
{
    // Database metrics automatically recorded by interceptor
    var result = await base.InsertAsync(entity, ...);
    
    // Only business-specific metrics needed
    workflowMetrics.RecordInstanceCreated(entity.Flow, runtimeInfoProvider.Domain);
    
    return result;
}
```

## Best Practices

### **Repository Implementation**
- Focus only on business metrics in repositories
- Let interceptor handle all database metrics
- Keep repository code clean and focused

### **Metrics Analysis**
- Use query_type + table labels for granular analysis
- Monitor error rates and patterns
- Set up alerts for high error rates or slow queries

### **Performance Optimization**
- Use duration histograms to identify slow operations
- Correlate high query volume with performance issues
- Monitor transaction rollback rates

The Database Metrics Interceptor provides comprehensive, zero-maintenance database observability across your entire workflow system! 🚀