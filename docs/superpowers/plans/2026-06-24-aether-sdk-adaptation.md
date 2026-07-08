# Aether SDK Adaptation — UOW & MultiSchema Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Aether SDK'daki köklü UOW/MultiSchema değişikliklerini vNext projesine uyarlamak: `SchemaSwitchingMode`'u config'e taşımak, `BackgroundJobs` entity'sini `MessagingDbContext`'e geçirmek, UOW kullanımını auditleyin ve Outbox worker'ı `WorkflowDbContext` bağımlılığından kurtarmak.

**Architecture:** Dört bağımsız ama sıralı görev — (1) config extraction, (2) BackgroundJobs entity migration, (3) UOW audit, (4) Outbox/Inbox refactor. Task 4 hem Task 1'e hem Task 2'ye bağlıdır. Her görev kendi commit'ine sahip olacak.

**Tech Stack:** .NET 10, EF Core 10 (Npgsql), Aether SDK (AddAetherNpgsql, IHasEfCoreBackgroundJobs, IUnitOfWorkManager), PostgreSQL multi-schema, Dapr

---

## File Map

| File | Görev | İşlem |
|------|-------|--------|
| `src/BBT.Workflow.HttpApi.Shared/.../WorkflowApiBaseServiceCollectionExtensions.cs` | 1, 2 | Modify |
| `workers/BBT.Workflow.Workers.Inbox/.../InboxWorkerServiceCollectionExtensions.cs` | 1 | Modify |
| `orchestration/.../appsettings.json` | 1, 4 | Modify |
| `execution/.../appsettings.json` | 1 | Modify |
| `workers/BBT.Workflow.Workers.Inbox/appsettings.json` | 1 | Modify |
| `workers/BBT.Workflow.Workers.Outbox/appsettings.json` | 1, 4 | Modify |
| `src/BBT.Workflow.Infrastructure/Data/MessagingDbContext.cs` | 2 | Modify |
| `src/BBT.Workflow.Infrastructure/Data/WorkflowDbContext.cs` | 2 | Modify (deprecate) |
| `workers/BBT.Workflow.Workers.Outbox/.../OutboxWorkerServiceCollectionExtensions.cs` | 2, 4 | Modify |
| `src/BBT.Workflow.Application/BackgroundJobs/Handlers/` (4 handlers) | 3 | Read/Analyze |
| `src/BBT.Workflow.Application/Execution/Transitions/Strategy/AsyncTransitionStrategy.cs` | 3 | Read/Analyze/Fix |
| `src/BBT.Workflow.Application/Execution/Services/TransitionRunner.cs` | 3 | Read/Analyze |
| `workers/BBT.Workflow.Workers.Outbox/HostedServices/ChainReaperHostedService.cs` | 4 | Move → Orchestration |
| `orchestration/.../HostedServices/ChainReaperHostedService.cs` | 4 | Create (moved file) |
| `orchestration/.../OrchestrationApiServiceCollectionExtensions.cs` | 4 | Modify |
| `src/BBT.Workflow.Infrastructure/Migrations/MessagingDb/` | 2 | Create (EF migration) |

---

## Task 1: SchemaSwitchingMode → appsettings.json

**Files:**
- Modify: `src/BBT.Workflow.HttpApi.Shared/Microsoft/Extensions/DependencyInjection/WorkflowApiBaseServiceCollectionExtensions.cs`
- Modify: `workers/BBT.Workflow.Workers.Inbox/Microsoft/Extensions/DependencyInjection/InboxWorkerServiceCollectionExtensions.cs`
- Modify: `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json`
- Modify: `execution/BBT.Workflow.Execution.HttpApi.Host/appsettings.json`
- Modify: `workers/BBT.Workflow.Workers.Inbox/appsettings.json`
- Modify: `workers/BBT.Workflow.Workers.Outbox/appsettings.json`

**Bağlam:** `SchemaSwitchingMode.SessionSearchPath` şu an hardcoded olarak `WorkflowApiBaseServiceCollectionExtensions.cs`'de (WorkflowDbContext + MessagingDbContext için 2 kez) ve `InboxWorkerServiceCollectionExtensions.cs`'de (1 kez) geçiyor. Bunu `"Aether:SchemaSwitchingMode"` config key'i üzerinden okunacak şekilde değiştiriyoruz. Varsayılan `SessionSearchPath` olarak kalacak (geriye dönük uyum için).

- [ ] **Step 1: appsettings.json'lara config key ekle**

`orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json` dosyasındaki mevcut `"Aether"` section'a `SchemaSwitchingMode` key'i ekle (yoksa section'ı oluştur):

```json
"Aether": {
  "SchemaSwitchingMode": "SessionSearchPath"
}
```

Aynı key'i bu dosyaların **mevcut** `"Aether"` section'larına ekle (Inbox ve Outbox'ta section zaten var):
- `execution/BBT.Workflow.Execution.HttpApi.Host/appsettings.json` → `"Aether": { "SchemaSwitchingMode": "SessionSearchPath" }` section oluştur
- `workers/BBT.Workflow.Workers.Inbox/appsettings.json` → mevcut `"Aether"` section'a ekle
- `workers/BBT.Workflow.Workers.Outbox/appsettings.json` → mevcut `"Aether"` section'a ekle

> **Not:** `"Aether": { "Outbox": {...} }` zaten var olan Outbox/Inbox appsettings'lerde `SchemaSwitchingMode` key'ini bu object'e ekle:
> ```json
> "Aether": {
>   "SchemaSwitchingMode": "SessionSearchPath",
>   "Outbox": { ... }
> }
> ```

- [ ] **Step 2: WorkflowApiBaseServiceCollectionExtensions — AddDbContext metodunu güncelle**

`AddDbContext` metodundaki her iki `AddAetherNpgsql` çağrısında hardcoded `SchemaSwitchingMode.SessionSearchPath` yerine config'den oku:

```csharp
public static IServiceCollection AddDbContext(this IServiceCollection services, IConfiguration configuration)
{
    // ...
    var schemaSwitchingMode = configuration.GetValue("Aether:SchemaSwitchingMode",
        SchemaSwitchingMode.SessionSearchPath);

    services.AddSchemaResolution(options =>
    {
        options.HeaderKey = "X-Workflow";
        options.QueryStringKey = "workflow";
        options.RouteValueKey = "workflow";
        options.ThrowIfNotFound = false;
    });

    services.AddAetherNpgsql<WorkflowDbContext>(
        configuration.GetConnectionString("Default")!,
        schemaSwitchingMode,          // ← hardcoded yerine config'den
        (sp, options) =>
        {
            options.UseNpgsql(...);
            // ... mevcut konfigürasyon değişmez
        });

    services.AddAetherUnitOfWorkMiddleware();
    services.AddSingleton<IDataSeedService, WorkflowDataSeedService>();

    services.AddAetherNpgsql<MessagingDbContext>(
        configuration.GetConnectionString("Default")!,
        schemaSwitchingMode,          // ← hardcoded yerine config'den
        (_, options) =>
        {
            options.UseNpgsql(...);
            // ... mevcut konfigürasyon değişmez
        });

    return services;
}
```

- [ ] **Step 3: InboxWorkerServiceCollectionExtensions — AddInboxMessagingDbContext metodunu güncelle**

`AddInboxMessagingDbContext` private metodunda hardcoded `SchemaSwitchingMode.SessionSearchPath` yerine config'den oku. Metoda `IConfiguration configuration` parametresi zaten var:

```csharp
private static IServiceCollection AddInboxMessagingDbContext(
    this IServiceCollection services,
    IConfiguration configuration)
{
    var schemaSwitchingMode = configuration.GetValue("Aether:SchemaSwitchingMode",
        SchemaSwitchingMode.SessionSearchPath);

    // ...

    services.AddAetherNpgsql<MessagingDbContext>(
        configuration.GetConnectionString("Default")!,
        schemaSwitchingMode,          // ← hardcoded yerine config'den
        (_, options) =>
        {
            // ... mevcut konfigürasyon değişmez
        });

    return services;
}
```

- [ ] **Step 4: Build ile doğrula**

```bash
dotnet build src/BBT.Workflow.HttpApi.Shared/BBT.Workflow.HttpApi.Shared.csproj
dotnet build workers/BBT.Workflow.Workers.Inbox/BBT.Workflow.Workers.Inbox.csproj
```

Expected: Her ikisi de hatasız build olsun.

- [ ] **Step 5: Commit**

```bash
git add src/BBT.Workflow.HttpApi.Shared/Microsoft/Extensions/DependencyInjection/WorkflowApiBaseServiceCollectionExtensions.cs
git add workers/BBT.Workflow.Workers.Inbox/Microsoft/Extensions/DependencyInjection/InboxWorkerServiceCollectionExtensions.cs
git add orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json
git add execution/BBT.Workflow.Execution.HttpApi.Host/appsettings.json
git add workers/BBT.Workflow.Workers.Inbox/appsettings.json
git add workers/BBT.Workflow.Workers.Outbox/appsettings.json
git commit -m "config(schema): extract SchemaSwitchingMode to appsettings.json"
```

---

## Task 2: BackgroundJobs Entity → MessagingDbContext

**Files:**
- Modify: `src/BBT.Workflow.Infrastructure/Data/MessagingDbContext.cs`
- Modify: `src/BBT.Workflow.Infrastructure/Data/WorkflowDbContext.cs`
- Modify: `src/BBT.Workflow.HttpApi.Shared/Microsoft/Extensions/DependencyInjection/WorkflowApiBaseServiceCollectionExtensions.cs`
- Modify: `workers/BBT.Workflow.Workers.Outbox/Microsoft/Extensions/DependencyInjection/OutboxWorkerServiceCollectionExtensions.cs`
- Create: EF Core migration (MessagingDbContext)

**Bağlam:** `BackgroundJobInfo` entity'si şu an `WorkflowDbContext`'te — bu context şema-bazlı (multi-tenant) çalıştığından her flow'un şemasında ayrı bir `BackgroundJobs` tablosu oluşuyor. Bu tablo `MessagingDbContext`'e (sabit `sys_queues` şeması) taşınmalı ki tüm job'lar tek bir paylaşımlı tabloda toplanabilsin. Breaking-change'i minimize etmek için `WorkflowDbContext`'teki implementasyon `[Obsolete]` olarak işaretlenecek ama silinmeyecek.

- [ ] **Step 1: MessagingDbContext'e IHasEfCoreBackgroundJobs ekle**

`src/BBT.Workflow.Infrastructure/Data/MessagingDbContext.cs` dosyasını düzenle. `IHasEfCoreBackgroundJobs` interface'ini ve `DbSet<BackgroundJobInfo>` property'sini ekle, `OnModelCreating`'de `builder.ConfigureBackgroundJob()` çağır:

```csharp
using BBT.Aether.Persistence;   // IHasEfCoreBackgroundJobs, BackgroundJobInfo

public class MessagingDbContext(
    DbContextOptions<MessagingDbContext> options)
    : AetherDbContext<MessagingDbContext>(options),
        IHasEfCoreInbox, IHasEfCoreOutbox, IHasEfCoreBackgroundJobs   // ← ekle
{
    public virtual DbSet<InboxMessage> InboxMessages { get; set; }
    public virtual DbSet<OutboxMessage> OutboxMessages { get; set; }

    /// <summary>
    /// Background job tracking records. Primary store — replaces WorkflowDbContext.BackgroundJobs
    /// which is kept only for backwards-compat (see that context's deprecation notice).
    /// </summary>
    public virtual DbSet<BackgroundJobInfo> BackgroundJobs { get; set; }   // ← ekle

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("sys_queues");
        base.OnModelCreating(builder);

        builder.ConfigureInbox();
        builder.ConfigureOutbox();
        builder.ConfigureBackgroundJob();   // ← ekle (schema parametresi yok — HasDefaultSchema'dan alır)
    }
}
```

- [ ] **Step 2: WorkflowDbContext'teki BackgroundJobs implementasyonunu deprecate et**

`src/BBT.Workflow.Infrastructure/Data/WorkflowDbContext.cs` dosyasını düzenle. `IHasEfCoreBackgroundJobs` ve `DbSet<BackgroundJobInfo>` başlığına `[Obsolete]` ekle ama kaldırma:

```csharp
/// <summary>
/// Database context for workflow engine persistence.
/// ...
/// </summary>
/// <remarks>
/// <para>
/// <b>DEPRECATED:</b> <see cref="BackgroundJobInfo"/> / <see cref="IHasEfCoreBackgroundJobs"/>
/// implementasyonu bu context'ten kaldırılacak. Birincil store <c>MessagingDbContext</c>'tir
/// (sys_queues.BackgroundJobs). Bu DbSet ve interface yalnızca geriye dönük uyum için tutulmaktadır;
/// yeni kod <c>AddAetherBackgroundJob&lt;MessagingDbContext&gt;</c> kullanmalıdır.
/// </para>
/// </remarks>
public class WorkflowDbContext : AetherDbContext<WorkflowDbContext>, IHasEfCoreBackgroundJobs
{
    // ...

    /// <summary>
    /// Gets or sets the background jobs.
    /// </summary>
    /// <remarks>
    /// <b>[DEPRECATED]</b> BackgroundJobs birincil store'u MessagingDbContext.BackgroundJobs'a taşındı.
    /// Bu DbSet yalnızca geriye dönük uyum için tutulmaktadır.
    /// </remarks>
    [Obsolete("Use MessagingDbContext.BackgroundJobs. This DbSet will be removed in a future major version.")]
    public virtual DbSet<BackgroundJobInfo> BackgroundJobs { get; set; }

    // OnModelCreating içindeki builder.ConfigureBackgroundJob(schema) çağrısı yerinde kalıyor.
    // ...
}
```

- [ ] **Step 3: AddBackgroundJob kaydını MessagingDbContext'e taşı**

`WorkflowApiBaseServiceCollectionExtensions.cs` → `AddBackgroundJob` metodu:

```csharp
public static IServiceCollection AddBackgroundJob(this IServiceCollection services)
{
    services.AddAetherBackgroundJob<MessagingDbContext>(options =>   // ← WorkflowDbContext → MessagingDbContext
    {
        options.AddHandler<FlowTimeoutJobHandler>(FlowTimeoutJobHandler.HandlerName);
        options.AddHandler<TransitionJobHandler>(TransitionJobHandler.HandlerName);
        options.AddHandler<TransitionTimerJobHandler>(TransitionTimerJobHandler.HandlerName);
        options.AddHandler<LongPollAckTimeoutJobHandler>(LongPollAckTimeoutJobHandler.HandlerName);
    });

    services.AddDaprJobScheduler();

    return services;
}
```

- [ ] **Step 4: OutboxWorkerServiceCollectionExtensions'daki BackgroundJob kaydını güncelle**

`workers/BBT.Workflow.Workers.Outbox/Microsoft/Extensions/DependencyInjection/OutboxWorkerServiceCollectionExtensions.cs` dosyasında:

```csharp
.AddAetherBackgroundJob<MessagingDbContext>()   // ← WorkflowDbContext → MessagingDbContext
```

> **Not:** Bu geçici bir değişiklik — Task 4'te bu satır tamamen kaldırılacak çünkü Outbox worker BackgroundJob çalıştırmayacak.

- [ ] **Step 5: EF Core migrasyonu oluştur (MessagingDbContext)**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext
dotnet ef migrations add AddBackgroundJobsToMessagingContext \
  --project src/BBT.Workflow.Infrastructure/BBT.Workflow.Infrastructure.csproj \
  --context MessagingDbContext \
  --output-dir Migrations/MessagingDb
```

Expected output: Yeni migration dosyası `Migrations/MessagingDb/YYYYMMDD_AddBackgroundJobsToMessagingContext.cs` oluşsun. Migration içeriği: `sys_queues.BackgroundJobs` tablosunu CREATE eden DDL.

- [ ] **Step 6: Migration içeriğini doğrula**

Oluşan migration dosyasını oku ve şunları kontrol et:
- `sys_queues` şemasında `BackgroundJobs` tablosu oluşuyor
- `Id`, `Name`, `Type`, `Status`, `NextExecutionTime`, `JobArgs`, `Priority`, `TryCount`, `LastTryTime`, `NextTryTime`, `IsAbandoned`, `ConcurrencyStamp` kolonları var (Aether BackgroundJobInfo schema'sı)
- `Down()` metodu tabloyu düzgün drop ediyor

- [ ] **Step 7: Build ve migration SQL çıktısını kontrol et**

```bash
dotnet build src/BBT.Workflow.Infrastructure/BBT.Workflow.Infrastructure.csproj
dotnet ef migrations script --idempotent \
  --project src/BBT.Workflow.Infrastructure/BBT.Workflow.Infrastructure.csproj \
  --context MessagingDbContext \
  --output /tmp/messaging_migration.sql
```

`/tmp/messaging_migration.sql` dosyasını oku: `sys_queues.BackgroundJobs` CREATE TABLE ifadesi doğru şemada olmalı.

- [ ] **Step 8: Commit**

```bash
git add src/BBT.Workflow.Infrastructure/Data/MessagingDbContext.cs
git add src/BBT.Workflow.Infrastructure/Data/WorkflowDbContext.cs
git add src/BBT.Workflow.HttpApi.Shared/Microsoft/Extensions/DependencyInjection/WorkflowApiBaseServiceCollectionExtensions.cs
git add workers/BBT.Workflow.Workers.Outbox/Microsoft/Extensions/DependencyInjection/OutboxWorkerServiceCollectionExtensions.cs
git add src/BBT.Workflow.Infrastructure/Migrations/MessagingDb/
git commit -m "feat(infra): migrate BackgroundJobs to MessagingDbContext (sys_queues schema)

WorkflowDbContext.BackgroundJobs [Obsolete] olarak işaretlendi.
AddAetherBackgroundJob<WorkflowDbContext> → MessagingDbContext olarak güncellendi.
sys_queues.BackgroundJobs EF Core migrasyonu eklendi."
```

---

## Task 3: UOW Kullanımı Audit & Temizlik

**Files:**
- Read/Analyze: `src/BBT.Workflow.Application/Execution/Transitions/Strategy/AsyncTransitionStrategy.cs`
- Read/Analyze: `src/BBT.Workflow.Application/Execution/Services/TransitionRunner.cs`
- Read/Analyze: `src/BBT.Workflow.Application/BackgroundJobs/Handlers/TransitionJobHandler.cs`
- Read/Analyze: `src/BBT.Workflow.Application/BackgroundJobs/Handlers/FlowTimeoutJobHandler.cs`
- Read/Analyze: `src/BBT.Workflow.Application/BackgroundJobs/Handlers/LongPollAckTimeoutJobHandler.cs`
- Read/Analyze: `src/BBT.Workflow.Application/BackgroundJobs/Handlers/TransitionTimerJobHandler.cs`
- Read/Analyze: `src/BBT.Workflow.Application/BackgroundJobs/Recovery/ChainReaperService.cs`
- Read/Analyze: `src/BBT.Workflow.Application/BackgroundJobs/TransitionJobEnqueuer.cs`
- Read/Analyze: `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/Steps/ScheduleTransitionsStep.cs`
- Read/Analyze: `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/Steps/HandleLongPollTerminationStep.cs`

**Bağlam:** Aether SDK'daki UOW değişikliklerinden önce pek çok yerde gereksiz `RequiresNew` UoW yaratılıyordu (özellikle pipeline steps içinde). Mevcut değişiklikler (git status'taki modified dosyalar) bunların bir kısmını zaten düzeltiyor. Bu task kalan sorunları tespit eder.

**UOW Tasarım Referansı:**
- `TransitionRunner`: Her transition için `RequiresNew` UoW açar — bu **root UoW**'dur.
- Pipeline steps (`ScheduleTransitionsStep`, `HandleLongPollTerminationStep` vb.) bu root UoW içinde çalışır — kendi UoW'larını açmamalı.
- `AsyncTransitionStrategy.SetInstanceBusyAsync`: `RequiresNew` açar — **kasıtlı** (Busy durumunu Dapr enqueue'dan önce commit etmeli).
- `AsyncTransitionStrategy.EnqueueAndSaveJobAsync`: `RequiresNew` açar — **kasıtlı** (job kaydını Dapr schedule'dan önce commit etmeli).
- `ChainReaperService.SweepAsync`: `RequiresNew` açar — **kasıtlı** (her sweep izole transaction).
- `TransitionJobEnqueuer`: `directly:true` flag ile ambient UoW'a katılır — ayrı `BeginAsync` yok, doğru tasarım.

- [ ] **Step 1: AsyncTransitionStrategy'deki tüm UoW çağrılarını say ve yerlerini belgele**

`AsyncTransitionStrategy.cs` dosyasının tamamını oku. `uowManager.BeginAsync` çağrılarını listele:
- Her çağrının satır numarası ve hangi private metodda olduğu
- `UnitOfWorkScopeOption` değeri (RequiresNew mi, Required mı?)
- İçindeki işlem (ne commit ediyor?)
- Bu UoW gerçekten gerekli mi, değilse neden?

Beklenen bulgu: 2 adet `RequiresNew` (SetInstanceBusy + EnqueueAndSaveJob). Eğer 3'ten fazla varsa, fazla olanları raporla.

- [ ] **Step 2: Pipeline steps'te UoW kullanımını kontrol et**

`ScheduleTransitionsStep.cs` ve `HandleLongPollTerminationStep.cs` dosyalarını oku. Şunları ara:
- `IUnitOfWorkManager` injection'ı var mı?
- `BeginAsync` çağrısı var mı?
- Eğer varsa: bu step zaten TransitionRunner'ın root UoW'u içinde çalışıyor. `RequiresNew` burada **gereksiz** — kaldır, ambient UoW kullan.
- Eğer yoksa: doğru tasarım, devam et.

- [ ] **Step 3: BackgroundJob handlers'ı kontrol et**

4 handler dosyasını oku: `TransitionJobHandler.cs`, `FlowTimeoutJobHandler.cs`, `LongPollAckTimeoutJobHandler.cs`, `TransitionTimerJobHandler.cs`.

Kontrol et:
- Handler'lar kendi `BeginAsync` çağrısı yapıyor mu?
- Aether BackgroundJob framework zaten her handler invocation'ı için UoW sağlıyor (bu framework sorumluluğu). Handler'ların ayrıca UoW açması **çift wrapping** oluşturur — gereksizse kaldır.

- [ ] **Step 4: ChainReaperService UoW kullanımını doğrula**

`ChainReaperService.cs` dosyasını oku. Sweep işlemi için `RequiresNew` kullanıyor — bu doğru (izolasyon gerekli). Sadece şunu doğrula: her `SweepAsync` çağrısı yeni UoW açıyor ve commit ediyor, UoW scope dışında repository çağrısı yok.

- [ ] **Step 5: Tespit edilen gereksiz UoW'ları kaldır**

Step 2 veya 3'te gereksiz `RequiresNew` tespit edilirse:
- İlgili sınıftan `IUnitOfWorkManager` injection'ını kaldır
- `BeginAsync` / `CommitAsync` çağrılarını kaldır
- `await using var uow = ...` bloğunu kaldır, içindeki kodu doğrudan bırak

Eğer hiç gereksiz UoW bulunamazsa bu step'i "no-op" olarak işaretle.

- [ ] **Step 6: Build + test + commit**

```bash
dotnet build
dotnet test test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj
```

Expected: Tüm testler geçmeli.

```bash
git add -p   # yalnızca UOW değişikliklerini stage'e al
git commit -m "refactor(uow): remove redundant RequiresNew UoW wrapping in pipeline"
```

Eğer değişiklik yoksa commit atla.

---

## Task 4: Outbox Worker Slim-Down + ChainReaper Orchestration'a Taşıma

**Files:**
- Move: `workers/BBT.Workflow.Workers.Outbox/HostedServices/ChainReaperHostedService.cs` → `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/HostedServices/ChainReaperHostedService.cs`
- Modify: `orchestration/.../OrchestrationApiServiceCollectionExtensions.cs` (ChainReaper kaydı + namespace ekle)
- Modify: `workers/BBT.Workflow.Workers.Outbox/.../OutboxWorkerServiceCollectionExtensions.cs` (slim-down)
- Modify: `orchestration/.../appsettings.json` (WorkflowExecution.EnableChainReaper kontrolü)
- Modify: `workers/BBT.Workflow.Workers.Outbox/appsettings.json` (WorkflowExecution section kaldır)

**Bağlam:**

**Inbox Worker** durumu: Zaten sadece `MessagingDbContext` kullanıyor (`AddInboxMessagingDbContext` private metodu). WorkflowDbContext bağımlılığı **yok**. Task 1 ile `SchemaSwitchingMode` config'e taşındı. Inbox için başka değişiklik gerekmiyor.

**Outbox Worker** sorunu: Şu an `AddApplicationModule() + AddInfrastructureModule() + AddDbContext()` çağırarak tam stack'i yüklüyor. Bu gereksiz — `WorkflowDbContext`, instance repository'leri, distributed cache/lock, Redis, event hooks hiçbiri Outbox publisher'ı için şart değil. Outbox worker'ın tek işi: `sys_queues.OutboxMessages` tablosunu okuyup Dapr pub/sub'a publish etmek.

**ChainReaper taşınma gerekçesi:** `ChainReaperHostedService` instance'ları okuyup manipüle ediyor (`IInstanceRepository`, `IInstanceJobRepository`). Bu bir orchestration sorumluluğu — Orchestration host zaten Application/Infrastructure stack'ini yüklüyor ve bu servislere erişimi var. Outbox worker'da çalışması gereksiz bağımlılıklar yaratıyor.

- [ ] **Step 1: ChainReaperHostedService dosyasını taşı**

Dosyayı şuraya kopyala:
```
orchestration/BBT.Workflow.Orchestration.HttpApi.Host/HostedServices/ChainReaperHostedService.cs
```

Namespace'i güncelle:
```csharp
// ESKİ:
namespace BBT.Workflow.Workers.Outbox.HostedServices;

// YENİ:
namespace BBT.Workflow.HostedServices;  // Orchestration host'un mevcut namespace'i ile aynı
```

Dosyanın `using` direktifleri ve sınıf içeriği değişmez — sadece namespace değişiyor.

- [ ] **Step 2: Orchestration host'a ChainReaperHostedService kaydını ekle**

`orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Microsoft/Extensions/DependencyInjection/OrchestrationApiServiceCollectionExtensions.cs` dosyasındaki `AddHostedServices` metoduna ekle:

```csharp
private static IServiceCollection AddHostedServices(this IServiceCollection services)
{
    #if DEBUG
    services.AddHostedService<MultiSchemaMigrationHostedService>();
    #endif
    services.AddHostedService<DomainDiscoveryInitializationHostedService>();
    services.AddHostedService<ChainReaperHostedService>();   // ← ekle
    return services;
}
```

- [ ] **Step 3: Orchestration appsettings.json'da WorkflowExecution.EnableChainReaper'ı doğrula**

`orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json` dosyasını oku. `WorkflowExecution.EnableChainReaper: true` zaten var mı kontrol et (arama ile doğrulayabilirsin). Yoksa ekle:

```json
"WorkflowExecution": {
  "EnableChainReaper": true
}
```

- [ ] **Step 4: Outbox appsettings.json'dan WorkflowExecution section'ını kaldır**

`workers/BBT.Workflow.Workers.Outbox/appsettings.json` dosyasından `WorkflowExecution` section'ını tamamen kaldır — Outbox worker artık ChainReaper çalıştırmıyor.

- [ ] **Step 5: Outbox worker'a slim MessagingDbContext registrar ekle**

`OutboxWorkerServiceCollectionExtensions.cs` dosyasına yeni private metod ekle (Inbox'taki `AddInboxMessagingDbContext` ile paralel):

```csharp
/// <summary>
/// Registers only the messaging DbContext (sys_queues outbox/inbox tables) and the outbox
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

    services.AddAetherOutbox<MessagingDbContext>(options =>
        configuration.GetSection("Aether:Outbox").Bind(options));

    return services;
}
```

- [ ] **Step 6: OutboxWorkerServiceCollectionExtensions'ı slim yap**

`AddWorkerOutboxModule` metodunu yeniden yaz. Gereksiz modülleri kaldır, sadece ihtiyaç duyulanları bırak:

```csharp
public static IServiceCollection AddWorkerOutboxModule(this IServiceCollection services)
{
    var configuration = services.GetConfiguration();
    services
        .AddDomainModule()                  // IRuntimeInfoProvider + temel domain tipleri
        .AddAspNetCoreModules(configuration) // Middleware altyapısı (health check, exception handling)
        .AddDaprClients()
        .AddAetherEventBus(options =>
        {
            options.DefaultSource =
                $"urn:vnext:{configuration.GetValue<string?>("ApplicationName")?.ToLowerInvariant()}";
            options.PrefixEnvironmentToTopic = true;
            options.PubSubName = configuration["DAPR_PUBSUB_STORE_NAME"]!;
        })
        .AddOutboxMessagingContext(configuration)  // ← YENİ: sadece MessagingDbContext + outbox
        .AddTelemetry(configuration)
        .AddExceptionHandling()
        .AddRuntimeMiddleware()
        .AddHeaderService()
        .AddHostedServices()
        .AddAppHealthChecks();
    return services;
}

private static IServiceCollection AddHostedServices(this IServiceCollection services)
{
    services.AddHostedService<OutboxProcessorHostedService>();
    // ChainReaperHostedService kaldırıldı — Orchestration host'a taşındı
    return services;
}
```

**Kaldırılanlar ve gerekçeleri:**
- `AddApplicationModule()` — instance service'leri yok, gereksiz
- `AddInfrastructureModule(configuration)` — WorkflowDbContext + repo'lar yok, gereksiz
- `AddResultResilience(configuration)` — transition execution yok, gereksiz
- `AddWorkflowEventHooks()` — event hooks Orchestration host'ta çalışır, burada değil
- `AddDomainEventsInfrastructure()` — `AddOutboxMessagingContext` içinde zaten `AddAetherOutbox` var; `AddAetherInbox` ve `AddAetherDomainEvents` outbox publisher için gerekmez
- `AddInfrastructureRuntimeServices()` — WorkflowDbContext bağımlı, gereksiz
- `AddDbContext(configuration)` — `AddOutboxMessagingContext` ile değiştirildi
- `AppMapper()` — domain mapping yok, gereksiz
- `AddDistributedCache(configuration)` — cache kullanımı yok, gereksiz
- `AddDistributedLock(configuration)` — lock kullanımı yok, gereksiz
- `AddTransitionLockScope()` — transition execution yok, gereksiz
- `.AddAetherBackgroundJob<MessagingDbContext>()` — job çalıştırmak yok, gereksiz
- `AddDaprJobScheduler()` — job scheduling yok, gereksiz
- `AddRedis()` — cache/lock kaldırıldı, Redis gerekmez

- [ ] **Step 7: Outbox worker'ın Redis config gereksinimi kalmadı mı doğrula**

`workers/BBT.Workflow.Workers.Outbox/appsettings.json` dosyasında `Redis` section'ı var. Task 6'da `AddRedis()` kaldırıldığından bu section artık kullanılmıyor. Silmek opsiyonel ama appsettings temizliği için silebilirsin.

- [ ] **Step 8: Outbox worker build'ini doğrula**

```bash
dotnet build workers/BBT.Workflow.Workers.Outbox/BBT.Workflow.Workers.Outbox.csproj
```

Expected: Build başarılı. Eğer `AddDomainModule` veya `AddAspNetCoreModules`'un ihtiyaç duyduğu bir servis eksik ise, ilgili registration'ı `AddOutboxMessagingContext`'e ekle.

- [ ] **Step 9: Orchestration host build'ini doğrula**

```bash
dotnet build orchestration/BBT.Workflow.Orchestration.HttpApi.Host/BBT.Workflow.Orchestration.HttpApi.Host.csproj
```

Expected: Build başarılı. `ChainReaperHostedService` ile ilgili namespace hatası varsa using direktiflerini kontrol et.

- [ ] **Step 10: Eski ChainReaperHostedService dosyasını sil**

```bash
git rm workers/BBT.Workflow.Workers.Outbox/HostedServices/ChainReaperHostedService.cs
```

- [ ] **Step 11: Tüm testleri çalıştır**

```bash
dotnet test
```

Expected: Tüm testler geçmeli.

- [ ] **Step 12: Commit**

```bash
git add orchestration/BBT.Workflow.Orchestration.HttpApi.Host/HostedServices/ChainReaperHostedService.cs
git add orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Microsoft/Extensions/DependencyInjection/OrchestrationApiServiceCollectionExtensions.cs
git add orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json
git add workers/BBT.Workflow.Workers.Outbox/
git commit -m "refactor(outbox): slim worker to MessagingDbContext-only, move ChainReaper to Orchestration

- ChainReaperHostedService → Orchestration host (orchestration kaygısı)
- OutboxWorkerServiceCollectionExtensions: Application/Infrastructure/WorkflowDbContext kaldırıldı
- Outbox worker sadece MessagingDbContext + outbox processor + event bus kullanıyor
- Inbox worker zaten temizdi, değişiklik yok"
```

---

## Kontrol Listesi (Self-Review)

- [ ] Task 1: 4 appsettings.json dosyasında `Aether:SchemaSwitchingMode` key'i var
- [ ] Task 1: `WorkflowApiBaseServiceCollectionExtensions` ve `InboxWorkerServiceCollectionExtensions` config'den okuyor
- [ ] Task 2: `MessagingDbContext` `IHasEfCoreBackgroundJobs` implement ediyor
- [ ] Task 2: `WorkflowDbContext.BackgroundJobs` `[Obsolete]` attribute'u taşıyor
- [ ] Task 2: `AddAetherBackgroundJob<MessagingDbContext>()` kayıtlı (WorkflowDbContext değil)
- [ ] Task 2: MessagingDb migration dosyası var ve `sys_queues.BackgroundJobs` oluşturuyor
- [ ] Task 3: Gereksiz UoW varsa kaldırıldı; tasarım kararı belgelendi
- [ ] Task 4: `ChainReaperHostedService` Orchestration host namespace'inde
- [ ] Task 4: Orchestration `AddHostedServices` ChainReaper'ı kaydediyor
- [ ] Task 4: Outbox worker sadece `MessagingDbContext` kullanıyor
- [ ] Task 4: `WorkflowDbContext` Outbox worker'dan hiçbir yerde referans edilmiyor
- [ ] Tüm `dotnet build` başarılı
- [ ] `dotnet test` geçiyor
