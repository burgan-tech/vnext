# vnext (BBT.Workflow) — Kritik Akışlar

Bu dosya motorun en sık sorulan uçtan-uca akışlarını, giriş noktasından dış uca kadar `dosya:satır`
kanıtıyla verir. Her akış kendi başına bağımsızdır.

---

## 1. Workflow instance başlatma (tanım → örnekleme)
1. **Giriş:** `POST api/v1/{domain}/workflows/{workflow}/instances/start` (`InstanceController.cs:45`).
   `{domain}` şema/flow ayrıştırıcısı; `?sync`, `?version`, `?extensions` query'leri okunur (`:53-57`).
2. **Payload modu:** `PayloadModeDetector.IsStandard` ile gövde ya `CreateInstanceDto` ya doğrudan
   `Attributes` olarak yorumlanır (`InstanceController.cs:65-73`); header ve route değerleri input'a eklenir (`:87-90`).
3. **Uygulama servisi:** `commandAppService.StartAsync(input)` (`InstanceController.cs:92`,
   `IInstanceCommandAppService` — `WorkflowApplicationModuleServiceCollectionExtensions.cs:70`). Servis workflow
   **tanımını** component cache'ten çözer, yeni `Instance` aggregate'i yaratır (`Instance.Create`,
   `Instance.cs:45`), başlangıç durumu `InstanceStatus.Active` (`Instance.cs:32`).
4. **Başlangıç geçişi:** initial state'e giriş transition pipeline'ından geçer (§2).
5. **Yanıt:** `StartInstanceOutput` (`InstanceController.cs:46`); `sync=true` ise tam instance verisi, aksi halde
   `{ id, status }` döner (`CLAUDE.md:158-161`).
- **DIŞ UÇ:** workflow tanımının kendisi (state/transition/task listesi) dağıtılan domain tanım reposundadır.

## 2. Transition yürütme (deterministik pipeline)
1. **Giriş:** `PATCH …/instances/{instance}/transitions/{transitionKey}` (`InstanceController.cs:322`) veya
   async için `POST …/transitions/{transitionKey}/enqueue` (`:275`).
2. **Pipeline:** `TransitionPipeline` (`TransitionPipeline.cs:20`) tek request-scope lock alır (auto-chain
   boyunca boşluk yok), bağlamı `TransitionContextFactory` ile kurar, profil çözer (`IPipelineProfileResolver`),
   sonra `TransitionExecutor` adım planını sırayla yürütür (`TransitionExecutor.cs:219-269`).
3. **Adım sırası** (`LifecycleOrder.cs`): Preflight(5) → CheckParentUpdateData(9) → ForwardToActiveSubflow(10) →
   SetBusy(19) → CreateTransition(20) → ResourceLock(25) → **OnExecute tasks(30)** → ApplyTimeoutState(38) →
   CancelScheduledJobs(39) → **OnExit tasks(40)** → **ChangeState(50)** → **OnEntry tasks(60)** → SubFlow(70) →
   LongPollTermination(75) → ClearBusyOnResume(79) → Schedule(80) → **Auto transitions(90)** → Finish(100) →
   Finalize(110) → ResolveAvailable(112). Her adım `Result<StepOutcome>` döner (`ITransitionStep.cs:30`).
4. **Adım kontrol akışı:** `StepOutcome.SkipTo(order)` plan içinde atlar+yeniden planlar; örn.
   `HandleSubFlowStep` bittiğinde `SkipToOrder = LifecycleOrder.Finalize` (`HandleSubFlowStep.cs:98`),
   `HandleCancelPreflightStep` iptalde `CreateTransition`'a atlar (`HandleCancelPreflightStep.cs:107`).
5. **Task adımları:** `RunOnExecuteTasksStep`/`RunOnExitTasksStep`/`RunOnEntryTasksStep` (`:38,39,38`) →
   `ITaskCoordinator`/`TaskExecutionEngine` → task executor'ları (§3).
6. **Sonlandırma:** `HandleFinishStep` (`:23`) Finish state'te instance'ı Completed/Cancelled yapar;
   `ResolveAvailableStep` (`:25`) hedef state yalnız manual/event transition içeriyorsa Active'e çeker.
- **DIŞ UÇ:** transition/task tanımı domain tanım reposunda; task hedefleri harici servisler (§3).

## 3. Task yürütme (executor → remote invoker → dış çağrı)
1. Pipeline task adımı `ITaskExecutorRegistry`'den ilgili executor'ı ister
   (`docs/runtime/task-executors-and-invokers.md`; kayıt `TaskServiceCollectionExtensions.cs:81`).
2. **Executor** (Orchestration/Application, `Application/Tasks/Executors/<aile>/`) workflow context'ini
   değerlendirir, tipli binding kurar. `ExecutionMode.Local` ise yerinde yürütür (script/trigger/subprocess),
   `Remote` ise remote invoker'a devreder (`ExecutionMode.cs:13,19`).
3. **Remote köprü:** `RemoteInvokerService.InvokeAsync` (`RemoteInvokerService.cs:38`) `TaskEnvelope`'u Dapr
   ile Execution app'ine gönderir: `CreateInvokeMethodRequest(appId, "/api/v1/execution/invoke/{taskType}/{taskKey}")`
   (`:63-66`), `appId` = `ExecutionApi:AppId` (varsayılan `vnext-execution`, `:31`), per-invocation timeout `:58-59`.
   `WorkflowInfo` header'ı domain/workflow/version/instanceId taşır (`:68-72`).
4. **Execution tarafı:** `ExecutionController.InvokeTaskAsync` (`ExecutionController.cs:34`) envelope'u
   `invokerRegistry.InvokeAsync` ile yönlendirir (`:56`). `TaskInvokerRegistry` `TaskType` → invoker eşler
   (`TaskInvokerRegistry.cs:18-24,44`).
5. **Invoker** (`src/BBT.Workflow.Execution/Invokers/`) stateless dış çağrıyı yapar: `HttpTaskInvoker`,
   `SoapTaskInvoker`, `DaprServiceTaskInvoker`, `DaprBindingTaskInvoker`, `DaprHttpEndpointTaskInvoker`,
   `DaprPubSubTaskInvoker`, `StateStoreTaskInvoker`; trigger invoker'ları (`StartTrigger/DirectTrigger/
   SubProcess/GetInstanceData/GetInstances`) çapraz-domain. HTTP named client `WorkflowHttpClient[.NoSslValidation]`.
6. Sonuç `TaskInvocationResult` olarak executor'a döner, pipeline data/error-handling'e maplenir.
- **DIŞ UÇ:** gerçek HTTP/SOAP/Dapr hedefleri task binding'inden (tanımdan); Execution invoker jeneriktir.

## 4. Function çağrısı (state / view / data / authorize)
1. **Giriş:** domain-scope `GET/POST {domain}/functions/{function}` (`FunctionController.cs:33,91`) veya
   instance-scope `…/instances/{instance}/functions/{function}` (`:54,116`).
2. **Yönlendirme:** `IFunctionAppService` (`WorkflowApplicationModuleServiceCollectionExtensions.cs:74`) ilgili
   handler'a çözer: `StateFunctionHandler`, `ViewFunctionHandler`, `DataFunctionHandler`, `AuthorizeFunctionHandler`,
   `SchemaFunctionHandler`, `ExtensionsFunctionHandler`, `HierarchyFunctionHandler`, `HumanTaskFunctionHandler`
   (`orchestration/.../Controllers/Functions/Handlers/`; fabrikalar `DomainFunctionHandlerFactory`,
   `InstanceFunctionHandlerFactory`).
3. **State function (long-poll):** koşullu GET + ETag; `200` (değişti) / `304` (değişmedi → bekle→tekrar).
   ETag kaynağı `LatestData?.ETag` veya `IRepresentationEtagService.Generate` (`CLAUDE.md:164-168`). Rol
   filtresi `ITransitionAuthorizationManager` (`$InstanceStarter`, `$PreviousUser` pseudo-rolleri).
4. **View function:** `views[]` bildirim sırasında değerlendirilir, ilk eşleşen kural kazanır; kural inline
   C# script `IConditionMapping` (`CLAUDE.md:182-186`). `loadData:true` ise veri de yüklenir.
- **DIŞ UÇ:** view/schema tanımı domain reposunda; long-poll döngüsünü istemci SDK yürütür.

## 5. Subflow yaşam döngüsü
1. **Başlatma:** `HandleSubFlowStep` (`HandleSubFlowStep.cs:24`) SubFlow state tipinde korelasyon başlatır ve
   `StartSubflowJob`'u post-commit kuyruğa alır (`Execution/PostCommit/Handlers/StartSubflowJobHandler.cs`);
   başlatma `StrictIdempotency:true`, parent metadata `ExtraProperties`'te (`CLAUDE.md:209`).
2. **SubFlow (S) tamamlanması:** output mapping → `ResumePipelineAsync`, `ResumeFrom =
   LifecycleOrder.ClearBusyOnResumeStep` (order 79 — `SubflowCompletionService.cs:237`); parent pipeline devam eder.
3. **SubProcess (P):** korelasyon tamamla + persist, parent resume YOK (fire-and-forget) (`CLAUDE.md:208`).
4. **Fault:** `SubflowFaultService` resume'ı `ClearBusyOnResumeStep`'ten sürdürür (`SubflowFaultService.cs:302`);
   resume başarısızsa korelasyon yeni UoW'da geri alınır.
5. **Tamamlanma penceresi:** subflow terminal ama parent korelasyonu açıksa, State function subflow terminal
   görünümü yerine **parent** ana-flow transition'larını gösterir (`CLAUDE.md:169,211`).

## 6. Domain event çift-işleme (senkron hook + asenkron handler)
1. Bir domain event yayınlandığında **iki** işleyici çalışır (`CLAUDE.md:106-110`):
   - **Senkron hook** `IEventPublishHook<T>` — aynı UoW içinde, lokal (`Infrastructure/*/Events/`, örn.
     `Infrastructure/Instances/Events/`).
   - **Asenkron handler** `IEventHandler<T>` — dağıtık, hataya-dayanıklı (`workers/BBT.Workflow.Workers.Inbox/Handlers/`).
2. **Outbox:** olaylar transactional outbox ile yazılır, `Workers.Outbox` bunları event bus'a (Dapr pub/sub)
   publish eder; `MessagingDbContext` (`sys_queues`) Inbox/Outbox store'u.
3. Event tanımları `src/BBT.Workflow.Events.Contracts/` (CloudEvents; örn. `InstanceCompletedCleanupEvent`,
   `InstanceSubFaultedEvent`, `TransitionContinuationRequested`).
- **DIŞ UÇ:** event bus taşıması Dapr sidecar/broker'da; başka servisler bu event'leri tüketebilir.
