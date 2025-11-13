# Infrastructure Layer

## Overview

The Infrastructure Layer of the BBT Workflow Engine implements the technical concerns required for data persistence, external system integration, and runtime infrastructure management. This layer follows Clean Architecture principles by providing concrete implementations of interfaces defined in the Application and Domain layers, while maintaining clear separation of concerns and testability.

## Architecture Overview

```
┌─────────────────────────────────────────────────────────┐
│                  Application Layer                      │
│  ┌─────────────────┐  ┌─────────────────┐             │
│  │   Repository    │  │   Background    │             │
│  │   Interfaces    │  │   Job Service   │             │
│  └─────────────────┘  └─────────────────┘             │
└─────────────────────────────────────────────────────────┘
                           │
┌─────────────────────────────────────────────────────────┐
│              Infrastructure Layer                       │
│  ┌─────────────────┐  ┌─────────────────┐             │
│  │  Entity Framework│  │  DAPR           │             │
│  │  Core Repositories│  │  Integration    │             │
│  │                 │  │                 │             │
│  │ • EF Repositories│  │ • Background Jobs│             │
│  │ • DbContext     │  │ • Service Invoke │             │
│  │ • Migrations    │  │ • HTTP Endpoints │             │
│  └─────────────────┘  └─────────────────┘             │
│                                                         │
│  ┌─────────────────┐  ┌─────────────────┐             │
│  │  PostgreSQL     │  │  Redis          │             │
│  │  Database       │  │  Caching        │             │
│  │                 │  │                 │             │
│  │ • Schema Mgmt   │  │ • Distributed   │             │
│  │ • Multi-Schema  │  │ • Health Checks │             │
│  │ • Query Opt.    │  │ • Configuration │             │
│  └─────────────────┘  └─────────────────┘             │
└─────────────────────────────────────────────────────────┘
                           │
┌─────────────────────────────────────────────────────────┐
│              External Systems                           │
│  ┌─────────────────┐  ┌─────────────────┐             │
│  │   PostgreSQL    │  │     Redis       │             │
│  │   Database      │  │     Cache       │             │
│  └─────────────────┘  └─────────────────┘             │
└─────────────────────────────────────────────────────────┘
```

## Core Dependencies

### Project Dependencies

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>BBT.Workflow</RootNamespace>
  </PropertyGroup>
  
  <ItemGroup>
    <!-- Aether Framework Infrastructure -->
    <PackageReference Include="BBT.Aether.Infrastructure" Version="$(AetherPackageVersion)" />
    
    <!-- Entity Framework Core with PostgreSQL -->
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="$(NpgsqlPackageVersion)" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Tools" Version="$(MicrosoftPackageVersion)" />
    
    <!-- Application Layer Reference -->
    <ProjectReference Include="..\BBT.Workflow.Application\BBT.Workflow.Application.csproj" />
  </ItemGroup>
</Project>
```

## Data Access Layer

### 1. WorkflowDbContext

The central database context with multi-schema support:

```csharp
public class WorkflowDbContext : AetherDbContext<WorkflowDbContext>, IDbContextSchema
{
    private readonly ICurrentSchema _currentSchema;

    public WorkflowDbContext(
        DbContextOptions<WorkflowDbContext> options,
        ICurrentSchema currentSchema) : base(options)
    {
        _currentSchema = currentSchema;
    }

    public string? SchemaName => _currentSchema?.Name;

    // Entity Sets
    public virtual DbSet<Instance> Instances { get; set; }
    public virtual DbSet<InstanceCorrelation> InstanceCorrelations { get; set; }
    public virtual DbSet<InstanceData> InstancesData { get; set; }
    public virtual DbSet<InstanceAction> InstanceActions { get; set; }
    public virtual DbSet<InstanceTask> InstanceTasks { get; set; }
    public virtual DbSet<InstanceTransition> InstanceTransitions { get; set; }
    public virtual DbSet<InstanceJob> InstanceJobs { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        // Apply current schema to all entities
        builder.HasDefaultSchema(SchemaName);
        
        base.OnModelCreating(builder);

        // Configure workflow-specific mappings
        builder.ConfigureWorkflow(SchemaName);
    }
}
```

### 2. Entity Configuration

Comprehensive entity mappings with PostgreSQL optimizations:

```csharp
public static class InstancesModelCreatingExtensions
{
    public static void ConfigureInstances(this ModelBuilder builder, string? schema)
    {
        // Instance Entity Configuration
        builder.Entity<Instance>(b =>
        {
            b.ToTable("Instances", schema);
            b.ConfigureByConvention();

            b.Property(p => p.Key)
                .HasMaxLength(InstanceConstants.MaxKeyLength);

            b.Property(p => p.Tags)
                .HasColumnType("text[]"); // PostgreSQL array support

            b.Property(p => p.Status)
                .IsRequired()
                .HasMaxLength(InstanceConstants.MaxStatusLength)
                .HasConversion(new InstanceStatusConverter());

            // Relationships
            b.HasMany(m => m.DataList)
                .WithOne()
                .HasForeignKey(p => p.InstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Instance Data with JSONB Support
        builder.Entity<InstanceData>(b =>
        {
            b.ToTable("InstancesData", schema);
            
            b.OwnsOne(p => p.Data, d =>
            {
                d.Property(g => g.Json)
                    .HasColumnType("jsonb") // PostgreSQL JSONB for performance
                    .HasColumnName(nameof(InstanceData.Data));
            });
        });

        // Instance Transitions with JSON Headers/Body
        builder.Entity<InstanceTransition>(b =>
        {
            b.ToTable("InstanceTransitions", schema);
            
            b.OwnsOne(p => p.Body, d =>
            {
                d.Property(g => g.Json)
                    .HasColumnType("jsonb")
                    .HasColumnName(nameof(InstanceTransition.Body));
            });

            b.OwnsOne(p => p.Header, d =>
            {
                d.Property(g => g.Json)
                    .HasColumnType("jsonb")
                    .HasColumnName(nameof(InstanceTransition.Header));
            });
        });

        // Instance Jobs for Background Processing
        builder.Entity<InstanceJob>(b =>
        {
            b.ToTable("InstanceJobs", schema);
            
            b.Property(p => p.JobName)
                .IsRequired()
                .HasMaxLength(InstanceJobConstants.MaxJobNameLength);

            b.Property(p => p.JobId)
                .IsRequired()
                .HasMaxLength(InstanceJobConstants.MaxJobIdLength);
            
            b.OwnsOne(p => p.Payload, d =>
            {
                d.Property(g => g.Json)
                    .HasColumnType("jsonb")
                    .HasColumnName(nameof(InstanceJob.Payload));
            });

            // Unique constraint for job ID
            b.HasIndex(i => i.JobId).IsUnique();
        });
    }
}
```

### 3. Repository Implementations

EF Core repository implementations with performance optimizations:

```csharp
public sealed class EfCoreInstanceRepository : EfCoreRepository<WorkflowDbContext, Instance, Guid>, IInstanceRepository
{
    public EfCoreInstanceRepository(
        WorkflowDbContext dbContext,
        IServiceProvider serviceProvider,
        ITransactionService transactionService)
        : base(dbContext, serviceProvider, transactionService)
    {
    }

    public override async Task<IQueryable<Instance>> WithDetailsAsync()
    {
        return (await base.WithDetailsAsync())
            .Include(i => i.DataList);
    }

    public async Task<Instance?> FindByKeyAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        return await (await GetDbSetAsync())
            .Include(i => i.DataList)
            .FirstOrDefaultAsync(p => p.Key == key, cancellationToken);
    }

    public async Task<List<InstanceAndDataModel>> GetActiveDataListAsync(
        CancellationToken cancellationToken = default)
    {
        var context = await GetDbContextAsync();
        
        // Optimized query with proper indexing
        return await (from instance in context.Instances
                where instance.Status == InstanceStatus.Active
                join data in context.InstancesData on instance.Id equals data.InstanceId
                select new InstanceAndDataModel
                {
                    Instance = instance,
                    InstanceData = data
                })
            .AsNoTracking() // Read-only operations
            .AsSplitQuery() // Better performance with joins
            .ToListAsync(cancellationToken);
    }
}
```

## Database Schema Management

### 1. Multi-Schema Support

Dynamic schema creation and management:

```csharp
public class PostgresSchemaManager : ISchemaManager
{
    private readonly IDbContextFactory<WorkflowDbContext> _dbContextFactory;

    public PostgresSchemaManager(IDbContextFactory<WorkflowDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task EnsureSchemaAndTablesAsync(
        string schemaName, 
        CancellationToken cancellationToken = default)
    {
        var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        
        // Check for pending migrations
        var pendingMigrations = await context.Database.GetPendingMigrationsAsync(cancellationToken);
        if (pendingMigrations.Any())
        {
            // Apply migrations automatically
            await context.Database.MigrateAsync(cancellationToken);
        }
    }
}
```

### 2. Schema-Aware Model Caching

Ensures proper EF Core model caching per schema:

```csharp
public class DynamicSchemaModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
        => new CoreModelCacheKey(context, designTime);
}

class CoreModelCacheKey : ModelCacheKey
{
    private readonly string? _schema = (context as WorkflowDbContext)?.SchemaName ?? "public";
    private readonly bool _designTime = designTime;
    
    protected override bool Equals(ModelCacheKey other)
    {
        return base.Equals(other)
               && other is CoreModelCacheKey otherKey
               && otherKey._schema == _schema
               && otherKey._designTime == _designTime;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(base.GetHashCode(), _schema);
    }
}
```

### 3. DbContext Factory

Schema-aware DbContext creation:

```csharp
public class WorkflowDbContextFactory : IDbContextFactory<WorkflowDbContext>
{
    private readonly ICurrentSchema _currentSchema;
    private readonly DbContextOptions<WorkflowDbContext> _options;

    public WorkflowDbContext CreateDbContext()
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        
        var builder = new DbContextOptionsBuilder<WorkflowDbContext>(_options);

        builder.UseNpgsql(connectionString, npgsqlOptions =>
        {
            // Schema-specific migration history
            npgsqlOptions.MigrationsHistoryTable("__Workflow_Migrations", _currentSchema.Name);
            
            // Connection resilience
            npgsqlOptions.EnableRetryOnFailure(
                maxRetryCount: 3,
                maxRetryDelay: TimeSpan.FromSeconds(30),
                errorCodesToAdd: null);
        });

        // Register custom services
        builder.ReplaceService<IModelCacheKeyFactory, DynamicSchemaModelCacheKeyFactory>();
        builder.ReplaceService<IMigrationsAssembly, DbSchemaAwareMigrationAssembly>();
        
        return new WorkflowDbContext(builder.Options, _currentSchema);
    }
}
```

## DAPR Integration

### 1. Background Job Service

DAPR-based distributed job scheduling:

```csharp
public sealed class DaprBackgroundJobService : IBackgroundJobService
{
    private readonly ILogger<DaprBackgroundJobService> _logger;
    private readonly DaprJobsClient _daprJobsClient;
    private readonly IJobStore _jobStore;

    public async Task EnqueueAsync<T>(
        string jobName,
        string jobId,
        DaprJobSchedule schedule,
        T payload,
        CancellationToken cancellationToken = default) where T : class
    {
        _logger.LogInformation("Scheduling job {jobName} - {jobId}.", jobName, jobId);

        var jobData = new BackgroundJobInfo<T>
        {
            JobName = jobName,
            JobId = jobId,
            ExpressionValue = schedule.ExpressionValue,
            Payload = payload,
            IsTriggered = false
        };

        // Persist job information
        await _jobStore.SaveAsync(jobId, jobData, cancellationToken);

        // Schedule with DAPR
        await _daprJobsClient.ScheduleJobAsync(
            jobName,
            schedule,
            JsonSerializer.SerializeToUtf8Bytes(jobData),
            cancellationToken: cancellationToken);
    }
}
```

### 2. Job Store Implementation

Entity Framework-based job persistence:

```csharp
public sealed class EfCoreJobStore : IJobStore
{
    private readonly ICurrentSchema _currentSchema;
    private readonly IInstanceJobRepository _jobRepository;
    private readonly IGuidGenerator _guidGenerator;

    public async Task SaveAsync<T>(
        string jobId, 
        BackgroundJobInfo<T> job,
        CancellationToken cancellationToken = default) where T : class
    {
        var domain = job.GetDomain();
        if (domain.IsNullOrEmpty()) return;

        // Switch to appropriate schema
        using (_currentSchema.Change(domain))
        {
            var instanceJob = await _jobRepository.FindByNameAsync(jobId, cancellationToken);
            
            if (instanceJob != null)
            {
                // Update existing job
                if (job.IsTriggered)
                {
                    instanceJob.Triggered();
                }
                instanceJob.UpdateTriggerAt(job.ExpressionValue);
                await _jobRepository.UpdateAsync(instanceJob, true, cancellationToken);
            }
            else
            {
                // Create new job
                instanceJob = InstanceJob.Create(
                    _guidGenerator.Create(),
                    job.JobName,
                    job.JobId,
                    job.GetDomain()!,
                    job.GetFlowName()!,
                    job.GetInstanceId(),
                    job.ExpressionValue,
                    JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(job.Payload)));
                    
                await _jobRepository.InsertAsync(instanceJob, true, cancellationToken);
            }
        }
    }
}
```

## External Service Integration

### 1. HTTP Client Configuration

Optimized HTTP client setup for external service calls:

```csharp
private static void ConfigureClient(IServiceCollection services)
{
    // DAPR clients
    services.AddDaprClient();
    services.AddDaprJobsClient();
    
    // HTTP client with connection pooling
    services.AddHttpClient<HttpTaskExecutor>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Add("User-Agent", "BBT-Workflow-Engine/1.0");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        MaxConnectionsPerServer = 10,
        UseCookies = false
    });
}
```

### 2. DAPR Service Task Executor

Service-to-service communication through DAPR:

```csharp
public sealed class DaprServiceTaskExecutor : TaskExecutor, ITaskExecutor
{
    private readonly IScriptEngine _scriptEngine;
    private readonly DaprClient _daprClient;

    public async Task<object?> ExecuteAsync(
        WorkflowTask task,
        string scriptCode,
        ScriptContext context,
        CancellationToken cancellationToken = default)
    {
        var daprTask = (task as DaprServiceTask)!;

        // Compile input mapping script
        var scriptRunner = await _scriptEngine.CompileToInstanceAsync<IMapping>(
            scriptCode, cancellationToken: cancellationToken);
        
        // Prepare request
        var downResponse = await scriptRunner.InputHandler(daprTask, context);

        // Create DAPR request
        var request = _daprClient.CreateInvokeMethodRequest(
            new HttpMethod(daprTask.HttpVerb),
            daprTask.AppId,
            daprTask.MethodName);

        if (request.Method != HttpMethod.Get && downResponse.Data != null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(downResponse.Data), 
                Encoding.UTF8, 
                "application/json");
        }
        
        // Invoke service
        var response = await _daprClient.InvokeMethodAsync<dynamic?>(
            request, cancellationToken: cancellationToken);

        // Process response
        context.SetBody(response);
        var upResponse = await scriptRunner.OutputHandler(context);
        return upResponse.Data;
    }
}
```

### 3. DAPR HTTP Endpoint Task Executor

HTTP endpoint invocation through DAPR:

```csharp
public sealed class DaprHttpEndpointTaskExecutor : TaskExecutor, ITaskExecutor
{
    public async Task<object?> ExecuteAsync(
        WorkflowTask task,
        string scriptCode,
        ScriptContext context,
        CancellationToken cancellationToken = default)
    {
        var daprTask = (task as DaprHttpEndpointTask)!;

        var scriptRunner = await _scriptEngine.CompileToInstanceAsync<IMapping>(
            scriptCode, cancellationToken: cancellationToken);
        
        var downResponse = await scriptRunner.InputHandler(daprTask, context);

        // Invoke HTTP endpoint through DAPR
        var response = await _daprClient.InvokeMethodAsync<object?, object>(
            new HttpMethod(daprTask.Method),
            daprTask.EndpointName,
            daprTask.Path,
            downResponse.Data,
            cancellationToken: cancellationToken);

        context.SetBody(response);
        var upResponse = await scriptRunner.OutputHandler(context);
        return upResponse.Data;
    }
}
```

## Caching Infrastructure

### 1. Redis Configuration

Flexible Redis setup supporting standalone and cluster modes:

```json
{
  "Redis": {
    "Mode": "Standalone",
    "InstanceName": "workflow-api",
    "ConnectionTimeout": 5000,
    "DefaultDatabase": 0,
    "Password": "",
    "Ssl": false,
    "Standalone": {
      "EndPoints": ["localhost:6379"]
    },
    "Cluster": {
      "EndPoints": [
        "redis-cluster-1:6379",
        "redis-cluster-2:6379",
        "redis-cluster-3:6379"
      ]
    },
    "RetryPolicy": {
      "MaxRetries": 3,
      "RetryTimeout": 1000
    }
  }
}
```

### 2. Distributed Cache Configuration

Multi-option caching support:

```csharp
private static void ConfigureDistributedCache(IServiceCollection services)
{
    // Option 1: Use .NET Core Distributed Cache
    services.AddNetCoreDistributedCache(sc =>
    {
        sc.AddDistributedMemoryCache(); // Development
        // sc.AddRedis(); // Production
    });
}

private static void ConfigureRedis(IServiceCollection services)
{
    // Option 2: Use DAPR State Store Cache
    // services.AddDaprStateStoreCache();

    // Redis configuration
    services.AddRedis();
}
```

## Health Checks

Comprehensive health monitoring for infrastructure components:

```csharp
public static IServiceCollection AddAppHealthChecks(this IServiceCollection services)
{
    var configuration = services.GetConfiguration();
    var healthChecksBuilder = services.AddHealthChecks();

    var endpoint = configuration["Redis:Standalone:EndPoints:0"];
    var password = configuration["Redis:Password"];
    var redisConnectionString = string.IsNullOrWhiteSpace(password)
        ? endpoint
        : $"{endpoint},password={password}";

    healthChecksBuilder
        .AddDapr(name: "dapr", tags: ["ready"])
        .AddRedis(redisConnectionString!, name: "redis", tags: ["ready"])
        .AddNpgSql(
            configuration.GetConnectionString("Default")!, 
            name: "database", 
            tags: ["ready"])
        .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);
    
    return services;
}
```

## Configuration Management

### 1. Database Configuration

PostgreSQL setup with connection resilience:

```csharp
private static void ConfigureDbContext(IServiceCollection services, IConfiguration configuration)
{
    services.AddAetherDbContext<WorkflowDbContext>(options =>
    {
        options.UseNpgsql(configuration.GetConnectionString("Default"), npgsqlOptions =>
        {
            npgsqlOptions.MigrationsHistoryTable("__Workflow_Migrations");
            
            // Enable connection resilience
            npgsqlOptions.EnableRetryOnFailure(
                maxRetryCount: 3,
                maxRetryDelay: TimeSpan.FromSeconds(30),
                errorCodesToAdd: null);
        })
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));

        // Multi-schema support
        options.ReplaceService<IModelCacheKeyFactory, DynamicSchemaModelCacheKeyFactory>();
        options.ReplaceService<IMigrationsAssembly, DbSchemaAwareMigrationAssembly>();
    });
    
    services.AddSingleton<IDataSeedService, WorkflowDataSeedService>();
}
```

### 2. Environment-Specific Configuration

Configuration for different environments:

```json
// Development
{
  "ConnectionStrings": {
    "Default": "Host=localhost;Port=5432;Database=Aether_WorkflowDb;Username=postgres;Password=postgres;"
  },
  "Redis": {
    "Mode": "Standalone",
    "Standalone": {
      "EndPoints": ["localhost:6379"]
    }
  }
}
```

## Service Registration

Complete infrastructure service registration:

```csharp
public static IServiceCollection AddInfrastructureModule(this IServiceCollection services)
{
    // Add application dependencies
    services.AddApplicationModule();
    services.AddAetherInfrastructure();

    // Schema management
    services.AddScoped<ISchemaManager, PostgresSchemaManager>();
    
    // Background jobs
    services.AddScoped<IBackgroundJobService, DaprBackgroundJobService>();
    services.AddScoped<IJobStore, EfCoreJobStore>();
    
    // Database context
    services.AddScoped<IDbContextFactory<WorkflowDbContext>, WorkflowDbContextFactory>();

    // Repositories
    services.AddScoped<IInstanceRepository, EfCoreInstanceRepository>();
    services.AddScoped<IInstanceTransitionRepository, EfCoreInstanceTransitionRepository>();
    services.AddScoped<IInstanceTaskRepository, EfCoreInstanceTaskRepository>();
    services.AddScoped<IInstanceJobRepository, EfCoreInstanceJobRepository>();
    
    return services;
}
```

## Performance Optimizations

### 1. Database Query Optimization

```csharp
// Use AsNoTracking for read-only operations
.AsNoTracking()

// Use AsSplitQuery for complex joins
.AsSplitQuery()

// Proper include patterns
.Include(i => i.DataList)

// Projection for large datasets
.Select(x => new { x.Id, x.Key, x.Status })
```

### 2. Connection Pooling

```csharp
// HTTP client connection pooling
services.AddHttpClient<HttpTaskExecutor>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        MaxConnectionsPerServer = 10,
        UseCookies = false
    });

// Database connection pooling (built into Npgsql)
npgsqlOptions.EnableRetryOnFailure(
    maxRetryCount: 3,
    maxRetryDelay: TimeSpan.FromSeconds(30));
```

### 3. Caching Strategies

```csharp
// Model caching per schema
builder.ReplaceService<IModelCacheKeyFactory, DynamicSchemaModelCacheKeyFactory>();

// Distributed caching for workflow definitions
services.AddRedis();

// In-memory caching for frequently accessed data
services.AddMemoryCache();
```

## Error Handling and Resilience

### 1. Database Resilience

```csharp
// Automatic retries for transient failures
npgsqlOptions.EnableRetryOnFailure(
    maxRetryCount: 3,
    maxRetryDelay: TimeSpan.FromSeconds(30),
    errorCodesToAdd: null);

// Connection timeout configuration
services.Configure<DbContextOptions>(options =>
{
    options.CommandTimeout = TimeSpan.FromSeconds(30);
});
```

### 2. External Service Resilience

```csharp
// HTTP client timeout and retries
services.AddHttpClient<HttpTaskExecutor>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
})
.AddPolicyHandler(GetRetryPolicy());

private static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
{
    return HttpPolicyExtensions
        .HandleTransientHttpError()
        .WaitAndRetryAsync(
            retryCount: 3,
            sleepDurationProvider: retryAttempt => 
                TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));
}
```

## Monitoring and Observability

### 1. Telemetry Configuration

```json
{
  "Telemetry": {
    "ServiceName": "Workflow",
    "ServiceVersion": "1.0.0",
    "Environment": "Production",
    "TraceProvider": "otlp",
    "LogProvider": "file",
    "Logging": {
      "Enabled": true,
      "FilePath": "Logs/workflow-api.log",
      "MinimumLevel": "Information",
      "Enrichers": {
        "Headers": [
          "x-correlation-id",
          "x-request-id"
        ]
      }
    },
    "Otlp": {
      "Endpoint": "http://opentelemetry-collector:4317"
    }
  }
}
```

### 2. Database Performance Monitoring

```csharp
// Enable sensitive data logging in development
#if DEBUG
options.EnableSensitiveDataLogging();
options.EnableDetailedErrors();
#endif

// Log slow queries
options.LogTo(Console.WriteLine, new[] {
    DbLoggerCategory.Database.Command.Name
}, LogLevel.Information);
```

## Security Considerations

### 1. Connection Security

```csharp
// Encrypted connections
services.AddDbContext<WorkflowDbContext>(options =>
{
    options.UseNpgsql(connectionString, npgsqlOptions =>
    {
        npgsqlOptions.EnableRetryOnFailure();
        // SSL configuration in production
        if (environment.IsProduction())
        {
            npgsqlOptions.RemoteCertificateValidationCallback = ValidateCertificate;
        }
    });
});
```

### 2. Credential Management

```csharp
// Environment-based configuration
"ConnectionStrings": {
  "Default": "ENV_CONNECTION_STRING_DEFAULT"
},
"Redis": {
  "Password": "ENV_REDIS_PASSWORD"
}

// DAPR secret management
services.AddDaprSecretStore();
```

## Best Practices

### 1. Repository Pattern

- Use dependency injection for repository interfaces
- Implement proper async/await patterns
- Include necessary related data efficiently
- Use proper exception handling

### 2. Database Design

- Leverage PostgreSQL JSONB for flexible data storage
- Use appropriate indexes for query performance
- Implement proper foreign key relationships
- Design for multi-schema isolation

### 3. External Integration

- Implement circuit breaker patterns
- Use connection pooling appropriately
- Handle timeouts and retries gracefully
- Monitor external service health

### 4. Caching Strategy

- Cache frequently accessed data
- Implement proper cache invalidation
- Use distributed caching for scalability
- Monitor cache hit rates

The Infrastructure Layer provides a robust foundation for the BBT Workflow Engine, ensuring reliable data persistence, efficient external system integration, and scalable runtime infrastructure management while maintaining clean separation of concerns and testability. 