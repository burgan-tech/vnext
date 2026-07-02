# vnext (BBT.Workflow) — Domain Sözlüğü

Yalnızca bu motor kodunu okurken gerçekten gereken terimler. Her madde kanıtlı.

## Çekirdek kavramlar
- **vNext:** Amorphie low-code workflow platformu. Bu repo onun **server-side motorudur** (`CLAUDE.md:63`);
  istemci SDK'ları ve tanım repoları buna bağlanır.
- **Workflow (flow / definition):** bir sürecin **tanımı** — state, transition, task, function, view, schema
  içerir (`Definitions/Workflow.cs:16`). Kod içinde `Flow` alanı flow adıdır; her flow kendi PostgreSQL şeması.
- **Instance:** çalışan bir workflow örneği; aggregate root (`Instances/Instance.cs:13`, `AggregateRoot<Guid>`).
  `Flow`, `FlowVersion`, `Key`, `Status`, `DataList` taşır.
- **State:** workflow durum makinesindeki durum. **State Type** (`CLAUDE.md:152`): `Initial=1`, `Intermediate=2`,
  `Finish=3`, `SubFlow=4`, `Wizard=5`. **State Sub Type** (`:154`): None/Success/Error/Terminated/Suspended/
  Busy/Human/Cancelled/Timeout (0-8).
- **Transition:** bir state'ten diğerine geçiş; `PATCH …/transitions/{transitionKey}` ile tetiklenir
  (`InstanceController.cs:322`). **Trigger Type** (`CLAUDE.md:156`): `Manual=0`, `Automatic=1`, `Scheduled=2`,
  `Event=3`.
- **Shared transition:** workflow genelinde paylaşılan transition (`Workflow.cs` `sharedTransitions`); ayrıca
  yerleşik `cancel` / `updateData` / `exit` reserved transition'ları vardır.
- **Instance Status:** `Busy(B)` pipeline çalışıyor, `Active(A)` bekliyor, `Passive(P)` devre-dışı,
  `Completed(C)` bitti, `Faulted(F)` terminal hata (`InstanceStatus.cs:6`, `CLAUDE.md:150`).

## Task (görev)
- **Task:** transition/state yaşam döngüsünde çalışan iş birimi. **TaskType** numaralı enum
  (`Definitions/Tasks/TaskEnums.cs:9`): DaprHttpEndpoint=1, DaprBinding=2, DaprService=3, DaprPubSub=4, Human=5,
  Http=6, Script=7, Condition=8, Timer=9, Notification=10, StartTrigger=11, DirectTrigger=12, GetInstanceData=13,
  SubProcess=14, GetInstances=15, Soap=16, StateStore=17.
- **TaskTypes (string sabit):** invoker yönlendirmesi için `TaskType` enum'unun küçük-harf karşılıkları
  (`Execution.Abstractions/TaskTypes.cs:7`, örn. `"http"`, `"daprservice"`, `"statestore"`).
- **TaskTrigger:** task'ın ne zaman çalışacağı (`TaskEnums.cs:33`): `OnEntry=1`, `OnExit=2`, `Both=3`,
  `Manual=4`, `OnExecute=5`, `Extension=6`.
- **Task executor:** Orchestration'da çalışan, workflow context'i bilen ve binding kuran bileşen
  (`Application/Tasks/Executors/`). **Task invoker:** Execution'da çalışan, tipli binding'den stateless dış
  çağrıyı yapan bileşen (`Execution/Invokers/`). İkisi FARKLIDIR (bkz. bilinen-tuzaklar).
- **TaskEnvelope:** Orchestration↔Execution arası kararlı task istek kontratı
  (`Execution.Abstractions/TaskEnvelope.cs`).
- **ExecutionMode:** task'ın nerede çalışacağı — `Local` (Orchestration içi), `Remote` (Execution app'i),
  `Custom` (`Tasks.Abstractions/Core/ExecutionMode.cs:7-25`).

## Pipeline
- **Transition pipeline:** bir geçişi yürüten deterministik, sıralı adım zinciri (`TransitionPipeline.cs:20`).
- **ITransitionStep:** tek pipeline adımı; `int Order` + `Task<Result<StepOutcome>> ExecuteAsync(...)`
  (`ITransitionStep.cs:10`).
- **LifecycleOrder:** adım sıra sabitleri (`Domain/Execution/Transitions/Pipeline/LifecycleOrder.cs`; Preflight=5
  … ResolveAvailable=112). Adımlar artan `Order` ile çalışır.
- **StepOutcome:** adım sonucu — `Continue()`, `Stop()`, `SkipTo(order)`, `SkipToFinalize()`, `With(...)`
  (`CLAUDE.md:142`).
- **PipelineExecutionProfile:** trigger türüne göre ilgisiz adımları dışlayan profil (`IPipelineProfileResolver`);
  Manual/AutoChain/Scheduled/Event/ErrorBoundary (`CLAUDE.md:144`).
- **TransitionExecutionContext:** tüm adımlar boyunca akan tek bağlam nesnesi; `Cache` (Finalize'da temizlenir),
  `Directives` (biriken mutasyonlar) taşır (`CLAUDE.md:146`).
- **Auto-chain:** bir geçişin ardından otomatik transition zinciri; `MaxChainDepth=50` koruması
  (`TransitionPipeline.cs:44`).

## Kalıcılık ve kiracılık
- **Multi-schema:** her flow kendi PostgreSQL şemasında; şema `ICurrentSchema` ile header/route/query'den çözülür
  (`CLAUDE.md:104`). `SchemaAwareModelCacheKeyFactory` şema başına model cache; `SET search_path` gönderilmez
  (PgBouncer-güvenli — `WorkflowDbContext.cs:17-24`).
- **WorkflowDbContext:** instance tarafı DbContext (`Instances`, `InstancesData`, `InstanceTransitions`,
  `InstanceTasks`, `InstanceCorrelations`, `InstanceJobs`, `InstanceActions` — `WorkflowDbContext.cs:48-78`).
- **MessagingDbContext:** Inbox/Outbox + BackgroundJobs store'u (`sys_queues` şeması).
- **Instance data:** immutable, SemVer versiyonlu, full-merge; `LatestData` güncel + `DataList` geçmiş; GraphQL-tarzı
  filtre operatörleri (`eq/ne/gt/like/in/…`, `and/or/not`, `groupBy`) (`CLAUDE.md:189-196`).

## Fonksiyonlar ve error boundary
- **Function:** instance/domain üzerinde salt-okunur veya yan-etkili sorgu ucu; türleri State, View, Data,
  Authorize, Schema, Extensions, Hierarchy, HumanTask (`orchestration/.../Controllers/Functions/Handlers/`).
- **State function:** long-poll; ETag ile `200`/`304` (`CLAUDE.md:164`).
- **Error boundary:** Task→State→Global seviye zinciri (`CompiledBoundaryChain`); aksiyonlar `Abort/Retry/
  Rollback/Ignore/Notify/Log` (`CLAUDE.md:199-203`). ErrorBoundary profili auto-chain+subflow'u kapatır.
- **Component:** workflow'u oluşturan yeniden-kullanılabilir tanım birimleri (Workflow, Task, View, Function,
  Schema, Extension, Mapping); keşif `ComponentDiscoveryController.cs:26`.

## Altyapı terimleri
- **Aether SDK:** `BBT.Aether.*` — motorun altyapı çatısı (UoW, Result, multi-schema, entity/audit tabanı,
  controller tabanı). Dış NuGet kaynağı `burgan-tech/aether` (`AetherPackageVersion 1.0.29`).
- **Dapr:** service invocation (Orchestration↔Execution), pub/sub (event bus), binding, state store, jobs
  (scheduled transition/timer). appId `ExecutionApi:AppId` (varsayılan `vnext-execution`, `RemoteInvokerService.cs:31`).
- **Inbox/Outbox:** transactional mesajlaşma deseni; `Workers.Inbox` tüketir, `Workers.Outbox` publish eder.
- **Script (Roslyn):** `modules/BBT.Workflow.Modules.Scripting` — sandbox'lı C# script motoru (`CSharpEvaluator`,
  `BannedApiAnalyzer`). `DynamicExpresso` hafif ifade değerlendirme için (koşul/routing).
