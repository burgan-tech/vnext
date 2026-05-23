# Infrastructure Layer

## Overview

The Infrastructure layer (`BBT.Workflow.Infrastructure`) provides persistence, schema management, remote routing, discovery, data sinks, and system integrations. It supplies concrete implementations for interfaces from the Domain and Application layers and wires these concerns through `AddInfrastructureModule()`.

All services adhere to the Result pattern and are designed for multi-schema (multi-tenant) execution.

## Responsibilities

- EF Core persistence and model configuration
- Multi-schema migrations and schema validation
- Repository implementations
- Domain discovery and remote routing
- Embedded script providers and Dapr notification metadata
- Data sink integration (e.g., ClickHouse)
- Monitoring interceptors and hosted services
- Event hooks for instance lifecycle events

## Service Registration

`AddInfrastructureModule()` is the entry point for wiring infrastructure services:

```csharp
public static IServiceCollection AddInfrastructureModule(this IServiceCollection services)
{
    services.AddAetherInfrastructure();

    if (!services.Any(sd => sd.ServiceType == typeof(IDistributedCache)))
    {
        services.AddDistributedMemoryCache();
    }

    services.AddSingleton<NpgsqlSchemaConnectionInterceptor>();
    services.AddScoped<IMultiSchemaMigrator<WorkflowDbContext>, MultiSchemaMigrator<WorkflowDbContext>>();
    services.AddScoped<ISchemaValidator, SchemaValidator>();

    services.AddScoped<IInstanceRepository, EfCoreInstanceRepository>();
    services.AddScoped<IInstanceCorrelationRepository, EfCoreInstanceCorrelationRepository>();
    services.AddScoped<IInstanceTransitionRepository, EfCoreInstanceTransitionRepository>();
    services.AddScoped<IInstanceTaskRepository, EfCoreInstanceTaskRepository>();
    services.AddScoped<IInstanceJobRepository, EfCoreInstanceJobRepository>();

    services.AddDomainDiscovery();
    services.AddVNextApiServices();
    services.AddInstanceGatewayServices();

    services.AddSingleton<IWorkflowMetrics, PrometheusWorkflowMetrics>();
    services.AddSingleton<WorkflowDatabaseInterceptor>();
    services.AddSingleton<WorkflowTransactionInterceptor>();
    services.AddHostedService<SystemHealthMonitoringHostedService>();

    services.AddDataSinkServices();
    services.AddClickHouseDataSinks();
    services.RegisterDataSinks();

    services.AddScoped<ISchemaMigrationOrchestrator, SchemaMigrationOrchestrator>();
    services.AddSingleton<IPostCommitIdempotencyStore, DistributedCacheIdempotencyStore>();

    services.AddEventHook<InstanceSubCompletedEvent, InstanceSubCompletedEventHook>();
    services.AddEventHook<InstanceSubStateChangedEvent, InstanceSubStateChangedEventHook>();
    services.AddEventHook<InstanceCanceledEvent, InstanceCanceledEventHook>();

    services.AddEmbeddedScriptServices();
    services.ConfigureEmbeddedScripts(opt =>
    {
        opt.Add(
            NotificationScriptProvider.DefaultKey,
            "BBT.Workflow.Tasks.Scripting.NotificationMapping.csx",
            typeof(EmbeddedScriptEntry).Assembly);
    });

    return services;
}
```

## Data Access

### WorkflowDbContext

`WorkflowDbContext` extends Aether’s `AetherDbContext` and implements background job storage:

```csharp
public class WorkflowDbContext : AetherDbContext<WorkflowDbContext>, IHasEfCoreBackgroundJobs
{
    public virtual DbSet<Instance> Instances { get; set; }
    public virtual DbSet<InstanceCorrelation> InstanceCorrelations { get; set; }
    public virtual DbSet<InstanceData> InstancesData { get; set; }
    public virtual DbSet<InstanceAction> InstanceActions { get; set; }
    public virtual DbSet<InstanceTask> InstanceTasks { get; set; }
    public virtual DbSet<InstanceTransition> InstanceTransitions { get; set; }
    public virtual DbSet<InstanceJob> InstanceJobs { get; set; }
    public virtual DbSet<BackgroundJobInfo> BackgroundJobs { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema(null);
        base.OnModelCreating(builder);
        builder.ConfigureWorkflow();
        builder.ConfigureBackgroundJob();
    }
}
```

### Model Configuration

`InstancesModelCreatingExtensions` configures:

- JSONB columns for `InstanceData`, `InstanceTransition`, `InstanceTask`, `InstanceAction`.
- Indexes for effective state and versioning.
- Subflow correlations with composite indexes.
- `IsLatest` partial unique index for `InstanceData`.

### Repositories

EF Core repositories live in `Infrastructure/Instances` and add:

- eager loading (`DataList`, `ChildCorrelations`)
- optimized JSON filtering with schema validation
- metrics and data sink integration

Example: `EfCoreInstanceRepository` uses `WorkflowDatabaseInterceptor` for automatic DB metrics and transfers data to sinks when enabled.

## Multi-Schema and Migrations

Infrastructure provides schema-aware migration and SQL generation:

- `MultiSchemaMigrator<TContext>`: creates schemas and applies pending migrations.
- `SchemaMigrationOrchestrator`: wraps migration with distributed locking.
- `MultiSchemaNpgsqlMigrationsSqlGenerator`: strips `"public"` schema defaults to keep multi-schema output clean.
- `NpgsqlSchemaConnectionInterceptor`: resolves schema per request.

## Domain Discovery and Remote Routing

### Discovery

`DomainDiscoveryResolver` resolves remote endpoints using:

- `IDistributedCacheService` with ETag-based validation
- `ServiceDiscoveryOptions` configuration
- fallback to `RemoteOptions.BaseUrl` when discovery is disabled

### Gateways

Instance gateways route between local and remote execution:

- `LocalInstanceCommandGateway` / `LocalInstanceQueryGateway`
- `RemoteInstanceCommandGateway` / `RemoteInstanceQueryGateway`
- `RoutedInstanceCommandGateway` / `RoutedInstanceQueryGateway` (uses `IRuntimeInfoProvider.IsDomainMatch`)

### Remote App Services

Remote instance clients live in `Infrastructure/Instances/Remote` and implement:

- `IRemoteInstanceCommandAppService`
- `IRemoteInstanceQueryAppService`

They use `IDomainDiscoveryResolver` for endpoint discovery and return `Result` / `ConditionalResult`.

See [Remote Routing and Discovery](./remote-routing-and-discovery.md) for the detailed flow and configuration.

## Embedded Scripts and Notifications

Infrastructure provides embedded script providers for runtime scripts:

- `IEmbeddedScriptProvider`
- `INotificationScriptProvider`
## Data Sink Integration

Infrastructure includes a pluggable data sink pipeline:

- `DataSinkManager` and `DataSinkRegistry`
- `DataSinkRegistrationHostedService`
- ClickHouse sinks (optional) for tasks and transitions

Sinks are invoked by repositories (e.g., `EfCoreInstanceRepository`) and are intentionally best-effort to avoid breaking core operations.

## Monitoring and Metrics

Metrics are recorded via interceptors and a Prometheus-based implementation:

- `WorkflowDatabaseInterceptor`: records SQL command metrics.
- `WorkflowTransactionInterceptor`: records transaction metrics.
- `PrometheusWorkflowMetrics`: application metrics sink.
- `SystemHealthMonitoringHostedService`: health checks and runtime signals.

## Security and Schema Validation

Infrastructure provides:

- `SchemaValidator` for validation and security checks.
- `IDistributedCache` fallback (in-memory) if no distributed cache is registered.

## Event Hooks

Event hooks are registered for lifecycle events and use the Event Hook infrastructure:

- `InstanceSubCompletedEventHook`
- `InstanceSubStateChangedEventHook`
- `InstanceCanceledEventHook`

## Dapr Metadata and Notifications

Dapr metadata and notification bindings are managed in:

- `DaprMetadataProvider` and `DaprMetadataWarmupHostedService`
- `NotificationBindingResolver`
- `DaprNotificationServiceCollectionExtensions`

These services support runtime resolution of Dapr components for notification bindings.

See [Embedded Scripts and Dapr](../infrastructure/embedded-scripts-and-dapr.md) for the full flow and configuration.

## Implementation References

- `src/BBT.Workflow.Infrastructure/Data/WorkflowDbContext.cs`
- `src/BBT.Workflow.Infrastructure/Data/InstancesModelCreatingExtensions.cs`
- `src/BBT.Workflow.Infrastructure/Instances/EfCoreInstanceRepository.cs`
- `src/BBT.Workflow.Infrastructure/Schemas/SchemaMigrationOrchestrator.cs`
- `src/BBT.Workflow.Infrastructure/Schemas/MultiSchemaMigrator.cs`
- `src/BBT.Workflow.Infrastructure/Schemas/MultiSchemaNpgsqlMigrationsSqlGenerator.cs`
- `src/BBT.Workflow.Infrastructure/Discovery/DomainDiscoveryResolver.cs`
- `src/BBT.Workflow.Infrastructure/Gateway/RoutedInstanceCommandGateway.cs`
- `src/BBT.Workflow.Infrastructure/Scripting/EmbeddedScriptProvider.cs`
- `src/BBT.Workflow.Infrastructure/DataSink/DataSinkManager.cs`
- `src/BBT.Workflow.Infrastructure/Monitoring/WorkflowDatabaseInterceptor.cs`