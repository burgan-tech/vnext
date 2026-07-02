# vnext (BBT.Workflow) — Dependency Trace Contract

> `vnext`, Amorphie vNext'in **.NET 10 server-side çekirdek motorudur** (`vnext.sln`, `global.json` → .NET 10;
> ürün sürümü `common.props:Version 0.0.26`). İki API host + worker'lar + Execution servisi. Kalıcılık
> PostgreSQL (çok-şemalı), SP YOK. Base adres/appId/connection string bu repoda sabit DEĞİLDİR — `appsettings`
> ve environment'tan okunur (§3b, §5).

## 1. Giriş noktaları

### 1a. Orchestration API (dışa dönük, port 4201 — `CLAUDE.md:49`)
`[Route("api/v{version:apiVersion}")]` tabanlı; `{domain}` = flow şeması ayrıştırıcısı.

| Uç | Yer | Amaç |
|---|---|---|
| `POST {domain}/workflows/{workflow}/instances/start` | `InstanceController.cs:45` | Yeni workflow instance başlat (`?sync`, `?version`, `?extensions`) |
| `POST …/sub/instances/start` | `InstanceController.cs:97` | Subflow instance başlat |
| `POST …/instances/{instance}/complete` | `InstanceController.cs:135` | Instance tamamla |
| `POST …/instances/{instance}/sub/state` / `sub/fault` | `InstanceController.cs:153,171` | Subflow'dan parent'a state/fault raporu |
| `PUT …/instances/{instance}/busy` | `InstanceController.cs:195` | Instance'ı Busy'ye al |
| `POST …/instances/{instance}/cancel-cleanup` / `child-cancel` / `child-fault` | `InstanceController.cs:221,237,255` | İptal/temizlik ve alt-instance yönetimi |
| `POST …/instances/{instance}/transitions/{transitionKey}/enqueue` | `InstanceController.cs:275` | Transition kuyruğa al (async) |
| `PATCH …/instances/{instance}/transitions/{transitionKey}` | `InstanceController.cs:322` | **Transition tetikle** (ana geçiş ucu) |
| `POST …/instances/{instance}/longpoll/ack` | `InstanceController.cs:385` | Long-poll acknowledge (pipeline resume) |
| `POST …/instances/{instance}/retry` | `InstanceController.cs:426` | Faulted instance retry |
| `GET …/instances/{instance}` / `instances` / `…/transitions` / `…/data` | `InstanceController.cs:467,515,548,571` | Instance/liste/transition/data sorgu |
| `GET {domain}/functions` · `{domain}/functions/{function}` (GET/POST/PATCH/DELETE) | `FunctionController.cs:24,33,91-93` | Domain-scope function çağrısı |
| `… /workflows/{workflow}/instances/{instance}/functions/{function}` | `FunctionController.cs:54,116-118` | Instance-scope function (state/view/data/authorize) |
| `POST definitions/publish` · `GET definitions/re-initialize` | `DefinitionController.cs:14,30` | Workflow/component tanımı yayınla / yeniden başlat |
| `GET {domain}/components[/{type}[/{key}[/{cVersion}]]]` · `…/mappings/{key}/code` | `ComponentDiscoveryController.cs:26,40,58,75,93` | Component keşfi |
| `GET config` · `POST utilities/invalidate` · `POST utilities/discovery/refresh` | `UtilityController.cs:30,52,69` | Runtime config, cache invalidation, discovery |

### 1b. Execution API (iç servis, port 4202 — `CLAUDE.md:49`)
- `POST api/v1/execution/invoke/{type}/{key}` (`ExecutionController.cs:34`) — Orchestration'dan Dapr ile
  gelen `TaskEnvelope`'u `ITaskInvokerRegistry`'ye yönlendirir (`ExecutionController.cs:56`). DB erişimi yok.

### 1c. Monitoring API (`monitoring/BBT.Workflow.Monitor.HttpApi.Host`)
- `MonitorStatsController.cs:12` (`…/stats/instances|states|faults|tasks|duration|transitions`),
  `MonitorInstanceController.cs:20`, `MonitorConfigController.cs:12`. Salt-okunur istatistik/izleme.

### 1d. Arka plan / tetikleyici olmayan giriş noktaları
- **Hosted service'ler (Orchestration):** `ChainReaperHostedService.cs`,
  `HostedServices/Discovery/DomainDiscoveryInitializationHostedService.cs`, `Services/MultiSchemaMigrationHostedService.cs`.
- **Worker'lar (`workers/`):** `Workers.Inbox` (dağıtık `IEventHandler<T>` tüketicileri),
  `Workers.Outbox` (transactional outbox publish), `DbMigrator` (deploy-time EF migration).
- **Background job handler'ları:** `src/BBT.Workflow.Application/BackgroundJobs/Handlers/` (örn.
  `FlowTimeoutJobHandler.cs`, subflow start/forward job'ları `Execution/PostCommit/Handlers/`).
- **`init/VNext.Init.Host`:** Node.js tabanlı bootstrap/publish yardımcı host'u (C# değil).

## 2. İç bağımlılık grafı
`.csproj ProjectReference` zinciri (Clean Architecture; ok = "referans verir"):

```
Domain            → Modules.Scripting, Events.Contracts        (altyapıya bağımsız çekirdek)
Tasks.Abstractions→ Domain
Execution.Abstractions → (yok — saf binding/DTO kontratları)
Application       → Domain, Tasks.Abstractions, Execution.Abstractions
Infrastructure    → Application, Domain                        (EF Core, Dapr, event hook'ları)
HttpApi.Shared    → Domain, Infrastructure                     (ortak middleware/telemetri)
Execution         → Execution.Abstractions                     (task invoker'lar)

# Host'lar:
Orchestration.HttpApi.Host → HttpApi.Shared            (Application/Infra transitively)
Execution.HttpApi.Host     → HttpApi.Shared, Execution
Monitor.HttpApi.Host       → HttpApi.Shared, Monitor.Application (→ Domain, Application)

# Worker'lar:
Workers.Inbox / Workers.Outbox → HttpApi.Shared, Events.Contracts
DbMigrator                     → HttpApi.Shared, Infrastructure
```
> Kritik yön: **Infrastructure → Application** referansı Clean Architecture'ın tersine görünür ama
> Aether SDK modül desenidir; interface'ler Domain/Application'da, implementasyonlar Infrastructure'da
> (`WorkflowInfrastructureModuleServiceCollectionExtensions.cs:39`). Orchestration host'u Application/Infra'yı
> **doğrudan değil, HttpApi.Shared üzerinden transitif** alır.

## 3. Dış bağımlılıklar

### 3a. Paketler (mimari açıdan anlamlı; `Directory.Build.props` + `*.csproj`)
- **BBT.Aether SDK** (`AetherPackageVersion 1.0.29`, `Directory.Build.props`): `BBT.Aether.Application/Domain/
  Infrastructure/AspNetCore/Npgsql/Aspects/Mapperly`. UoW, Result pattern, multi-schema, entity/audit tabanı,
  controller tabanı — motorun altyapı iskeleti bu SDK'dır (dış repo `burgan-tech/aether`).
- **Dapr** (`DaprPackageVersion 1.17.9`): `Dapr.Client` (service invoke — `RemoteInvokerService.cs:7,79`),
  `Dapr.AspNetCore`, `Dapr.Extensions.Configuration`, `Dapr.Jobs` (scheduled transition/timer).
- **EF Core 10** (`EfCorePackageVersion 10.0.4.0`, CS1705 nedeniyle pinli): `Microsoft.EntityFrameworkCore`,
  `Npgsql.EntityFrameworkCore.PostgreSQL`, `.Sqlite` (test/tasarım-zamanı).
- **Scripting:** `Microsoft.CodeAnalysis.CSharp.Scripting` (Roslyn — `modules/BBT.Workflow.Modules.Scripting`),
  `DynamicExpresso.Core` (hafif ifade değerlendirme — `Tasks/Evaluators/DynamicExpressoConditionEvaluator.cs`).
- **Resilience:** `Polly` + `Polly.Extensions.Http` + `Microsoft.Extensions.Http.Polly`
  (`Execution/ErrorHandling/PollyRetryPolicyFactory.cs`).
- **Gözlemlenebilirlik:** `OpenTelemetry.Api`, Elastic APM (`ElasticApmPackageVersion`). `Ulid` (kimlik üretimi).

### 3b. Dış servisler (hangi client, base adres KAYNAĞI)
- **Execution servisi (kendi iç servisi):** Orchestration → Dapr service invocation → Execution.
  `RemoteInvokerService.CreateInvokeMethodRequest(appId, "/api/v1/execution/invoke/{taskType}/{taskKey}")`
  (`RemoteInvokerService.cs:63-66`); `appId` = config `ExecutionApi:AppId` (varsayılan `vnext-execution`, `:31`),
  timeout `ExecutionApi:InvocationTimeoutSeconds` (varsayılan 60, `:33`).
- **Workflow task'larının hedefi (harici servisler):** task **invoker**'ları Execution içinde yapar
  (`src/BBT.Workflow.Execution/Invokers/`): `HttpTaskInvoker`, `SoapTaskInvoker`, `DaprServiceTaskInvoker`,
  `DaprBindingTaskInvoker`, `DaprHttpEndpointTaskInvoker`, `DaprPubSubTaskInvoker`, `StateStoreTaskInvoker`;
  trigger invoker'ları (`StartTrigger/DirectTrigger/SubProcess/GetInstanceData/GetInstances`) çapraz-domain.
  HTTP çağrıları named client ile: `WorkflowHttpClient` / `WorkflowHttpClient.NoSslValidation`
  (`WorkflowHttpClientNames.cs:11,16`). Hedef URL'ler workflow **tanımındaki** task binding'lerinden gelir
  (bu repoda değil — dağıtılan domain tanımlarında).
- **Dapr sidecar:** pub/sub (event bus), binding, state store, jobs; resource lock
  (`Infrastructure/Execution/ResourceLock/DaprResourceLockService.cs`).

### 3c. Veritabanları / kalıcılık (stored procedure YOK — EF Core)
- **Stored procedure çağrısı yoktur.** Kalıcılık EF Core repository'leridir; ancak performans için bazı
  PostgreSQL native JSONB sorgularında parametreli `FromSqlRaw` kullanılır (`Domain/QueryExtensions/
  PostgreSqlJsonFilterService.cs:164`, `Infrastructure/Instances/EfCoreInstanceRepository.cs:734,853,1257`).
  "SP nerede?" sorusunun cevabı: SP yok — EF Core + hedefli raw JSONB filtresi.
- **PostgreSQL — `WorkflowDbContext`** (`Infrastructure/Data/WorkflowDbContext.cs:28`, namespace
  `BBT.Workflow.Data`): DbSet'ler `Instances` (`:48`), `InstanceCorrelations` (`:53`), `InstancesData` (`:58`),
  `InstanceActions` (`:63`), `InstanceTasks` (`:68`), `InstanceTransitions` (`:73`), `InstanceJobs` (`:78`),
  `BackgroundJobs` (`:88` — **DEPRECATED**, `:20-24` notu: birincil store artık `MessagingDbContext`).
- **Çok-şemalı kiracılık:** her flow kendi PostgreSQL şemasında. Şema `ICurrentSchema` ile HTTP header/route/
  query'den çözülür; compiled model şema başına `SchemaAwareModelCacheKeyFactory` ile cache'lenir; `SET
  search_path` GÖNDERİLMEZ (PgBouncer transaction-mode güvenli — `WorkflowDbContext.cs:17-24`). Altyapı işlemleri
  `currentSchema.Use(flow)` ile sarılır (`CLAUDE.md:104`).
- **PostgreSQL — `MessagingDbContext`** (`Infrastructure/Data/MessagingDbContext.cs`): Inbox/Outbox + BackgroundJobs
  (`sys_queues` şeması). Migration'lar `Infrastructure/Migrations/` (+ `Migrations/MessagingDb/`).
- **Redis:** `IDistributedCache` (component/definition cache).
- **ClickHouse:** analitik/DataSink (`Domain/ClickHouse/`, `Infrastructure/DataSink/`) — instance verisi akıtımı.

## 4. İz sürme algoritması (adım adım)
"Bir uç / task / state / transition / entity değişirse etkisini nasıl bulurum":
1. **Endpoint mi?** İlgili `*Controller.cs`'i aç (§1); action → çağırdığı AppService (`Application/*/…AppService.cs`,
   örn. `InstanceCommandAppService`, `FunctionAppService`, `DefinitionAppService`, `ComponentDiscoveryAppService` —
   `WorkflowApplicationModuleServiceCollectionExtensions.cs:69-84`).
2. **Transition/pipeline davranışı mı?** `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/`:
   `TransitionPipeline.cs` (orkestrasyon) → `TransitionExecutor.cs` (adım planı, `:219-269`) → `Steps/*Step.cs`.
   Adım sırası `LifecycleOrder.cs`; profil dışlamaları `PipelineProfileResolver.cs`.
3. **Task mı?** Türü belirle (`TaskEnums.cs:9` numaralı `TaskType` / `TaskTypes.cs:7` string). Executor
   `Application/Tasks/Executors/<aile>/…Executor.cs` (Orchestration, workflow context'i bilir); Remote ise
   `RemoteInvokerService.cs:79` → Execution `Invokers/<...>Invoker.cs`. Yerel/uzak kararı `ExecutionMode`
   (`Tasks.Abstractions/Core/ExecutionMode.cs:13,19`).
4. **Entity/kalıcılık mı?** `Domain/Instances/` (aggregate `Instance.cs:13`) veya `Domain/Definitions/`
   (`Workflow.cs:16`, `State`, `Transition`, `Task`, `Function`, `View`, `Schema`, `Extension`, `Mapping`).
   Repository impl'leri `Infrastructure/Instances/`; DbSet eşlemesi `WorkflowDbContext.cs`.
5. **DI ile çözülen bir bağımlılık mı?** İlgili projenin `Microsoft/Extensions/DependencyInjection/*ServiceCollectionExtensions.cs`'inde
   interface→impl kaydını bul (§ code-structure-contract §3).
6. **Domain event mi?** Çift-işleme: senkron `IEventPublishHook<T>` (`Infrastructure/*/Events/`) + asenkron
   `IEventHandler<T>` (`workers/BBT.Workflow.Workers.Inbox/Handlers/`) — `CLAUDE.md:106-110`.
7. Repo dışına çıkan her ucu (istemci SDK tüketicileri, workflow **tanım** repoları, harici HTTP/SOAP hedefleri,
   Dapr/PostgreSQL/Redis/ClickHouse, Aether SDK içi) `external_dependencies`'e yaz.

## 5. DURDU kriterleri
İz aşağıdaki durumlarda bu repoda biter; `DURDU: <somut neden>` yaz:
- **Base adres / appId / connection string:** kodda sabit değil; `appsettings.json` + environment + Dapr config
  (`RemoteInvokerService.cs:31` gibi `configuration[...]`). "Bu çağrı hangi URL/DB'ye gider" → DURDU: konfig-güdümlü.
- **Workflow/view/schema TANIMLARI:** hangi state/transition/task/function/view olduğu bu motor kodunda DEĞİL,
  dağıtılan **domain tanım** repolarında (flutter.backoffice.json, backoffice-flow vb.) → external_dependency.
- **Harici HTTP/SOAP task hedefi:** invoker jenerik; gerçek endpoint task binding'inden (tanımdan) gelir → DURDU.
- **Aether SDK içi davranış:** UoW, multi-schema model cache, Result, controller tabanı `BBT.Aether.*` paketinde
  (dış repo `burgan-tech/aether`) → external_dependency.
- **İstemci SDK / tüketici davranışı:** long-poll döngüsü, view render vNext client SDK/uygulamalarda
  (vnext-client-workflow-manager, flutter.core neo_core) → external_dependency.
- **Roslyn script içeriği:** workflow'a gömülü C# script'lerinin (`IConditionMapping`, `IMapping`) mantığı
  runtime'da derlenir; içerik tanımdan gelir → DURDU: script-güdümlü, tanımda.
