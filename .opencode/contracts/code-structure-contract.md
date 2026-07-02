# vnext (BBT.Workflow) — Code Structure Contract

## 1. Klasör / katman haritası (üst seviye + `src/`)
Kök namespace her yerde `BBT.Workflow.*`; klasör adları namespace'i **her zaman** izlemez (bkz. §4, tuzaklar).

| Dizin / Proje | Sorumluluk (tek cümle) | Kilit dosya |
|---|---|---|
| `src/BBT.Workflow.Domain` | Aggregate/entity, domain event, value object, iş kuralları, workflow **tanım** modeli, script kontratları — altyapıya bağımsız | `Definitions/Workflow.cs:16`, `Instances/Instance.cs:13` |
| `src/BBT.Workflow.Application` | Uygulama servisleri, DTO, **transition pipeline**, task executor/koordinatör, function/subflow/error-handling — Domain'e bağımlı | `Execution/Transitions/Pipeline/TransitionPipeline.cs:20` |
| `src/BBT.Workflow.Infrastructure` | EF Core repository/DbContext, Dapr, event hook, discovery, resource lock, gateway — Domain+App interface'lerini impl eder | `Data/WorkflowDbContext.cs:28` |
| `src/BBT.Workflow.Events.Contracts` | Dağıtık event tanımları (CloudEvents; instance/subflow/continuation event'leri) | `Instances/Events/*.cs` |
| `src/BBT.Workflow.Execution` | Stateless **task invoker**'lar + invoker registry (Execution servisi çekirdeği) | `Services/TaskInvokerRegistry.cs:11` |
| `src/BBT.Workflow.Execution.Abstractions` | Orchestration↔Execution kararlı kontratları: `TaskEnvelope`, binding'ler, `TaskTypes` | `TaskTypes.cs:7`, `TaskEnvelope.cs` |
| `src/BBT.Workflow.Tasks.Abstractions` | Task invoker/evaluator interface çekirdeği (`ITaskInvoker`, `ExecutionMode`, `TaskInvocationContext`) | `Core/ExecutionMode.cs:7` |
| `src/BBT.Workflow.HttpApi.Shared` | İki API host'u için ortak middleware, telemetri zenginleştirme, sağlık kontrolü | `Microsoft/.../WorkflowApiBaseServiceCollectionExtensions.cs` |
| `modules/BBT.Workflow.Modules.Scripting` | Roslyn tabanlı C# script motoru + sandbox + yasaklı-API analizörü | `BBT/Workflow/Scripting/Evaluators/CSharpEvaluator.cs` |
| `orchestration/…Orchestration.HttpApi.Host` | Dışa dönük API host (port 4201): instance/transition/function/definition/component | `Controllers/Instances/InstanceController.cs:22` |
| `execution/…Execution.HttpApi.Host` | İç API host (port 4202): stateless task invoke | `Controllers/Executions/ExecutionController.cs:16` |
| `monitoring/…Monitor.*` | Salt-okunur izleme/istatistik API'si + application katmanı | `Controllers/MonitorStatsController.cs:12` |
| `workers/Workers.Inbox` · `Workers.Outbox` · `DbMigrator` | Dağıtık event tüketimi / transactional outbox publish / deploy-time EF migration | `workers/BBT.Workflow.Workers.Inbox/Handlers/` |
| `tools/BBT.Workflow.Mcp.Server` | Geliştirici MCP sunucusu (yardımcı araç) | `BBT.Workflow.Mcp.Server.csproj` |
| `vnext-meta/` | Sürüm/şema manifest'i, feature/deprecation/known-issue/güvenlik-politikası JSON'ları (kod değil, Node.js meta paketi) | `version-manifest.json`, `index.js` |
| `init/VNext.Init.Host` | Node.js bootstrap/publish yardımcı host'u | `package-api-server.js` |
| `etc/` | Docker, Dapr component'leri, ortam config'leri | `etc/docker/run-docker.sh` |
| `docs/` | Geliştirici dokümantasyonu (mimari, runtime, domain, contracts) | `docs/runtime/task-executors-and-invokers.md` |

## 2. Katman mimarisi (gerçek desenler — genel prensip değil)
- **Deterministik transition pipeline:** Her geçiş, `LifecycleOrder` sabitleriyle sıralı `ITransitionStep`
  adımlarından geçer (`ITransitionStep.cs:10`, sıra tanımı `LifecycleOrder.cs`). Adımlar `Result<StepOutcome>`
  döner; `StepOutcome`: `Continue()` / `Stop()` / `SkipTo(order)` / `SkipToFinalize()` / `With(...)`
  (`CLAUDE.md:142`). Orkestrasyon `TransitionPipeline.cs:20`, plan+döngü `TransitionExecutor.cs:219-269`,
  bağlam `TransitionContextFactory.cs`. `MaxChainDepth = 50` (`TransitionPipeline.cs:44`) auto-chain sonsuz
  döngü koruması.
- **Pipeline profilleri:** Her trigger türü bir profile çözülür ve ilgisiz adımları dışlar
  (`IPipelineProfileResolver.cs`, `PipelineProfileResolver.cs`). Profiller: Manual (dışlama yok), AutoChain,
  Scheduled, Event, ErrorBoundary (auto-chain + subflow kapalı) (`CLAUDE.md:144`).
- **Task executor ↔ invoker AYRIMI (kritik):** *Executor* Orchestration/Application içinde çalışır, workflow
  context'i bilir, binding kurar, yerel iş yapar veya remote invoker'ı çağırır (`Application/Tasks/Executors/`).
  *Invoker* Execution servisinde çalışır, tipli binding'den stateless dış çağrıyı yapar (`Execution/Invokers/`).
  Köprü: `RemoteInvokerService.cs:79` (Dapr) → `ExecutionController.cs:34` → `TaskInvokerRegistry.cs`
  (`docs/runtime/task-executors-and-invokers.md`). Yerel/uzak kararı `ExecutionMode` (`ExecutionMode.cs:13,19`).
- **CQRS-tarzı app servisleri:** `IInstanceCommandAppService` / `IInstanceQueryAppService` / `IInstanceRetryAppService`
  ayrı (`WorkflowApplicationModuleServiceCollectionExtensions.cs:70-73`). Remote varyantlar `Instances/Remote/`.
- **Domain event çift-işleme:** her event için senkron `IEventPublishHook<T>` (aynı UoW,
  `Infrastructure/*/Events/`) + asenkron `IEventHandler<T>` (`Workers.Inbox/Handlers/`) — `CLAUDE.md:106-110`.
- **Result pattern:** `BBT.Aether.Results.Result` her yerde; exception yerine `Result<T>` (`ITransitionStep.cs:1,30`).
- **Immutable, versiyonlu instance data:** SemVer; task sonucu→Patch, şema eklentisi→Minor, kırıcı→Major.
  Full-merge model, `LatestData` güncel + `DataList` geçmiş (`CLAUDE.md:189-196`). GraphQL-tarzı filtre sorgusu.
- **EF Include stratejisi:** pipeline adımları doğrudan `Include` çağırmaz; yükleme anında `WithDetailsAsync()`
  ile uygulanır (`CLAUDE.md:213-218`).

## 3. DI kayıtlarının yeri (interface→impl çözümü)
Kayıtlar her projenin `Microsoft/Extensions/DependencyInjection/*ServiceCollectionExtensions.cs`'inde;
kompozisyon kökü host `Program.cs`'lerinde modül metotlarını zincirler.

| Modül metodu | Dosya | Ne kaydeder |
|---|---|---|
| `AddDomainModule(...)` | `Domain/.../WorkflowDomainModuleServiceCollectionExtensions.cs:20` | Domain servisleri, scripting fabrikaları |
| `AddApplicationModule()` | `Application/.../WorkflowApplicationModuleServiceCollectionExtensions.cs:33` | Pipeline + app servisleri + task handler'ları + cache + validator |
| ↳ `AddApplicationServices()` | aynı dosya `:69-84` | `IDefinitionAppService`, `IInstanceCommandAppService`, `IInstanceQueryAppService`, `IFunctionAppService`, `IComponentDiscoveryAppService`, `ITransitionAuthorizationManager`, subflow servisleri … |
| ↳ task DI | `Application/.../TaskServiceCollectionExtensions.cs` | `IRemoteInvokerService`→`RemoteInvokerService` (`:78`), `ITaskExecutorRegistry` (`:81`), `ITaskCoordinator`→`TaskCoordinator` (`:166-169`), `ITaskFactory` konfig-seçimli singleton (`:188-201`), `IRetryPolicyFactory`→`PollyRetryPolicyFactory` (`:151`) |
| ↳ pipeline DI | `Application/.../PipelineServiceCollectionExtensions.cs` | `ITransitionStep` adımları + pipeline servisleri |
| `AddInfrastructureModule(...)` | `Infrastructure/.../WorkflowInfrastructureModuleServiceCollectionExtensions.cs:39,56` | DbContext, repository, Dapr, discovery, event hook, gateway |
| `AddExecutionServices(...)` | `Execution/.../ExecutionServiceCollectionExtensions.cs:43` | `ITaskInvokerRegistry`→`TaskInvokerRegistry` (singleton) + tüm `ITaskInvoker`'lar (`:49-62`) |

- **Invoker keşfi tip-güdümlü:** `TaskInvokerRegistry` ctor'da `IEnumerable<ITaskInvoker>`'ı `i.TaskType`
  anahtarıyla case-insensitive dictionary'ye çevirir (`TaskInvokerRegistry.cs:18-24`). Yeni task türü için:
  `ITaskInvoker`'ı `AddSingleton` ile ekle → registry otomatik alır.
- **Bir tipin impl'ini arıyorsan:** önce ilgili modül extension dosyasına bak; bulunamazsa Aether SDK'da
  kayıtlı olabilir (`AddAetherApplication()` vb.) → DURDU: Aether SDK.

## 4. Adlandırma desenleri
- **Namespace ≠ klasör (KRİTİK):** kök `BBT.Workflow.*` proje sınırlarını aşar. Örn. `LifecycleOrder.cs`,
  `ITransitionStep.cs`, `TransitionPipeline.cs` hepsi `namespace BBT.Workflow.Execution.Pipeline` kullanır ama
  `LifecycleOrder`/`ITransitionStep` **Domain** projesinde, `TransitionPipeline` **Application** projesinde.
  Dosyayı proje adından değil, gerçek yoldan bul (`grep`/`glob`).
- **DI extension'ları:** her proje `Microsoft/Extensions/DependencyInjection/<Alan>ServiceCollectionExtensions.cs`
  (SDK konvansiyonu; `Microsoft.*` alt-namespace altında olsalar da BBT kodu).
- **Task türleri iki yerde:** numaralı enum `TaskType` (`TaskEnums.cs:9`, 1=DaprHttpEndpoint … 17=StateStore) ve
  string sabit `TaskTypes` (`TaskTypes.cs:7`, `"http"`, `"daprservice"` …). Executor↔invoker eşlemesi string üzerinden.
- **Pipeline adımı:** `*Step.cs` (`ITransitionStep`), `public int Order => LifecycleOrder.<Ad>;` (örn.
  `ChangeStateStep.cs:21`, `HandleFinishStep.cs:23`).
- **App servisi:** `I<Alan>AppService` / `<Alan>AppService`; command/query ayrı.
- **Function handler:** `<Ad>FunctionHandler` (`orchestration/.../Controllers/Functions/Handlers/`, örn.
  `StateFunctionHandler`, `ViewFunctionHandler`, `DataFunctionHandler`, `AuthorizeFunctionHandler`).
- **HttpClient adı:** `WorkflowHttpClient` / `WorkflowHttpClient.NoSslValidation` (`WorkflowHttpClientNames.cs`).
- **Migration:** `Infrastructure/Migrations/` (WorkflowDbContext) + `Migrations/MessagingDb/` (MessagingDbContext).

## 5. Kritik dosyalar (top-10)
1. `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/TransitionPipeline.cs:20` — transition orkestrasyonu (lock, auto-chain, post-commit).
2. `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/TransitionExecutor.cs:219` — adım planı + yürütme döngüsü.
3. `src/BBT.Workflow.Domain/Execution/Transitions/Pipeline/LifecycleOrder.cs` — adım sıra sözleşmesi (motorun kalbi).
4. `orchestration/.../Controllers/Instances/InstanceController.cs:22` — instance/transition giriş uçları.
5. `orchestration/.../Controllers/Functions/FunctionController.cs:16` — state/view/data/authorize function uçları.
6. `execution/.../Controllers/Executions/ExecutionController.cs:16` — stateless task invoke ucu (iç servis).
7. `src/BBT.Workflow.Application/Tasks/Executors/Remote/RemoteInvokerService.cs:79` — Orchestration→Execution Dapr köprüsü.
8. `src/BBT.Workflow.Execution/Services/TaskInvokerRegistry.cs:11` — task türü → invoker yönlendirmesi.
9. `src/BBT.Workflow.Infrastructure/Data/WorkflowDbContext.cs:28` — çok-şemalı EF Core kalıcılık.
10. `src/BBT.Workflow.Domain/Definitions/Workflow.cs:16` + `Instances/Instance.cs:13` — workflow **tanım** modeli ve çalışan **instance** aggregate'i.

## 6. Test / build
- **Testler VAR:** ~221 test `.cs` dosyası; projeler `Domain.Tests`, `Application.Tests`, `Infrastructure.Tests`,
  `Monitor.Application.Tests` ve ortak `TestBase` (`test/`). `dotnet test` ile koşar (`CLAUDE.md:53-57`).
- **CI:** `.github/workflows/build-and-publish-images.yml` (imaj yayını), `.github/workflows/check-sonar.yml`
  (SonarQube). DeepSource (`.deepsource.toml`).
- **Build:** .NET 10 SDK (`global.json`). İlk kurulumda `./scripts/setup-netstandard-ref.sh` (PostSharp/.NET 10
  uyumu — `README.md:16-28`). Yerel çalıştırma Docker+Dapr (`etc/docker/run-docker.sh`). Portlar 4201/4202.
- **Sürüm:** ürün `common.props:Version 0.0.26`; runtime↔schema eşlemesi `vnext-meta/version-manifest.json`
  (release notları burgan-tech.github.io/vnext-docs'a bağlar).
