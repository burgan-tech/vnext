# Multi-Schema Support

## Overview

The BBT Workflow Engine provides multi-schema support for isolating workflow data per schema while sharing the same application infrastructure. Schema resolution and switching are provided by the Aether SDK, while schema creation/migration and validation are orchestrated by workflow-specific services.

> **Note**: Multi-schema functionality is now provided by the **Aether SDK** (`BBT.Aether.MultiSchema`). This document describes how the workflow engine utilizes Aether's multi-schema capabilities.

## Architecture Overview

```
┌─────────────────────────────────────────────────────────┐
│                Application Layer                        │
│  ┌─────────────────┐  ┌─────────────────┐             │
│  │  ICurrentSchema │  │ Schema Manager  │             │
│  │  (Aether SDK)   │  │                 │             │
│  │                 │  │ • Create Schema │             │
│  │ • Current Schema│  │ • Migrate Tables│             │
│  │ • Context Switch│  │ • Ensure Tables │             │
│  └─────────────────┘  └─────────────────┘             │
└─────────────────────────────────────────────────────────┘
                           │
┌─────────────────────────────────────────────────────────┐
│             Entity Framework Core                       │
│  ┌─────────────────┐  ┌─────────────────┐             │
│  │ Schema-Aware    │  │ Dynamic Model   │             │
│  │  DbContext      │  │   Caching       │             │
│  │                 │  │                 │             │
│  │ • Schema Name   │  │ • Per-Schema    │             │
│  │ • Model Config  │  │   Cache Keys    │             │
│  │ • Migration     │  │ • Cache Factory │             │
│  └─────────────────┘  └─────────────────┘             │
└─────────────────────────────────────────────────────────┘
                           │
┌─────────────────────────────────────────────────────────┐
│               Database Layer                            │
│  ┌─────────────────┐  ┌─────────────────┐             │
│  │ Schema: public  │  │ Schema: flow-a  │             │
│  │                 │  │                 │             │
│  │ • Default Schema│  │ • Flow A Tables │             │
│  │ • System Tables │  │ • Isolated Data │             │
│  │ • Migration Log │  │ • Flow A Indexes│             │
│  └─────────────────┘  └─────────────────┘             │
│                                                         │
│  ┌─────────────────┐  ┌─────────────────┐             │
│  │ Schema: flow-b  │  │ Schema: flow-c  │             │
│  │                 │  │                 │             │
│  │ • Flow B Tables │  │ • Flow C Tables │             │
│  │ • Isolated Data │  │ • Isolated Data │             │
│  │ • Flow B Indexes│  │ • Flow C Indexes│             │
│  └─────────────────┘  └─────────────────┘             │
└─────────────────────────────────────────────────────────┘
```

## Core Components (Aether SDK)

Multi-schema support is provided by **Aether SDK** (`BBT.Aether.MultiSchema` and `BBT.Aether.AspNetCore.MultiSchema`). The workflow engine uses the following Aether-provided components:

### 1. ICurrentSchema Interface (Aether SDK)

The `ICurrentSchema` interface from Aether SDK manages the current database schema context:

```csharp
using BBT.Aether.MultiSchema;

// Aether SDK provides ICurrentSchema with Use() method
public interface ICurrentSchema
{
    string Name { get; }
    IDisposable Use(string schemaName);
}
```

### 2. Schema Resolution Configuration

Schema resolution is configured during application startup using Aether's `AddSchemaResolution`:

```csharp
services.AddSchemaResolution(options =>
{
    options.HeaderKey = "X-Workflow";
    options.QueryStringKey = "workflow";
    options.RouteValueKey = "workflow";
    options.ThrowIfNotFound = false;
});
```

### 3. Usage Pattern

```csharp
using BBT.Aether.MultiSchema;

public class RuntimeService(
    IInstanceRepository instanceRepository,
    ICurrentSchema currentSchema,
    IOptions<RuntimeOptions> runtimeOptions)
{
    public async Task<IEnumerable<T?>> GetAsync<T>(CancellationToken cancellationToken = default)
        where T : class, IDomainEntity, IReferenceSetter
    {
        var schemaInfo = runtimeOptions.Value.GetSchemaNameByType(typeof(T));

        // Use Aether's ICurrentSchema.Use() for scoped schema switching
        using (currentSchema.Use(schemaInfo.Schema))
        {
            var results = await instanceRepository.GetActiveDataListAsync(cancellationToken);
            // All database operations use the specified schema
            return results;
        }
    }
}
```

## Schema-Aware DbContext

The workflow engine uses Aether's `AetherDbContext` with schema support provided by `NpgsqlSchemaConnectionInterceptor`:

```csharp
services.AddAetherDbContext<WorkflowDbContext>((sp, options) =>
{
    options.UseNpgsql(configuration.GetConnectionString("Default"),
            npgsqlOptions => { npgsqlOptions.MigrationsHistoryTable("__Workflow_Migrations"); })
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));

    options.ReplaceService<IMigrationsSqlGenerator, MultiSchemaNpgsqlMigrationsSqlGenerator>();
    options.AddInterceptors(
        sp.GetRequiredService<NpgsqlSchemaConnectionInterceptor>(),
        sp.GetRequiredService<WorkflowDatabaseInterceptor>(),
        sp.GetRequiredService<WorkflowTransactionInterceptor>()
    );
});
```

### Key Components

- **`NpgsqlSchemaConnectionInterceptor`**: Aether SDK interceptor that automatically sets the PostgreSQL `search_path` based on `ICurrentSchema.Name`.
- **`MultiSchemaNpgsqlMigrationsSqlGenerator`**: Custom migration SQL generator for multi-schema support.

### Schema Migration Orchestration

The workflow runtime wraps schema creation and migration with distributed locking to prevent concurrent migrations:

- **`IMultiSchemaMigrator<TContext>`**: Creates schema and applies pending migrations.
- **`ISchemaMigrationOrchestrator`**: Adds distributed lock protection during migrations.

### Schema Validation

Schema names are validated via `ISchemaValidator` implementations (e.g., `SyncSchemaValidator`) to ensure PostgreSQL-compatible, safe identifiers.

## Schema Context Usage Patterns

### 1. Basic Schema Switching

Use `ICurrentSchema.Use()` from Aether SDK for scoped schema switching:

```csharp
using BBT.Aether.MultiSchema;

public class SomeService(
    ICurrentSchema currentSchema,
    IInstanceRepository instanceRepository)
{
    public async Task ProcessWorkflowAsync(string flowName, string instanceKey)
    {
        // Switch to the workflow's schema using Aether's Use() method
        using (currentSchema.Use(flowName))
        {
            // All database operations now use the specified schema
            var instance = await instanceRepository.FindByKeyAsync(instanceKey);
            
            if (instance != null)
            {
                await ProcessInstanceAsync(instance);
            }
        }
        // Schema context is automatically restored when disposed
    }
}
```

### 2. Runtime Service with Multi-Schema Loading

```csharp
using BBT.Aether.MultiSchema;

public sealed class RuntimeService(
    IInstanceRepository instanceRepository,
    ICurrentSchema currentSchema,
    IOptions<RuntimeOptions> runtimeOptions) : IRuntimeService
{
    public async Task<IEnumerable<T?>> GetAsync<T>(CancellationToken cancellationToken = default)
        where T : class, IDomainEntity, IReferenceSetter
    {
        var schemaName = runtimeOptions.Value.GetSchemaNameByType(typeof(T));
        var schemaInfo = runtimeOptions.Value.Schemas[schemaName];

        using (currentSchema.Use(schemaInfo.Schema))
        {
            var results = await instanceRepository.GetActiveDataListAsync(cancellationToken);
            // Process and return results
            return results;
        }
    }
}
```

## Configuration and Setup

### 1. Schema Resolution Registration

Schema resolution is configured using Aether SDK's `AddSchemaResolution`:

```csharp
services.AddSchemaResolution(options =>
{
    options.HeaderKey = "X-Workflow";       // HTTP header for schema
    options.QueryStringKey = "workflow";     // Query string parameter
    options.RouteValueKey = "workflow";      // Route value key
    options.ThrowIfNotFound = false;         // Graceful fallback
});
```

### 2. Runtime Schema Configuration

```csharp
services.Configure<RuntimeOptions>(opt =>
{
    opt.Schemas.Add(RuntimeSysSchemaInfo.Flows, "sys_flows", typeof(Workflow));
    opt.Schemas.Add(RuntimeSysSchemaInfo.Functions, "sys_functions", typeof(Function));
    opt.Schemas.Add(RuntimeSysSchemaInfo.Schemas, "sys_schemas", typeof(SchemaDefinition));
    opt.Schemas.Add(RuntimeSysSchemaInfo.Tasks, "sys_tasks", typeof(WorkflowTask));
    opt.Schemas.Add(RuntimeSysSchemaInfo.Views, "sys_views", typeof(View));
    opt.Schemas.Add(RuntimeSysSchemaInfo.Extensions, "sys_extensions", typeof(Extension));
});
```

## Best Practices

### 1. Always Use Scoped Schema Switching

Always use Aether's `ICurrentSchema.Use()` within a `using` block to ensure proper cleanup:

```csharp
using (currentSchema.Use(flowName))
{
    // All operations use the specified schema
    await repository.GetAsync(id);
}
// Schema automatically restored
```

### 2. Schema Naming Conventions

Schema names should follow PostgreSQL identifier rules:
- Lowercase letters, digits, and underscores only
- Must start with a letter or underscore
- Maximum 63 characters (PostgreSQL limit)

Schema names are formatted using `ISchemaNameFormatter` before creation and migration to enforce consistent naming.

### 3. Error Handling

Use the Result pattern for schema-related operations:

```csharp
public async Task<Result<T>> ExecuteInSchemaAsync<T>(
    string schemaName, 
    Func<Task<Result<T>>> operation)
{
    using (currentSchema.Use(schemaName))
    {
        return await operation();
    }
}
```

## Usage Examples

### 1. Multi-Flow Workflow Processing

```csharp
using BBT.Aether.MultiSchema;

public class WorkflowProcessor(
    ICurrentSchema currentSchema,
    IInstanceRepository instanceRepository,
    ILogger<WorkflowProcessor> logger)
{
    public async Task ProcessMultipleFlowsAsync()
    {
        var flows = new[] { "loan-approval", "customer-onboarding", "document-processing" };
        
        foreach (var flow in flows)
        {
            using (currentSchema.Use(flow))
            {
                logger.LogInformation("Processing workflow in schema: {Schema}", currentSchema.Name);
                
                // All operations in this block use the current flow's schema
                var activeInstances = await instanceRepository.GetActiveInstancesAsync();
                
                foreach (var instance in activeInstances)
                {
                    await ProcessInstanceAsync(instance);
                }
            }
        }
    }
}
```

### 2. Cross-Schema Data Analysis

```csharp
using BBT.Aether.MultiSchema;

public class CrossSchemaAnalytics(
    ICurrentSchema currentSchema,
    IAnalyticsRepository analyticsRepository)
{
    public async Task<AnalyticsReport> GenerateReportAsync()
    {
        var report = new AnalyticsReport();
        var schemas = await GetWorkflowSchemasAsync();
        
        foreach (var schema in schemas)
        {
            using (currentSchema.Use(schema))
            {
                var stats = await analyticsRepository.GetSchemaStatsAsync();
                report.SchemaStats[schema] = stats;
            }
        }
        
        return report;
    }
}
```

## Aether SDK Reference

For detailed documentation on the multi-schema implementation, refer to the Aether SDK documentation:

- **`BBT.Aether.MultiSchema.ICurrentSchema`**: Interface for managing current schema context
- **`BBT.Aether.MultiSchema.NpgsqlSchemaConnectionInterceptor`**: EF Core interceptor for automatic schema switching
- **`AddSchemaResolution()`**: Extension method for configuring schema resolution from HTTP context

The multi-schema support provides excellent isolation and scalability for workflow processing, enabling the same application to handle multiple distinct workflow types while maintaining data separation and performance optimization. 