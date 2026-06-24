# Inbox & Outbox SDK Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the custom `OutboxProcessorHostedService` and `InboxProcessorHostedService` with the Aether SDK's built-in `OutboxBackgroundService` / `InboxBackgroundService` (adaptive polling), remove orphaned registrations, and align appsettings keys to the new SDK option classes.

**Architecture:** The Aether SDK now ships `OutboxBackgroundService` and `InboxBackgroundService` with adaptive polling (busy→idle→exponential backoff). Our custom hosted services reimplemented the same loop with a fixed `ProcessingInterval` field that no longer exists in `AetherOutboxOptions`. Switching to `withHostedService: true` in `AddAetherOutbox` / `AddAetherInbox` deletes ~100 lines of hand-rolled polling code and uses a maintained SDK implementation instead. The Inbox worker also carried a redundant `AddAetherOutbox` call (needed only because `InboxProcessorHostedService` injected `AetherOutboxOptions` for the poll interval — that dependency disappears when we use `AddAetherInbox` directly). `AddDaprDistributedLock` is also removed: the new `InboxProcessor` uses PostgreSQL `FOR UPDATE SKIP LOCKED` instead of a distributed lock.

**Tech Stack:** .NET 10, Aether SDK (`BBT.Aether.Infrastructure.OutboxBackgroundService`, `InboxBackgroundService`), EF Core, Dapr, xUnit.

---

## Context for implementer

### Aether SDK new behaviour

`AddAetherOutbox<TDbContext>(configure, withHostedService: true)` registers:
- `AetherOutboxOptions` singleton (bound from the configure callback)
- `IOutboxStore` → `EfCoreOutboxStore<TDbContext>`
- `IOutboxLeaseStore` → null fallback (overridden by `AddAetherNpgsql` to `NpgsqlOutboxLeaseStore`)
- `WorkerIdentity` singleton
- `IOutboxProcessor` → `OutboxProcessor<TDbContext>` singleton
- **`OutboxBackgroundService`** hosted service (adaptive polling: busy=`BusyPollingInterval`, idle doubles up to `MaxPollingInterval`)

`AddAetherInbox<TDbContext>(configure, withHostedService: true)` registers:
- `AetherInboxOptions` singleton (bound from the configure callback)
- `IInboxStore` → `EfCoreInboxStore<TDbContext>`
- `IInboxLeaseStore` → null fallback (overridden by `AddAetherNpgsql`)
- `WorkerIdentity` singleton
- `IInboxProcessor` → `InboxProcessor<TDbContext>` singleton
- **`InboxBackgroundService`** hosted service (adaptive polling)

### Removed SDK fields

`AetherOutboxOptions.ProcessingInterval` **no longer exists**. The SDK replaced it with:
- `BusyPollingInterval` (default 100 ms) — delay when batch was non-empty
- `IdlePollingInterval` (default 5 s) — starting delay when batch is empty
- `MaxPollingInterval` (default 60 s) — backoff ceiling

`AetherInboxOptions` is a **separate class** (previously the Inbox worker incorrectly reused `AetherOutboxOptions`). It has:
- `ProcessingBatchSize` (≠ `BatchSize`) — default 100
- `BusyPollingInterval`, `IdlePollingInterval`, `MaxPollingInterval` (same semantics)
- `CleanupInterval` (default 1 hour), `CleanupBatchSize` (default 1000)
- `Schema` (default **null** — **must be set** or processor skips all runs with a warning)
- `MaxRetryCount`, `RetentionPeriod`, `RetryBaseDelay`, `LeaseDuration`

### `InboxProcessor` no longer uses distributed lock

The new `InboxProcessor` uses the PostgreSQL lease store (`FOR UPDATE SKIP LOCKED`) for concurrency. `AddDaprDistributedLock` / `IDistributedLockService` can be removed from the Inbox worker.

### `Redis` config in Outbox appsettings is orphaned

The Outbox worker has never used Redis. The `Redis` section was copied from an older template. Remove it.

### `PollingJitter` remains

`ChainReaperHostedService` (in the Orchestration host) still uses `PollingJitter.Startup` and `PollingJitter.Apply`. Do not remove `src/BBT.Workflow.HttpApi.Shared/Hosting/PollingJitter.cs`.

---

## File Map

### Task 1 — Outbox Worker

| Action | File |
|--------|------|
| **Delete** | `workers/BBT.Workflow.Workers.Outbox/HostedServices/OutboxProcessorHostedService.cs` |
| **Modify** | `workers/BBT.Workflow.Workers.Outbox/Microsoft/Extensions/DependencyInjection/OutboxWorkerServiceCollectionExtensions.cs` |
| **Modify** | `workers/BBT.Workflow.Workers.Outbox/appsettings.json` |

### Task 2 — Inbox Worker

| Action | File |
|--------|------|
| **Delete** | `workers/BBT.Workflow.Workers.Inbox/HostedServices/InboxProcessorHostedService.cs` |
| **Modify** | `workers/BBT.Workflow.Workers.Inbox/Microsoft/Extensions/DependencyInjection/InboxWorkerServiceCollectionExtensions.cs` |
| **Modify** | `workers/BBT.Workflow.Workers.Inbox/appsettings.json` |

---

## Task 1: Outbox Worker — Switch to SDK Background Service

**Files:**
- Delete: `workers/BBT.Workflow.Workers.Outbox/HostedServices/OutboxProcessorHostedService.cs`
- Modify: `workers/BBT.Workflow.Workers.Outbox/Microsoft/Extensions/DependencyInjection/OutboxWorkerServiceCollectionExtensions.cs`
- Modify: `workers/BBT.Workflow.Workers.Outbox/appsettings.json`

- [ ] **Step 1: Delete the custom OutboxProcessorHostedService**

```bash
rm workers/BBT.Workflow.Workers.Outbox/HostedServices/OutboxProcessorHostedService.cs
```

The `OutboxBackgroundService` from the SDK replaces this class entirely. It implements adaptive polling (busy/idle/max) and proper exception handling — functionally equivalent but maintained by the SDK.

- [ ] **Step 2: Rewrite `OutboxWorkerServiceCollectionExtensions.cs`**

Open `workers/BBT.Workflow.Workers.Outbox/Microsoft/Extensions/DependencyInjection/OutboxWorkerServiceCollectionExtensions.cs` and replace its content with:

```csharp
using BBT.Aether.AspNetCore.MultiSchema;
using BBT.Aether.Uow.EntityFrameworkCore;
using BBT.Workflow.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service collection extensions specific to Worker Outbox
/// </summary>
public static class OutboxWorkerServiceCollectionExtensions
{
    /// <summary>
    /// Adds Worker Outbox specific services
    /// </summary>
    public static IServiceCollection AddWorkerOutboxModule(this IServiceCollection services)
    {
        var configuration = services.GetConfiguration();
        services
            .AddDomainModule()
            .AddAspNetCoreModules(configuration)
            .AddDaprClients()
            .AddAetherEventBus(options =>
            {
                options.DefaultSource =
                    $"urn:vnext:{configuration.GetValue<string?>("ApplicationName")?.ToLowerInvariant()}";
                options.PrefixEnvironmentToTopic = true;
                options.PubSubName = configuration["DAPR_PUBSUB_STORE_NAME"]!;
            })
            .AddOutboxMessagingContext(configuration)
            .AddTelemetry(configuration)
            .AddExceptionHandling()
            .AddRuntimeMiddleware()
            .AddHeaderService()
            .AddAppHealthChecks();
        return services;
    }

    /// <summary>
    /// Registers only the messaging DbContext (sys_queues outbox tables) and the outbox
    /// processor. The Outbox worker reads OutboxMessages and publishes via the event bus — it does
    /// not need WorkflowDbContext, instance repositories, or the application/infrastructure modules.
    /// </summary>
    private static IServiceCollection AddOutboxMessagingContext(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var schemaSwitchingMode = configuration.GetValue("Aether:SchemaSwitchingMode",
            SchemaSwitchingMode.SessionSearchPath);

        services.AddSchemaResolution(options =>
        {
            options.HeaderKey = "X-Workflow";
            options.QueryStringKey = "workflow";
            options.RouteValueKey = "workflow";
            options.ThrowIfNotFound = false;
        });

        services.AddAetherUnitOfWorkMiddleware();

        services.AddAetherNpgsql<MessagingDbContext>(
            configuration.GetConnectionString("Default")!,
            schemaSwitchingMode,
            (_, options) =>
            {
                options.UseNpgsql(
                        configuration.GetConnectionString("Default"),
                        npgsqlOptions =>
                        {
                            npgsqlOptions.MigrationsHistoryTable("__Workflow_Migrations", "sys_queues");
                        })
                    .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
            });

        // withHostedService: true → SDK registers OutboxBackgroundService with adaptive polling.
        // No manual AddHostedService call needed.
        services.AddAetherOutbox<MessagingDbContext>(
            options => configuration.GetSection("Aether:Outbox").Bind(options),
            withHostedService: true);

        return services;
    }
}
```

Key changes vs. original:
- Removed `using BBT.Workflow.Workers.Outbox.HostedServices;`
- Removed `AddHostedServices()` private method and its call in `AddWorkerOutboxModule`
- Added `withHostedService: true` to `AddAetherOutbox`

- [ ] **Step 3: Update `workers/BBT.Workflow.Workers.Outbox/appsettings.json`**

Replace the `Aether` and remove the `Redis` section so the file looks like:

```json
{
  "ApplicationName": "vnext-worker-outbox",
  "ConnectionStrings": {
    "Default": "Host=localhost;Port=5432;Database=Aether_WorkflowDb;Username=postgres;Password=postgres;"
  },
  "Aether": {
    "SchemaSwitchingMode": "SessionSearchPath",
    "Outbox": {
      "Schema": "sys_queues",
      "BatchSize": 100,
      "LeaseDuration": "00:00:30",
      "RetentionPeriod": "7.00:00:00",
      "MaxRetryCount": 5,
      "RetryBaseDelay": "00:01:00",
      "BusyPollingInterval": "00:00:00.100",
      "IdlePollingInterval": "00:00:05",
      "MaxPollingInterval": "00:01:00"
    }
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "System.Net.Http.HttpClient.OtlpTraceExporter.LogicalHandler": "Warning",
      "Microsoft.AspNetCore.Routing.EndpointMiddleware": "Warning",
      "System.Net.Http.HttpClient.Default.LogicalHandler": "Warning",
      "Microsoft.EntityFrameworkCore.Database.Command": "Warning"
    }
  },
  "Telemetry": {
    "ServiceName": "vnext-worker-outbox",
    "ServiceNamespace": "BBT.Workflow.Workers.Outbox",
    "ServiceVersion": "1.0.0",
    "TracingEnabled": false,
    "MetricsEnabled": false,
    "LoggingEnabled": true,
    "Otlp": {
      "Endpoint": "http://localhost:4318",
      "Protocol": "http/protobuf"
    },
    "Tracing": {
      "EnableConsoleExporter": false,
      "EnableOtlpExporter": false,
      "ExcludedPaths": ["^/health$", "^/metrics$", "^/live$", "^/ready$", "^/swagger/*"],
      "AdditionalSources": ["BBT.Workflow.Workers.*", "BBT.Workflow.BackgroundJobs", "BBT.Workflow.SubFlow", "BBT.Workflow.Tasks", "BBT.Workflow.Instances.Events", "BBT.Workflow.Cache"],
      "Headers": ["sub", "act_sub", "jti", "role", "X-Parent-Instance-Id", "User-Agent", "X-Request-Id"]
    },
    "Metrics": {
      "EnableConsoleExporter": false,
      "EnableOtlpExporter": false,
      "AdditionalMeters": []
    },
    "Logging": {
      "EnableConsoleExporter": true,
      "EnableOtlpExporter": true,
      "IncludeFormattedMessage": true,
      "IncludeScopes": true,
      "ParseStateValues": true,
      "ExcludedPaths": ["^/health$", "^/metrics$", "^/live$", "^/ready$", "^/swagger/*"],
      "Enrichers": {
        "Headers": ["sub", "act_sub", "jti", "role", "X-Parent-Instance-Id", "User-Agent", "X-Request-Id"],
        "CustomAttributes": {}
      },
      "Body": {
        "EnableRequestBody": false,
        "EnableResponseBody": false,
        "MaxBodyLengthToCapture": 16384,
        "AdditionalSensitiveJsonFields": [],
        "AdditionalSensitiveHeaderNames": []
      }
    }
  },
  "Vault": {
    "Enabled": false
  },
  "ResultRetry": {
    "MaxRetryAttempts": 3,
    "RetryDelayMilliseconds": 50,
    "UseJitter": true,
    "BackoffType": "Constant",
    "RetryOnErrorCodes": [
      "TransitionLocked",
      "Locked"
    ]
  }
}
```

What changed vs. original:
- `Aether.Outbox.ProcessingInterval` → **removed** (field gone from SDK)
- `Aether.Outbox.Schema` → **added** `"sys_queues"` (explicit, SDK default matches)
- `Aether.Outbox.BusyPollingInterval` → **added** `"00:00:00.100"` (100 ms)
- `Aether.Outbox.IdlePollingInterval` → **added** `"00:00:05"` (5 s)
- `Aether.Outbox.MaxPollingInterval` → **added** `"00:01:00"` (60 s)
- `Redis` section → **removed** (orphaned — Outbox worker has never used Redis)

- [ ] **Step 4: Build the Outbox worker project**

```bash
dotnet build workers/BBT.Workflow.Workers.Outbox/BBT.Workflow.Workers.Outbox.csproj
```

Expected: `Build succeeded` with 0 errors. Verify no reference to `OutboxProcessorHostedService` remains.

- [ ] **Step 5: Commit**

```bash
git add workers/BBT.Workflow.Workers.Outbox/
git commit -m "refactor(outbox-worker): switch to SDK OutboxBackgroundService with adaptive polling

Replace custom OutboxProcessorHostedService (fixed ProcessingInterval loop) with
AddAetherOutbox withHostedService:true → OutboxBackgroundService (adaptive busy/idle/max
backoff). Remove orphaned Redis config section. Align appsettings to new SDK option fields."
```

---

## Task 2: Inbox Worker — Switch to SDK Background Service & Fix Options

**Files:**
- Delete: `workers/BBT.Workflow.Workers.Inbox/HostedServices/InboxProcessorHostedService.cs`
- Modify: `workers/BBT.Workflow.Workers.Inbox/Microsoft/Extensions/DependencyInjection/InboxWorkerServiceCollectionExtensions.cs`
- Modify: `workers/BBT.Workflow.Workers.Inbox/appsettings.json`

- [ ] **Step 1: Delete the custom InboxProcessorHostedService**

```bash
rm workers/BBT.Workflow.Workers.Inbox/HostedServices/InboxProcessorHostedService.cs
```

- [ ] **Step 2: Rewrite `InboxWorkerServiceCollectionExtensions.cs`**

Open `workers/BBT.Workflow.Workers.Inbox/Microsoft/Extensions/DependencyInjection/InboxWorkerServiceCollectionExtensions.cs` and replace its content with:

```csharp
using BBT.Aether.AspNetCore.MultiSchema;
using BBT.Aether.Uow.EntityFrameworkCore;
using BBT.Workflow.Data;
using BBT.Workflow.Workers.Inbox.Forwarding;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service collection extensions specific to Worker Inbox.
/// </summary>
/// <remarks>
/// The Inbox worker is a THIN FORWARDER: it receives distributed events, applies the local
/// domain-match guard, and forwards each to an Orchestration internal endpoint via Dapr service
/// invocation. It deliberately registers only what that requires — domain runtime info, the
/// ASP.NET/Aether core (unit of work + multi-schema + controllers), event subscription, the inbox
/// dedup store, and the messaging DbContext. It does NOT pull the Application or Infrastructure
/// modules, background jobs, the outbox, distributed cache/lock/Redis, the object mapper, or the
/// WorkflowDbContext — those are orchestration concerns and must not run in the Inbox process.
/// </remarks>
public static class InboxWorkerServiceCollectionExtensions
{
    /// <summary>
    /// Adds Worker Inbox specific services.
    /// </summary>
    public static IServiceCollection AddWorkerInboxModule(this IServiceCollection services)
    {
        var configuration = services.GetConfiguration();
        services
            .AddDomainModule()                       // IRuntimeInfoProvider + domain primitives (no infra deps)
            .AddAspNetCoreModules(configuration)     // AddAetherCore (UoW + multi-schema), AspNetCore, controllers
            .AddDaprClients()
            .AddAetherEventBus(options =>
            {
                options.DefaultSource =
                    $"urn:vnext:{configuration.GetValue<string?>("ApplicationName")?.ToLowerInvariant()}";
                options.PrefixEnvironmentToTopic = true;
                options.PubSubName = configuration["DAPR_PUBSUB_STORE_NAME"]!;
            })
            .AddInboxMessagingDbContext(configuration)
            .AddTelemetry(configuration)
            .AddExceptionHandling()
            .AddRuntimeMiddleware()
            .AddHeaderService()
            .AddAppHealthChecks();

        // Inbox = thin forwarder: deliver events to Orchestration via Dapr service invocation.
        // Singleton — depends only on configuration/logger and owns one Dapr-invokable HttpClient.
        services.AddSingleton<IOrchestrationForwarder, DaprOrchestrationForwarder>();

        return services;
    }

    /// <summary>
    /// Registers only the messaging DbContext (sys_queues: inbox tables) plus the schema
    /// resolution + unit-of-work middleware the event-processing controller relies on. Also
    /// registers the inbox processor and its background service via AddAetherInbox.
    /// </summary>
    private static IServiceCollection AddInboxMessagingDbContext(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var schemaSwitchingMode = configuration.GetValue("Aether:SchemaSwitchingMode",
            SchemaSwitchingMode.SessionSearchPath);

        services.AddSchemaResolution(options =>
        {
            options.HeaderKey = "X-Workflow";
            options.QueryStringKey = "workflow";
            options.RouteValueKey = "workflow";
            options.ThrowIfNotFound = false;
        });

        services.AddAetherUnitOfWorkMiddleware();

        services.AddAetherNpgsql<MessagingDbContext>(
            configuration.GetConnectionString("Default")!,
            schemaSwitchingMode,
            (_, options) =>
        {
            options.UseNpgsql(configuration.GetConnectionString("Default"),
                    npgsqlOptions =>
                    {
                        npgsqlOptions.MigrationsHistoryTable("__Workflow_Migrations", "sys_queues");
                    })
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        });

        // withHostedService: true → SDK registers InboxBackgroundService with adaptive polling.
        // AetherInboxOptions is a separate class from AetherOutboxOptions (no longer share config).
        // Schema MUST be set — InboxProcessor skips all runs with a warning if Schema is null.
        services.AddAetherInbox<MessagingDbContext>(
            options => configuration.GetSection("Aether:Inbox").Bind(options),
            withHostedService: true);

        return services;
    }
}
```

Key changes vs. original:
- Removed `using BBT.Aether.DistributedLock;` (no longer needed)
- Removed `using BBT.Workflow.Workers.Inbox.HostedServices;`
- Removed `AddHostedServices()` private method and its call
- Removed `services.AddDaprDistributedLock(...)` call — `InboxProcessor` now uses PostgreSQL lease store (`FOR UPDATE SKIP LOCKED`), not distributed lock
- Removed `services.AddAetherOutbox<MessagingDbContext>(...)` — previously registered to provide `AetherOutboxOptions` for the custom hosted service; no longer needed because `AddAetherInbox` registers its own `AetherInboxOptions`
- Added `withHostedService: true` to `AddAetherInbox`
- Changed config section from `"Aether:Outbox"` → `"Aether:Inbox"` for inbox options

- [ ] **Step 3: Update `workers/BBT.Workflow.Workers.Inbox/appsettings.json`**

Replace the file content with:

```json
{
  "ApplicationName": "vnext-inbox-worker",
  "ConnectionStrings": {
    "Default": "Host=localhost;Port=5432;Database=Aether_WorkflowDb;Username=postgres;Password=postgres;"
  },
  "Aether": {
    "SchemaSwitchingMode": "SessionSearchPath",
    "Inbox": {
      "Schema": "sys_queues",
      "ProcessingBatchSize": 100,
      "LeaseDuration": "00:00:30",
      "RetentionPeriod": "7.00:00:00",
      "MaxRetryCount": 5,
      "RetryBaseDelay": "00:01:00",
      "BusyPollingInterval": "00:00:00.100",
      "IdlePollingInterval": "00:00:05",
      "MaxPollingInterval": "00:01:00",
      "CleanupInterval": "01:00:00",
      "CleanupBatchSize": 1000
    }
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "System.Net.Http.HttpClient.OtlpTraceExporter.LogicalHandler": "Warning",
      "Microsoft.AspNetCore.Routing.EndpointMiddleware": "Warning",
      "System.Net.Http.HttpClient.Default.LogicalHandler": "Warning",
      "Microsoft.EntityFrameworkCore.Database.Command": "Warning"
    }
  },
  "OrchestrationApi": {
    "AppId": "vnext-app",
    "InvocationTimeoutSeconds": 60
  },
  "Telemetry": {
    "ServiceName": "vnext-inbox-worker",
    "ServiceNamespace": "BBT.Workflow.Workers.Inbox",
    "ServiceVersion": "1.0.0",
    "TracingEnabled": false,
    "MetricsEnabled": false,
    "LoggingEnabled": true,
    "Otlp": {
      "Endpoint": "http://localhost:4318",
      "Protocol": "http/protobuf"
    },
    "Tracing": {
      "EnableConsoleExporter": false,
      "EnableOtlpExporter": false,
      "ExcludedPaths": ["^/health$", "^/metrics$", "^/live$", "^/ready$", "^/swagger/*"],
      "AdditionalSources": ["BBT.Workflow.Workers.*", "BBT.Workflow.Instances.Events"],
      "Headers": ["sub", "act_sub", "jti", "role", "X-Parent-Instance-Id", "User-Agent", "X-Request-Id"]
    },
    "Metrics": {
      "EnableConsoleExporter": false,
      "EnableOtlpExporter": false,
      "AdditionalMeters": []
    },
    "Logging": {
      "EnableConsoleExporter": true,
      "EnableOtlpExporter": true,
      "IncludeFormattedMessage": true,
      "IncludeScopes": true,
      "ParseStateValues": true,
      "ExcludedPaths": ["^/health$", "^/metrics$", "^/live$", "^/ready$", "^/swagger/*"],
      "Enrichers": {
        "Headers": ["sub", "act_sub", "jti", "role", "X-Parent-Instance-Id", "User-Agent", "X-Request-Id"],
        "CustomAttributes": {}
      },
      "Body": {
        "EnableRequestBody": false,
        "EnableResponseBody": false,
        "MaxBodyLengthToCapture": 16384,
        "AdditionalSensitiveJsonFields": [],
        "AdditionalSensitiveHeaderNames": []
      }
    }
  },
  "AllowedHosts": "*",
  "Vault": {
    "Enabled": false
  }
}
```

What changed vs. original:
- `Aether.Outbox` section → **renamed** to `Aether.Inbox`
- `Aether.Inbox.ProcessingInterval` → **removed** (field gone from SDK)
- `Aether.Inbox.BatchSize` → **renamed** to `ProcessingBatchSize` (`AetherInboxOptions` uses this name)
- `Aether.Inbox.Schema` → **added** `"sys_queues"` (**required** — null causes processor to skip with warning)
- `Aether.Inbox.BusyPollingInterval` → **added** `"00:00:00.100"`
- `Aether.Inbox.IdlePollingInterval` → **added** `"00:00:05"`
- `Aether.Inbox.MaxPollingInterval` → **added** `"00:01:00"`
- `Aether.Inbox.CleanupInterval` → **added** `"01:00:00"` (processed-message cleanup cadence)
- `Aether.Inbox.CleanupBatchSize` → **added** `1000`

- [ ] **Step 4: Build the Inbox worker project**

```bash
dotnet build workers/BBT.Workflow.Workers.Inbox/BBT.Workflow.Workers.Inbox.csproj
```

Expected: `Build succeeded` with 0 errors. Verify no reference to `InboxProcessorHostedService` or `AddDaprDistributedLock` remains in the built project.

- [ ] **Step 5: Build entire solution to catch cross-project breakage**

```bash
dotnet build
```

Expected: `Build succeeded`. If any project references `InboxProcessorHostedService` or `OutboxProcessorHostedService`, fix them.

- [ ] **Step 6: Commit**

```bash
git add workers/BBT.Workflow.Workers.Inbox/
git commit -m "refactor(inbox-worker): switch to SDK InboxBackgroundService with adaptive polling

Replace custom InboxProcessorHostedService (fixed ProcessingInterval, AetherOutboxOptions
workaround) with AddAetherInbox withHostedService:true → InboxBackgroundService (adaptive
backoff). Remove redundant AddAetherOutbox and AddDaprDistributedLock registrations (InboxProcessor
now uses FOR UPDATE SKIP LOCKED). Rename appsettings section Outbox→Inbox with new field names."
```
