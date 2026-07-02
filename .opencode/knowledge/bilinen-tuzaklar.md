# vnext (BBT.Workflow) — Bilinen Tuzaklar

> Bu dosya en yüksek halüsinasyon riskli dosyadır. Her madde kanıtlı ya da `DOĞRULANMADI:` etiketlidir.
> "X gibi görünür ama Y'dir" durumları ve yanıltıcı adlandırmalar.

## Namespace ≠ klasör ≠ proje (en sık hata)
Kök namespace `BBT.Workflow.*` proje ve klasör sınırlarını AŞAR. Aynı `namespace BBT.Workflow.Execution.Pipeline`
üç ayrı yerde geçer: `LifecycleOrder.cs` ve `ITransitionStep.cs` **Domain** projesinde
(`src/BBT.Workflow.Domain/Execution/Transitions/Pipeline/…`), `TransitionPipeline.cs` ise **Application**
projesinde (`src/BBT.Workflow.Application/Execution/Transitions/Pipeline/…`). Bir tipi namespace'ine veya
"Execution" kelimesine bakarak `src/BBT.Workflow.Execution` projesinde arama — `grep`/`glob` ile gerçek yolu bul.

## "Execution" iki farklı şeydir
- **`BBT.Workflow.Execution` projesi** = Execution servisinin **task invoker**'ları (`src/BBT.Workflow.Execution/
  Invokers/`, stateless dış çağrı).
- **`BBT.Workflow.Execution.Pipeline` namespace'i / `Application/Execution/`** = **transition pipeline**
  (Orchestration/Application). İkisi ayrı sorumluluktur. "Execution" gördüğünde hangisi olduğunu klasörden doğrula.

## Task executor ≠ task invoker (ve iki `ITaskInvoker` vardır)
`Application/Tasks/Executors/…Executor.cs` (Orchestration, workflow context'i bilir) ile
`Execution/Invokers/…Invoker.cs` (Execution, stateless) FARKLI katmanlardır (`docs/runtime/
task-executors-and-invokers.md`). Dahası **iki ayrı `ITaskInvoker` interface'i** vardır:
`src/BBT.Workflow.Tasks.Abstractions/Core/ITaskInvoker.cs` ve `src/BBT.Workflow.Execution/Invokers/ITaskInvoker.cs`.
Benzer şekilde **iki `TaskEnvelope.cs`** dosyası var (`Tasks.Abstractions/Core/TaskEnvelope.cs` ve
`Execution.Abstractions/TaskEnvelope.cs`). Grep'te ilk eşleşmeyi doğru sanma; envelope/invoker referansının
hangi projeden geldiğini using'lerden teyit et.

## Task türü iki yerde tanımlı — senkron tutulmalı
Numaralı `enum TaskType` (`Definitions/Tasks/TaskEnums.cs:9`, 1-17) ve string sabit `TaskTypes`
(`Execution.Abstractions/TaskTypes.cs:7`) AYRI tanımlardır; invoker yönlendirmesi string üzerinden yapılır
ve "TaskType enum değerinin küçük-harfi" olmalıdır (`TaskTypes.cs:5-6` notu). Bir task türü eklerken ikisini de
güncellemek gerekir — yalnız enum'a bakıp string'i unutma.

## `WorkflowDbContext.BackgroundJobs` DEPRECATED
`WorkflowDbContext.cs:88`'deki `BackgroundJobs` DbSet'i ve `IHasEfCoreBackgroundJobs` yalnızca geriye dönük uyum
içindir; birincil store `MessagingDbContext` (`sys_queues.BackgroundJobs`) — `WorkflowDbContext.cs:20-24` notu.
Background job'ları `WorkflowDbContext` üzerinden izleme; MessagingDbContext'e bak.

## CLAUDE.md pipeline tablosu tam liste DEĞİL — kod otoritedir
`CLAUDE.md:120-141`'deki adım tablosu `HandleLongPollTerminationStep` (order 75) ve `AfterEpilogueRefresh`
(order 111) adımlarını LİSTELEMEZ; oysa kodda vardır (`LifecycleOrder.cs:92,121`, `HandleLongPollTerminationStep.cs:37`).
Adım sırası/kapsamı için tek kaynak `LifecycleOrder.cs` ve `Steps/` klasörüdür, dokümantasyon değil.

## Orchestration host, Application/Infrastructure'ı DOĞRUDAN referanslamaz
`Orchestration.HttpApi.Host.csproj` yalnızca `HttpApi.Shared`'a referans verir; Application/Infrastructure
**transitif** gelir (`HttpApi.Shared → Infrastructure → Application`). Host'un `.csproj`'unda app servisi arama;
DI kayıtları modül extension'larındadır (`WorkflowApplicationModuleServiceCollectionExtensions.cs:33` vb.).

## Infrastructure → Application referansı ters DEĞİL (Aether modül deseni)
`Infrastructure.csproj` `Application`'a referans verir (`Infrastructure → Application`). Bu Clean Architecture'ı
ihlal gibi görünür ama Aether SDK modül desenidir: interface'ler Domain/Application'da, implementasyonlar
Infrastructure'da; modül `AddInfrastructureModule` ile bağlanır (`WorkflowInfrastructureModuleServiceCollectionExtensions.cs:39`).

## Stored procedure yok ama raw SQL VAR
Kalıcılık EF Core'dur; **SP yoktur**. Ancak performans için PostgreSQL native JSONB filtrelerinde parametreli
`FromSqlRaw` kullanılır (`Domain/QueryExtensions/PostgreSqlJsonFilterService.cs:164`,
`Infrastructure/Instances/EfCoreInstanceRepository.cs:734,853,1257`). "Hiç ham SQL yok" varsayma; ama SP arama.

## Multi-schema izolasyonu `SET search_path` ile DEĞİL
Şema izolasyonu tablo eşlemesine şema adının doğrudan enjekte edilmesiyle + şema-başına compiled model cache ile
yapılır; `SET search_path` HİÇBİR ZAMAN gönderilmez (PgBouncer transaction-mode güvenli — `WorkflowDbContext.cs:17-24`).
"Şema nasıl seçiliyor?" cevabı `ICurrentSchema` + `SchemaAwareModelCacheKeyFactory`, search_path değil.

## `vnext-meta/`, `init/`, `tools/` motor kodu DEĞİL
- `vnext-meta/` = Node.js meta paketi (sürüm/şema/feature/deprecation JSON'ları); `component-registry.json`
  şu an **boş iskele** (`{}`). Motor davranışını buradan çıkarma.
- `init/VNext.Init.Host/` = Node.js bootstrap/publish host'u (`.js` dosyaları), C# değil.
- `tools/BBT.Workflow.Mcp.Server` = geliştirici MCP aracı; runtime yürütme yolu değil.
- `docs/monitoring/claude_yonlendirmleri/.claude/…` = monitoring alt-projesinin AI yönergeleri; ürün kodu değil.

## Monitoring host salt-okunurdur
`monitoring/…Monitor.HttpApi.Host` içindeki 37 endpoint'in tamamı `[HttpGet]`'tir (stats/instance/config sorgu).
Yazma/transition tetikleme Orchestration'dadır; monitoring'i mutasyon kaynağı sanma.

## Uzaktaki `aiagent` branch'i eskidir (operasyonel not)
Bu paket `master`'dan taze dallanan `aiagent`'te üretildi; origin'deki eski `aiagent` master'ın gerisindeydi
ve `.opencode` içermiyordu. Analiz için değil, yalnızca branch geçmişini yorumlarken dikkat.

## Base adres / appId / hedef URL kodda sabit değildir
`ExecutionApi:AppId` gibi değerler `configuration[...]` ile okunur, kodda görülen `"vnext-execution"` yalnızca
**fallback varsayılandır** (`RemoteInvokerService.cs:31`). Task'ların gerçek HTTP/SOAP hedefleri workflow
**tanımındaki** binding'lerden gelir (bu repoda değil). "Bu çağrı nereye gidiyor?" → DURDU: konfig/tanım-güdümlü.
