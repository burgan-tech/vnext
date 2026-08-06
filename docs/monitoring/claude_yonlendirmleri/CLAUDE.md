# CLAUDE.md — vNext Monitoring API Development Guide

This file provides guidance when working on the **monitoring feature** of the vNext workflow engine. The monitoring API mirrors the orchestration API's read endpoints in a separate, lightweight host so that the main runtime is not burdened with dashboard and observability traffic.

> **Repo Yapısı Notu:** Git reposu **`vnext`**'tir. `vnext-monitoring` dizini bir git reposu değildir; yalnızca monitoring ile ilgili endpoint örnekleri (`.http`, Postman koleksiyonları) ve geliştirme kurallarını (`docs/`, `.claude/`, `.cursor/`) barındırmak amacıyla ayrı tutulmuştur. Kaynak kod `vnext/` altındadır.

---

## 0. vNext Nedir?

**vNext**, .NET 10 üzerine inşa edilmiş, Clean Architecture ve Domain-Driven Design (DDD) prensiplerini takip eden bir **workflow otomasyon motorudur**. İş süreçlerini (workflow) tanım dosyaları (JSON) ve çalışma zamanı (runtime) olarak iki katmanda yönetir.

### Temel Kavramlar

- **Workflow (Definition)**: State'ler, transition'lar, task'lar ve policy'lerden oluşan iş akışı tanımı. JSON dosyaları olarak versiyonlanır ve publish edilir.
- **Instance**: Bir workflow tanımının çalışan bir örneği. `CurrentState`, `Status` (Active/Busy/Completed/Faulted/Passive), versiyonlanmış `Data` ve geçiş geçmişi (`InstanceTransition`, `InstanceTask`) tutar.
- **Transition**: Bir state'ten diğerine geçiş. Manual (kullanıcı tetikler), Automatic (rule ile otomatik), Scheduled (zamanlı) ve Event (olay tetikli) türleri vardır.
- **Task**: Transition sırasında çalışan iş birimleri. HTTP çağrısı (type 6), C# script (type 7), Dapr binding, human task gibi çeşitleri vardır.
- **State**: Workflow'un bulunabileceği durumlar. Initial (1), Intermediate (2), Final (3), SubFlow (4), Wizard (5) tipleri mevcuttur.

### Mimari Yapı

vNext, **iki ana API host** ve destekleyici worker'lardan oluşur:

| Host | Port | Rol |
|------|------|-----|
| **Orchestration API** | 4201 | Dış dünyaya açık: instance başlatma, transition tetikleme, sorgulama, tanım yönetimi |
| **Execution API** | 4202 | Dahili: task çalıştırma (HTTP, script, Dapr). Orchestration tarafından Dapr üzerinden çağrılır |
| **Monitor API** | 4203 | Salt-okunur: dashboard ve observability sorguları. Bu repo'nun ana odağı |
| **Workers** | — | Inbox (event tüketici), Outbox (event yayıncı), DbMigrator (şema göçleri) |

### Katmanlar

```
Presentation  →  Orchestration / Execution / Monitor API Host'ları (controller, middleware)
Application   →  İş servisleri, transition pipeline, execution stratejileri, DTO'lar
Domain        →  Instance aggregate, repository arayüzleri, value object'ler, domain event'ler
Infrastructure→  EF Core repo'ları, Dapr/HTTP gateway'leri, Redis cache, multi-schema desteği
```

### Transition Pipeline (Kısaca)

Bir transition isteği geldiğinde: **TransitionRunner** → yeni DI scope + UnitOfWork → **TransitionPipeline** sıralı adımları çalıştırır (preflight → busy kayıt → OnExecute → OnExit → state değiştir → OnEntry → subflow/scheduling/finalize). Her adım `StepOutcome` (Continue/Stop/SkipTo) döner; pipeline buna göre ilerler veya durur.

### Altyapı

- **PostgreSQL**: Multi-schema desteği (workflow başına ayrı şema, `ICurrentSchema` ile çözümlenir)
- **Redis**: Tanım ve component cache (`DomainCacheContext` → `CacheSet<T>` → distributed + local snapshot)
- **Dapr**: Servisler arası iletişim, pub/sub, secret store (Vault), state store
- **OpenTelemetry**: Trace, metric ve log toplama (OTLP exporter)

> **Bu repo'nun odağı**: Yukarıdaki altyapının **salt-okunur** bir aynası olan **Monitor API**'dir. Orchestration ve Execution'ın aynı veritabanı ve cache'ini okur; hiçbir state değişikliği yapmaz, event yayınlamaz veya task çalıştırmaz.

---

## Supplementary Files

Detailed patterns, endpoint maps, and domain references are in dedicated files:

| File | Content |
|------|---------|
| `.cursor/rules/monitoring-learned-lessons.md` | **alwaysApply:** Monitor’da öğrenilen hatalar (4 başlık şablonu), zorunlu doküman referansları (`caching-strategy.md` dahil) |
| `.cursor/rules/monitor-constraints.md` | Hard constraints (always applied): file scope (monitor + additive Domain/Infrastructure), version/publish, monitor read-only API principle, reuse order |
| `.cursor/rules/monitor-coding-patterns.md` | Code patterns: DI, controller, service, DTO, result, pagination, naming (applied on `vnext/monitoring/**/*.cs`) |
| `.cursor/rules/monitor-endpoint-map.md` | Full orchestration vs monitor endpoint status table (applied on controller files) |
| `.cursor/skills/add-monitor-endpoint/SKILL.md` | Step-by-step guide for adding a new monitor endpoint with checklist |
| `.cursor/skills/vnext-domain-reference/SKILL.md` | Domain entity properties, repository methods, cache store API, entity relationships |
| `.claude/skills/add-query-param-filter/SKILL.md` | 3-sınıf pattern (FilterInput/Descriptor/Filter), bracket-notation binding, type-discriminated 400 validasyon — monitor list endpoint'ine query param filter eklerken kullanılır |

---

## 0.1 Doküman Haritası — Nereye Bakmalı (ZORUNLU)

Monitoring üzerinde çalışmaya başlamadan önce **doğru kaynağa** bak. Her dizinin rolü farklıdır; soruna göre şu sırayı izle:

| Soru / İhtiyaç | Bakılacak yer | İçerik |
|----------------|---------------|--------|
| **Şu ana kadar ne yaptık? Hangi endpoint'ler var, nasıl çalışıyor?** | `docs/features/` (özellikle `monitoring-features.md`) | Kullanıcıya dönük, **tamamlanmış** yeteneklerin rehberi. Mevcut tüm endpoint'ler ve nasıl kullanılacakları. |
| **Bundan sonra ne ekleyeceğiz? Büyük resim, yol haritası?** | `docs/upcoming/` (`vnext-monitoring-upcoming-features.md`) | **Eklemeyi düşündüğümüz** öğelerin kapsamlı feature map'i. Endpoint isimlerinden çok genel resmi ve önceliği görmek için. |
| **Faz bazlı plan: bir fazda neyi, neden, hangi kontratla ekledik/ekleyeceğiz?** | `docs/superpowers/specs/` (ör. `2026-06-09-monitor-phase1-endpoints-design.md`) | Faz bazlı tasarım/spec. Her endpoint'in gerekçesi, kontratı, mimari kararları, riskleri. |
| **Faz uygulama adımları (task-by-task)?** | `docs/superpowers/plans/` | writing-plans ile üretilen, adım adım implementasyon planları. |
| **Endpoint'in orchestration karşılığı / durumu?** | `.claude/rules/monitor-endpoint-map.md` | Orchestration vs Monitor endpoint haritası ve durum tablosu. |
| **Geçmiş değişiklik notları?** | `docs/changes/` | Operasyonel/altyapısal değişiklik kayıtları. |
| **Doğruluğundan şüphe duyulan kararlar, güvenlik riski, onay bekleyen mimari seçimler?** | `docs/ask-correctness/` | Üstlere ya da ekibe sorulması gereken soru notları. Her dosya bir konuyu ele alır. |

**Kural:**
- **Yapılanları** görmek için → `docs/features/`.
- **İlerleyeceğimiz yolu / planlananları** görmek için → `docs/upcoming/` (büyük resim) ve `docs/superpowers/specs/` (faz bazlı detay plan).
- Yeni bir endpoint planlarken önce `docs/upcoming/` ile büyük resmi, sonra ilgili `specs/` faz tasarımını oku; yoksa brainstorming → spec → plan akışını izle.
- **Onayından emin olunmayan** bir karar, güvenlik riski veya mimari seçim söz konusuysa → `docs/ask-correctness/` altına kısa bir not bırak; uygulamaya geçmeden önce üstlerden onay al.

---

## 1. Hard Constraints

### 1.1 Monitor projeleri (birincil yazım alanı)

`vnext/monitoring/` altında **Application** ve **API (HttpApi.Host)** katmanları **ayrı projeler** olarak kalır; monitoring özellikleri burada geliştirilir:

- `vnext/monitoring/BBT.Workflow.Monitor.Application/`
- `vnext/monitoring/BBT.Workflow.Monitor.HttpApi.Host/`

### 1.2 vNext kod tabanı ile ilişki (Domain, Infrastructure, Application)

| Kod yolu | Rol | Değişiklik politikası |
|----------|-----|------------------------|
| `vnext/src/BBT.Workflow.Domain` | Ortak domain | **Eklenebilir** — aşağıdaki **ekleme kuralına** uyulmalı. |
| `vnext/src/BBT.Workflow.Infrastructure` | Ortak altyapı (EF, gateway, vb.) | **Eklenebilir** — aynı **ekleme kuralı**. |
| `vnext/src/BBT.Workflow.Application` ve `vnext/src/` içindeki **diğer** kütüphaneler | Tüketim / referans | **Salt okunur**: davranış veya imza **değiştirilmez**; ihtiyaç olduğunda **yeni kod** monitoring veya Domain/Infra tarafında **eklenerek** karşılanır. |

**Kritik kural (Domain ve Infrastructure):** Mevcut **action’lara**, **metotlara**, **fonksiyonlara** veya iş kurallarına **dokunulmaz**. İhtiyaç, **yalnızca yeni üyeler** (yeni metotlar, yeni sınıflar, yeni repository operasyonları vb.) ile karşılanır. Var olanı ihtiyaca göre **değiştirmek yerine** paralel **yeni** bir API eklenir. Öncelik **değişiklik değil, eklemedir**; mevcut tüketicilerin davranışı bozulmamalıdır.

**İstisna — Sadece Monitoring Tüketen Metodlar:** Domain veya Infrastructure’daki bir metot **yalnızca ve yalnızca** monitoring tarafından çağrılıyorsa (codebase genelinde başka hiçbir tüketici yoksa), o metot **doğrudan değiştirilebilir** — additive zorunluluğu kalkar. Bu istisnayı uygulamadan önce:

1. **Tüketici doğrulaması zorunludur:** `grep -rn "MetotAdi" vnext/ --include="*.cs"` ile tüm codebase taranır. Yalnızca `vnext/monitoring/` altındaki dosyalar ve interface/implementation tanımları çıkıyorsa bu istisna uygulanır.
2. **Test dosyaları sayılmaz:** Mock kullanan test dosyaları tüketici sayılmaz; sadece production kodu tüketici olarak değerlendirilir.
3. **Şüphe durumunda additive yol izlenir:** Tüketici analizinden emin olunamazsa, güvenli taraf olarak her zaman yeni metot ekleme yolu seçilir.

### 1.2.1 Optimizasyon Kapsamı — Kesin Kural

**Optimizasyonlar (AsNoTracking, projeksiyon, slim metodlar, Include kısıtlama) yalnızca monitoring tarafındaki kodlara uygulanır.**

Domain ve Infrastructure'daki bir metot monitoring dışında herhangi bir consumer'a sahipse — o metot **ne kadar verimsiz olursa olsun dokunulmaz.** Optimizasyon ihtiyacı monitoring tarafında **yeni bir metot** eklenerek karşılanır; mevcut metodun davranışı veya imzası asla değiştirilmez.

| Senaryo | Yapılacak |
|---------|-----------|
| Metot **sadece** monitoring tüketiyor | Direkt değiştirilebilir (CLAUDE.md §1.2 İstisna kuralı) |
| Metot monitoring + başka consumer tüketiyor | Monitoring için yeni slim/projeksiyon metot eklenir; mevcut metot aynen bırakılır |
| Optimizasyon fırsatı var ama consumer belirsiz | Additive yol — yeni metot ekle, mevcut dokunma |

**Yaygın hata:** `InstanceQueryAppService`, `InstanceRetryAppService`, `ScriptContextBuilder`, `RuntimeService` gibi orchestration/domain katmanındaki consumer'lar da kullanan bir metodu "monitoring için optimize etmek" amacıyla değiştirmek. Bu **yasaktır** — o consumer'ların davranışı bizim sorumluluğumuzda değildir ve kırılması kabul edilemez.

### 1.3 Geliştirme önceliği (tekrar kullanım sırası)

Yeni ihtiyaç doğduğunda sırayla:

1. **Aether SDK** — karşılayan bir yapı var mı kontrol et.
2. **`vnext/src` (vNext)** — domain, infrastructure, application ve paylaşılan paketlerde uygun mevcut fonksiyon var mı bak; **mümkünse ondan devam et**.
3. Hiçbiri yetmezse, **vNext reposunun mimarisine ve kod stiline** uyumlu şekilde **yeni** kod yaz (monitor veya Domain/Infra’da **ekleme** modeliyle).

### 1.4 Repo kısıtları (genel)

Önceki workspace kurallarıyla çakışma olmaması için: **versiyon atlama**, **publish** ve kullanıcı onayı olmadan **yeni NuGet** ekleme yapılmaz.

İzleme (monitoring) için dosya kapsamı ve diğer sert kısıtlar: `.cursor/rules/monitor-constraints.md`

> **Özet:** Monitoring **Application** + **API** ayrıdır; **Domain** ve **Infrastructure** olarak `BBT.Workflow.Domain` ve `BBT.Workflow.Infrastructure` kullanılır ve bunlarda geliştirme **yalnızca ekleme** ile yapılır. `BBT.Workflow.Application` ve vNext’in geri kalanı **tüketilir**, **değiştirilmez**.

### 1.5 Saf DDD Mimarisine Kesin Uyum (zorunlu)

vNext kod tabanı **saf Domain-Driven Design** prensiplerine son derece özenli ve tutarlı biçimde uygular. Bu bir tercih değil, mimarinin temel tasarım kararıdır; monitoring dahil her yeni kod bu yapıya **tam uyumlu** olmak zorundadır.

#### Neden önemli?

vNext’te katmanlar arasındaki sınırlar kasıtlı olarak sıkı tutulmuştur:

- **Domain katmanı** yalnızca iş kurallarını, aggregate root’ları, repository *interface*’lerini ve value object’leri barındırır. Altyapıya bağımlılığı sıfırdır.
- **Application katmanı** kullanım senaryolarını (use case) orkestre eder; domain nesnesini *nasıl* kalıcı kılacağını bilmez.
- **Infrastructure katmanı** yalnızca domain interface’lerini uygular; iş kuralı içermez.
- **Presentation (Controller) katmanı** yalnızca HTTP dönüşümlerini yapar; servis çağrısının ötesinde hiçbir iş mantığı içermez.

#### Kesin kurallar

| Kural | Açıklama |
|-------|----------|
| **Domain’e altyapı sızmaz** | Domain entity veya service’lerinde `DbContext`, EF Core, Redis, Dapr, `HttpClient` gibi altyapı bileşenlerine doğrudan referans **yasaktır**. |
| **İş kuralı yalnızca Domain’de** | Bir koşul veya hesaplama birden fazla yerde tekrarlanıyorsa doğru yer Domain’dir; Application veya Controller’a iş kuralı yazılmaz. |
| **Repository arayüzleri Domain’de, implementasyonu Infrastructure’da** | Yeni bir veri erişim ihtiyacı doğduğunda önce `IXRepository`’ye metot eklenir, ardından `XRepository` (EF Core) implemente eder. |
| **Application servisleri domain nesnesini döndürmez** | Controller’a doğrudan `Instance`, `InstanceTransition` gibi entity’ler dönmez; her zaman DTO (Response) nesneleri döner. |
| **Controller’da iş mantığı olmaz** | Controller sadece `FromRoute`/`FromQuery` ile input toplar, servisi çağırır, `FromResult()` ile döner. Koşul, döngü veya mapping içermez. |
| **Aggregate dışından state değiştirilmez** | Bir aggregate’in durumunu değiştirmek yalnızca kendi metodları (domain behavior) aracılığıyla yapılır; dışarıdan property set edilmez. |
| **Yeni kavram = yeni Domain sınıfı** | Bir varlık veya değer nesnesi birden fazla yerde kullanılıyorsa anonymous type veya tuple değil, isimlendirilmiş bir Value Object veya DTO yazılır. |

#### Pratik kontrol soruları (kod yazmadan önce)

1. Bu kodun *neden* bu katmanda olduğunu açıklayabilir miyim?
2. Domain nesnem altyapı import’u içeriyor mu? → İçeriyorsa katman sınırı ihlali.
3. Controller’da `if` veya `foreach` var mı? → Varsa bu mantık bir service’e taşınmalı.
4. Yeni bir repository metodu mu ekliyorum? → Önce interface’e (`IXRepository`), sonra implementasyona.
5. Entity mi yoksa DTO mu dönmeliyim? → Her zaman DTO.

> **Altın kural:** vNext’teki mevcut kodu incelediğinde bir deseni görüyorsan, yeni kodunu da o desene tam uygun yaz. "Bu sefer kısayol alırsak olur" diye bir durum yoktur — DDD sınırları kısmen ihlal edildiğinde tüm mimari değeri yitirir.

---

## 2. Reference Sources (READ-ONLY)

### Architecture and Implementation Docs

| Path | Content |
|------|---------|
| `vnext/docs/architecture/overview.md` | Layer diagram, Orchestration vs Execution, responsibilities |
| `vnext/docs/architecture/domain-models.md` | Instance aggregate, definitions, transitions, correlations |
| `vnext/docs/architecture/transition-pipeline.md` | Transition lifecycle, steps, sync/async strategies |
| `vnext/docs/architecture/multi-schema.md` | `ICurrentSchema`, schema resolution, multi-tenant patterns |
| `vnext/docs/implementation/application-services.md` | AppService map, Result/ConditionalResult patterns |
| `vnext/docs/implementation/infrastructure-layer.md` | `AddInfrastructureModule` breakdown, repositories, hooks |
| `vnext/docs/features/instance-filtering.md` | Legacy vs GraphQL filters, groupBy, aggregations |
| `vnext/docs/features/caching-strategy.md` | `DomainCacheContext`, `CacheSet`, distributed + fallback |
| `vnext/docs/infrastructure/opentelemetry-logging.md` | `WorkflowLogs`, `WorkflowEventIds`, telemetry conventions |

### Cursor Rules and AI Instructions

| Path | Content |
|------|---------|
| `vnext/.cursor/rules/vnext.mdc` | .NET coding standards, naming, DDD, Result pattern, logging, testing, API design, domain events |
| `vnext/CLAUDE.md` | Build/run commands, architecture overview, layer responsibilities, transition pipeline |

### Key Source Files (Pattern Reference)

| Purpose | File |
|---------|------|
| Orchestration controllers | `vnext/orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Controllers/` |
| Core application services | `vnext/src/BBT.Workflow.Application/` (Instances/, Definitions/, Functions/, Authorization/) |
| Domain entities and repos | `vnext/src/BBT.Workflow.Domain/Instances/` |
| EF Core implementations | `vnext/src/BBT.Workflow.Infrastructure/Instances/` |
| Shared web middleware | `vnext/src/BBT.Workflow.HttpApi.Shared/` |
| DI module composition | `vnext/orchestration/.../OrchestrationApiServiceCollectionExtensions.cs` |

---

## 3. Project Architecture

### Dependency Graph

```
BBT.Workflow.Monitor.HttpApi.Host
  ├── BBT.Workflow.Monitor.Application
  │     ├── BBT.Workflow.Domain          (entities, repos interfaces, value objects)
  │     └── BBT.Workflow.Application     (cache module, shared DTOs, IComponentCacheStore)
  └── BBT.Workflow.HttpApi.Shared        (ASP.NET middleware, DbContext, Dapr, telemetry)
        ├── BBT.Workflow.Domain
        └── BBT.Workflow.Infrastructure  (EF Core repos, event hooks, gateways)
```

The Monitor host composes a **read-focused subset** of the full vNext stack (orchestration komutları ve execution pipeline yok):
- Domain + Infrastructure modules (repositories, DbContext) — **davranış değişikliği olmadan** genişletilebilir; bkz. §1.2.
- `BBT.Workflow.Application` **tüketilir** (ör. cache modülü); orchestration application kodu **değiştirilmez**.
- Monitor-specific application services (`BBT.Workflow.Monitor.Application`)
- ASP.NET Core + telemetry + health checks

Intentionally **excluded**: event bus, Dapr pub/sub, outbox/inbox workers, background jobs, command application services, execution pipeline.

### Layer Responsibilities

| Layer | Project | What It Does |
|-------|---------|-------------|
| **Domain** | `BBT.Workflow.Domain` | Aggregate roots (`Instance`), repository interfaces, value objects, enums. **Genişletme:** bkz. §1.2 — yalnızca **yeni** üyeler; mevcut API’leri değiştirme. |
| **Application** | `BBT.Workflow.Monitor.Application` | Monitor query services, DTOs, DI registration (monitoring’e özel application katmanı). |
| **Infrastructure** | `BBT.Workflow.Infrastructure` | EF Core repos, DbContext, multi-schema. **Genişletme:** bkz. §1.2 — yalnızca **yeni** üyeler. |
| **HttpApi.Shared** | `BBT.Workflow.HttpApi.Shared` (read-only) | Middleware, health checks, exception handling, API versioning |
| **Host** | `BBT.Workflow.Monitor.HttpApi.Host` | Controllers, Program.cs, DI composition, middleware pipeline |

---

## 4. Current Implementation Status

### Completed

**Application Layer:**
- `IMonitorInstanceQueryService` + `MonitorInstanceQueryService` — instance list, detail, data, **state, hierarchy (cross-schema), unified `timeline` (full flow / single transition via `transitionId` / single task via `taskId` / `includeTasks`), faults, data diff**
- `IMonitorComponentQueryService` + `MonitorComponentQueryService` — component **definition** queries (route `components/definition`; `key` → latest version, `version` → exact)
- `IMonitorStatsService` + `MonitorStatsService` — instance status counters, live state distribution
- Pure, unit-tested helpers: `JsonDataDiff`, `InstanceHierarchyBuilder`
- All DTOs for instance, component, and stats queries

**Host Layer:**
- `MonitorInstanceController` — list, detail, data, data diff, state, timeline (unified), faults, hierarchy
- `MonitorComponentController` — `components/definition`
- `MonitorStatsController` — `stats/instances` (workflow + domain), `stats/states`
- `MonitorHealthController` — health detail
- Health checks, DI composition, middleware pipeline, Program.cs, appsettings.json, Dockerfiles

**Domain/Infrastructure (additive only):**
- `IInstanceRepository.CountAsync(filter)` + EF Core implementation (read-only filtered count, reuses existing filter path)
- `IInstanceTaskRepository.GetByIdAsReadOnlyAsync(id)` + EF Core implementation (`AsNoTracking` single-task lookup for the unified `timeline?taskId=`)

### Stubs (Interface Only)

- `IFlowHealthService` — empty interface in `Health/`
- (`IInstanceMetricsService` stub'ı `IMonitorStatsService` lehine kaldırıldı.)

### Not Yet Started

- Function endpoints (view, schema, extensions, authorize, authorization matrix, domain functions)
- Config/runtime info endpoint
- Instance extensions support
- ETag / conditional GET support (`If-None-Match`)
- Cross-schema/multi-domain aggregation (domain-wide `stats/instances` şu an best-effort: yalnızca `public` schema)
- Audit trail, scheduled job/timer monitoring, diagnostics (cache/DB/script) endpoints

Kullanıcıya dönük yetenek dokümanı: `docs/features/monitoring-features.md` · Tam ileriye dönük harita: `docs/upcoming/vnext-monitoring-upcoming-features.md`

---

## 5. Configuration

### Port Assignments

| Host | Port |
|------|------|
| Orchestration | 4201 |
| Execution | 4202 |
| **Monitor** | **4203** |

### Key appsettings.json Sections

| Section | Purpose |
|---------|---------|
| `ApplicationName` | `vnext-monitor` |
| `ConnectionStrings:Default` | PostgreSQL (same DB as orchestration) |
| `Redis` | Distributed cache (`Mode`, `InstanceName`, `Standalone`) |
| `UrlTemplates` | HATEOAS link generation. One key — `BasePath: "/api/v1/monitor"` — because the monitor serves its endpoints under that route prefix; per-endpoint templates are optional overrides |
| `Telemetry` | OpenTelemetry config (OTLP, tracing, metrics, logging) |
| `Vault:Enabled` | Dapr secret store toggle (default: false) |

---

## 6. Build and Run

Uygulama **yalnızca Docker ile** çalışacak şekilde tasarlanmıştır. Docker olmadan ayağa kaldırmak çok zahmetlidir: PostgreSQL, Redis, Dapr sidecar ve diğer bağımlılıkların ayrıca yapılandırılması gerekir. Bu nedenle **asla `dotnet run` ile uygulamayı ayağa kaldırmaya çalışmamalıyız**; yalnızca derleme (`dotnet build`) ile kod doğruluğunu kontrol etmek yeterlidir.

Kodları yazmaya odaklanmalıyız. build alınabilir ama restore, test gibi işlemlere çok girilmemeli. Kodlar düzgün yazılmış, syntax, linker, lint hatası yoksa bunu kontrol etmek yeterli.

### PostSharp Uyarıları

Build çıktısında **PostSharp** ile ilgili uyarılar veya hatalar görülebilir (örn. lisans, weaving, `PostSharp.targets` ile ilgili mesajlar). Bu uyarılar **görmezden gelinmeli** — monitoring projesi PostSharp kullanmaz; söz konusu mesajlar bağımlılık zincirindeki başka projelerden sızan derleme uyarılarıdır ve gerçek bir hata değildir. PostSharp çıktısı nedeniyle implementasyonu durdurmak veya sorgulamak gerekmez.

```bash
# Build monitoring projects
dotnet build vnext/monitoring/BBT.Workflow.Monitor.Application
dotnet build vnext/monitoring/BBT.Workflow.Monitor.HttpApi.Host

# Run locally (requires infrastructure: PostgreSQL, Redis, Dapr)
dotnet run --project vnext/monitoring/BBT.Workflow.Monitor.HttpApi.Host

# Health check
curl http://localhost:4203/health
curl http://localhost:4203/monitor/health/detail
```

---

## 7. Development Workflow

1. **Understand the requirement**: Which orchestration endpoint(s) need a monitoring mirror?
2. **Read the orchestration source**: Check the controller, app service, and DTOs in orchestration and application projects.
3. **Design the monitor contract**: Create simplified input/response DTOs.
4. **Implement the service**: Build a query-only service using domain repositories.
5. **Wire the controller**: Add the action to an existing or new controller.
6. **Register in DI**: Add `AddScoped` in `MonitorApplicationModuleServiceCollectionExtensions`.
7. **Ask the user to build and test**.

Step-by-step guide with code templates: `.cursor/skills/add-monitor-endpoint/SKILL.md`

### 7.1 Zorunlu Senkronizasyon (her YENİ veya DEĞİŞEN endpoint için)

Bir endpoint eklendiğinde veya değiştirildiğinde (route/parametre/davranış), kod tamamlanmış sayılmadan önce aşağıdaki **dört çıktı da güncellenmek ZORUNDADIR** — biri bile eksik bırakılamaz:

1. **`endpoints/vnext-monitor.http`** — endpoint için bir çağrı örneği eklenir; parametre varyasyonları ayrı satırlar olarak gösterilir.

2. **`endpoints/vnext-monitor.postman_collection.json`** — endpoint, ilgili **kullanıcı senaryosu** klasörüne eklenir. Kurallar:
   - İstek adı ve `description`, **kullanıcı gözüyle minimal** bir açıklama içerir: endpoint ne işe yarar, kullanıcı ne zaman/neden çağırır. **İç implementasyon, kodlama veya C# detayı YOK.**
   - Endpoint'in **parametrelerle birden fazla anlamlı kullanımı** varsa (ör. `?includeTasks=true`, `key` var/yok, `?version=`, farklı `type=`), her anlamlı varyant **ayrı bir istek** olarak eklenir; böylece kullanıcı farkı Postman'de doğrudan görebilir/fark edebilir. (Tek bir parametreli isteğe sıkıştırılmaz.)
   - Senaryo bazlı düzen korunur: 1-Dashboard, 2-Instance İzleme, 3-Hata Teşhisi, 4-Veri Analizi, 5-SubFlow, 6-Tanım Keşfi, 7-Filtreleme, 8-Sağlık, 9-Negatif. Yeni endpoint en uygun senaryoya yerleştirilir; uymuyorsa yeni bir senaryo klasörü açılır.
   - Koleksiyonun, controller route'larının **tamamını** kapsadığı korunur (yeni endpoint eklenince koleksiyon eksik kalmaz).

3. **`endpoints/vnext-monitor-endpoints.postman_collection.json`** — **faz bazlı endpoint listesi** (use-case koleksiyonundan ayrı, tekrar içermez). Kurallar:
   - Amaç: her endpoint'i **hızlı test edebilmek** için minimal, düz liste. Kullanıcı senaryosu odaklı değil, **endpoint odaklı**.
   - Klasör yapısı **faz bazlıdır**: her geliştirme fazı bir klasör altında toplanır (ör. `Phase 1 — Instance & Stats`, `Phase 2 — Components`, `Phase 3 — Functions`). Yeni faz başladığında yeni klasör açılır.
   - Her klasörde yalnızca o faza ait endpoint'ler, **tekrarsız** listelenir. Farklı parametreli anlamlı varyantlar (ör. `?includeTasks=true`) ayrı istek olarak gösterilir; anlamsız tekrar yoktur.
   - İstek adı: `METHOD route` formatı, kısa ve net (ör. `GET instances/{instance}`, `GET timeline?includeTasks=true`). Description isteğe bağlı, tek satır.
   - Bu koleksiyona use-case anlatısı veya senaryo düzeni eklenmez; saf endpoint envanteri olarak kalır.

4. **`docs/features/monitoring-features.md`** — endpoint, **kullanıcıyı yönlendiren** dille eklenir: monitoring'in vNext'i bu endpoint ile kullanıcıya **nasıl sunduğunu** ve kullanıcının onu **nasıl kullanacağını** anlatır. Yalnızca route, amaç, ne zaman kullanılacağı ve anlamlı parametre varyasyonları. **İç detay / kodlama / C# / DTO sınıf adı YOK.**

> Bu senkronizasyon, "Ask the user to build and test" adımından (Geliştirme Akışı 7) ÖNCE tamamlanır. Salt-okunur ilkesi gereği örnekler yalnızca GET'tir.

### Principles

- **Read-only first**: Monitor never mutates state. No command services, no transitions, no event publishing.
- **Mirror then simplify**: Start by duplicating the orchestration read contract, then simplify (drop extensions, conditional GETs) if not needed for the dashboard.
- **Same data, separate host**: Monitor reads the same PostgreSQL database and Redis cache. No data duplication or sync needed.
- **Performance over features**: Use `AsNoTracking`, avoid unnecessary includes, leverage cache store for definitions.

### 7.2 EF Core Zorunluluğu ve Raw SQL Yasağı

#### EF Core her zaman tercih edilir

Tüm veritabanı sorguları **EF Core LINQ** ile yazılır. vNext'teki schema switching (`NpgsqlSchemaConnectionInterceptor`), multi-tenant izolasyon ve `ICurrentSchema` mekanizması EF Core üzerinden çalışır; raw SQL bu convention'ların dışında kalır.

**Raw SQL (SqlQueryRaw, FromSqlRaw, ExecuteSqlRaw vb.) kullanmak yasaktır.** Tek istisna: EF Core'un kesinlikle ifade edemediği bir sorgu (örn. lateral join, recursive CTE, window function). Bu durumda bile:

1. **Önce kullanıcıya yaz ve neden EF Core'un yetmediğini açıkla.**
2. **Açık onay al** ("raw SQL yazabilirsin" veya benzeri) — onay gelmeden kod yazma.
3. Onay sonrası `EfRawSqlMetadata.QualifiedTable<T>()` ile schema-qualified tablo adı kullan; hiçbir zaman string literal tablo adı yazma.

#### EF Core sorgularını her zaman AsNoTracking ile yaz

Monitor API hiçbir zaman veri değiştirmez. Bu nedenle repository çağrılarında `AsNoTracking` / `AsReadOnly` varyantları kullanılır; change tracker devreye girmez, bellek ve CPU tasarrufu sağlanır.

```csharp
// DOĞRU
var entity = await instanceRepository.FindByIdentifierAsReadOnlyAsync(id, ct);
var list   = await instanceRepository.GetPagedResultsWithGroupsAsync(...); // zaten AsNoTracking

// YANLIŞ — monitor'da izleme gereksiz yük
var entity = await dbContext.Instances.FirstOrDefaultAsync(...);
```

Repository interface'leri zaten `AsReadOnly` / `AsNoTracking` uygulayan varyantlar sunar; bunlar kullanılır. Direkt `DbContext` erişimi gerekiyorsa `.AsNoTracking()` çağrısı zorunludur.

#### Multi-Schema — Şema Kullanım Kuralı

vNext'te şema iki farklı şekilde kullanılır:

| Yöntem | Ne zaman kullanılır |
|--------|---------------------|
| `currentSchema.Name` okumak | Şema **zaten set edilmiş**, sadece okunması gerekiyor |
| `currentSchema.Use(x)` | **Farklı** bir şemaya geçmek gerekiyor (cross-schema döngü vb.) |

**Şema ne zaman set edilir?**
`UseSchemaResolution()` middleware'i her request'te route'taki `{workflow}` parametresinden `ICurrentSchema.Name`'i otomatik doldurur. Bir controller action'a ulaşıldığında şema zaten doğru ayarlıdır.

**EF Core LINQ sorguları** bu şemayı otomatik kullanır — `NpgsqlSchemaConnectionInterceptor`, her veritabanı bağlantısında `search_path`'i `ICurrentSchema.Name`'e göre set eder. LINQ'te tablo adı nitelendirmeye gerek yoktur.

```csharp
// DOĞRU: farklı şemaya geçmek gerektiğinde (cross-schema hiyerarşi vb.)
using (currentSchema.Use(otherSchemaName))
{
    var result = await repository.GetAsync(...);
}

// YANLIŞ: Use() gereksiz yere şemayı yeniden set eder
using (currentSchema.Use(currentSchema.Name)) { ... } // fazla — Use() sadece geçiş için
```

**Onaylı raw SQL zorunluysa** tablo adı her zaman schema-qualified olmalıdır:

```csharp
// Onay alındıktan sonra — EfRawSqlMetadata zorunlu
var schema = currentSchema.Name ?? "public";
var table = EfRawSqlMetadata.QualifiedTable<Instance>(context, schema);
// → "my_flow"."Instances"
```

**`EfRawSqlMetadata` yardımcısı** (`vnext/src/BBT.Workflow.Infrastructure/Data/EfRawSqlMetadata.cs`): tablo ve kolon adlarını EF model metadata'sından türetir; rename'lere karşı derleme-zamanı güvencesi sağlar. Onaylı raw SQL'de bu yardımcı kullanılmak zorundadır.

### 7.3 Component Tanım Depolama Modeli

Monitoring'de bileşen (component) sorgularken aşağıdaki depolama modelini bilinmesi gerekir:

**`InstanceData.Data` (JSON blob) — yalnızca `Attributes` gövdesi:**
`PublishInput.Attributes` (JsonElement) veritabanında `InstanceData.Data` kolonuna ham blob olarak yazılır. Bu blob'un içeriği bileşen tipine göre farklılık gösterir; C# modeli üzerinden deserialize edilince aşağıdaki alanlar erişilebilir:

| Bileşen Tipi | Blob içinde bulunan alanlar |
|---|---|
| `sys-flows` | `type`, `labels` |
| `sys-tasks` | `type` |
| `sys-functions` | `scope` (labels yok) |
| `sys-extensions` | `type`, `scope` (labels yok) |
| `sys-schemas` | `type` (labels yok) |
| `sys-views` | `type`, `display`, `renderer`, `labels` |
| `sys-mappings` | `name` |

> Init aracından gelen JSON'da bu alanlar `attributes` sarmalayıcısı altındadır (`{ "attributes": { "type": "F" } }`). Publish sonrası depolanan blob'da bu alanlar **düz/flat** olarak tutulur; ayrıca bir `attributes` nesnesi yoktur.

**`Instance` entity kolonları — blob'un dışında:**
`PublishBaseInput.FlowVersion` ve `Tags`, `InstanceData.Data` blob'una değil `Instance.FlowVersion` ve `Instance.Tags` entity kolonlarına yazılır. `IRuntimeService.GetAsync<T>()` yalnızca `SetReference(key, domain, flow, version)` çağırır; `FlowVersion` ve `Tags` C# model nesnesine taşınmaz.

**Bu nedenle:** Bileşen özeti sorgusunda `FlowVersion` ve `Tags` erişmek için `IRuntimeService` yerine doğrudan `IInstanceRepository` kullanmak ve `item.Instance.FlowVersion` / `item.Instance.Tags` okumak gerekir.

**Bileşen tipi = PostgreSQL şema adı:**
`sys-flows`, `sys-tasks` gibi bileşen tipi stringleri aynı zamanda doğrudan PostgreSQL şema adıdır. Cross-schema bileşen sorgusu için `currentSchema.Use(componentType)` yeterlidir; `RuntimeSysSchemaInfo.Flows = "sys-flows"` sabiti bunu doğrular.

```csharp
// İzole scope içinde farklı bileşen şemasına geç
await using var scope = serviceScopeFactory.CreateAsyncScope();
var instanceRepo  = scope.ServiceProvider.GetRequiredService<IInstanceRepository>();
var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
using (currentSchema.Use(componentType))   // "sys-flows", "sys-tasks", vb.
{
    var page = await instanceRepo.GetActiveDataListPagedAsync(skip, pageSize, ct);
    // item.Instance.FlowVersion ve item.Instance.Tags burada erişilebilir
    // item.InstanceData.Data.JsonElement → bileşen gövdesi (Attributes blob)
}
```

### 7.4 Standart Pagination Response Yapısı

Monitoring'deki **tüm** list endpoint'lerinde tek bir pagination envelope kullanılır. Bu yapı `BBT.Workflow.Monitor.Application/Common/DTOs/MonitorPagedResponse.cs` dosyasında tanımlıdır.

```csharp
// Tüm monitor list endpoint'lerinin dönüş tipi
MonitorPagedResponse<T>
  ├── Pagination  : MonitorPaginationInfo?   // Sadece sayfalı sonuçlarda; groupBy'da JSON'da yoktur
  └── Items       : List<T>                 // Sayfalı öğeler veya group summary'leri

MonitorPaginationInfo
  ├── Page     : int   // Mevcut sayfa (1-tabanlı)
  ├── PageSize : int   // İstenen sayfa boyutu
  └── HasNext  : bool  // Sonraki sayfa var mı (COUNT sorgusu gerekmez)
```

**Örnek JSON — sayfalı sonuç:**
```json
{
  "pagination": { "page": 1, "pageSize": 10, "hasNext": true },
  "items": [ ... ]
}
```

**Örnek JSON — `groupBy` sonucu (`pagination` field'ı yoktur):**
```json
{
  "items": [ { "name": "Active", "count": 42 }, ... ]
}
```

> `Pagination = null` durumunda global `WhenWritingNull` ayarı sayesinde alan JSON çıktısında **hiç görünmez**; `"pagination": null` şeklinde dönmez.

**Kurallar:**
- Yeni bir list endpoint eklerken dönüş tipi her zaman `Result<MonitorPagedResponse<T>>` olmalıdır.
- `T`, somut item tipidir (ör. `MonitorInstanceResponse`, `MonitorComponentSummaryItem`). Aynı endpoint hem instance hem de group döndürüyorsa `object` kullanılır.
- `totalCount` eklenmez — ekstra COUNT sorgusu gerektirdiğinden performans önceliği nedeniyle dışlanmıştır.
- Bu yapı yalnızca **monitor** katmanına özgüdür; orchestration veya vNext application katmanına taşınmaz.

### 7.5 Monitoring Sorgu Optimizasyonları — Zorunlu Uygulamalar

Monitor API salt-okunur olduğundan, **yeni yazılan veya değiştirilen her monitoring metodu** aşağıdaki optimizasyonları varsayılan olarak uygulamalıdır. Bunlar tercihli değil, zorunludur.

#### AsNoTracking / AsReadOnly — Her Zaman

```csharp
// Her repository çağrısında
var entity = await instanceRepository.FindByIdentifierAsReadOnlyAsync(id, ct);
// Doğrudan DbContext kullanımında mutlaka
var items = await context.Instances.AsNoTracking().Where(...).ToListAsync(ct);
```

Change tracker monitor'da hiçbir değer katmaz; bellek ve CPU tasarrufu için her sorguda kullanılır.

#### Projeksiyon (Select) — Gereksiz Kolon Yükünden Kaçın

Tüm entity alanları gerekmiyorsa `Select()` ile sadece ihtiyaç duyulan alanlar çekilir:

```csharp
// YANLIŞ — 30 kolonlu Instance entity'sini tam yükler
var instances = await context.Instances.AsNoTracking().ToListAsync(ct);

// DOĞRU — yalnızca ihtiyaç duyulan 5 alan
var items = await context.Instances.AsNoTracking()
    .Select(i => new { i.Id, i.Key, i.Status, i.CurrentState, i.CreatedAt })
    .ToListAsync(ct);
```

`OwnsOne` kolonları (örn. `InstanceTransition.Body`, `.Header`) entity sorgusu yapılırken her zaman yüklenir — bunları dışlamak için `InstanceTransitionSlim` gibi bir projeksiyon tipi kullanılır.

#### Include Kısıtlama — Yalnızca Gerekli Navigation Property'ler

```csharp
// YANLIŞ — DataList tüm veri versiyonlarını yükler; LatestData gerekmiyorsa gereksizdir
var instance = await repo.WithDetailsAsync(id);   // Include(DataList) + Include(ChildCorrelations)

// DOĞRU — yalnızca ChildCorrelations gereken durumda slim versiyon
var instance = await instanceRepository.FindByIdentifierSlimAsync(id, ct);
```

#### Ne Zaman Hangi Yöntem

| Metot ihtiyacı | Tercih edilecek çağrı |
|----------------|----------------------|
| `LatestData`, `DataList` gerekiyor | `FindByIdentifierAsReadOnlyAsync` |
| Sadece Instance alanları + `ActiveCorrelations` | `FindByIdentifierSlimAsync` |
| Transition geçmişi (Body/Header gerekmez) | `GetByInstanceIdAsReadOnlyAsync` → `InstanceTransitionSlim` |
| Bileşen listesi (8 alan yeterli) | `GetActiveDataSummariesPagedAsync` |

> **Kapsam hatırlatıcısı:** Bu optimizasyonlar yalnızca monitoring metotlarına uygulanır — §1.2.1 kuralı gereği başka consumer'ı olan Domain/Infrastructure metotları ne kadar verimsiz olursa olsun dokunulmaz.

### Coding Patterns

All code patterns (DI, controller, service, DTO, result, pagination, naming): `.cursor/rules/monitor-coding-patterns.md`

### Domain Type Reference

Entity properties, repository methods, cache store API: `.cursor/skills/vnext-domain-reference/SKILL.md`

---

## 8. System Topology

### vNext Distributed Architecture

```
                     ┌─────────────────────┐
                     │   Client / Dashboard │
                     └──────┬──────────────┘
                            │
              ┌─────────────┼─────────────┐
              │             │             │
              ▼             ▼             ▼
    ┌─────────────┐ ┌──────────┐ ┌─────────────┐
    │Orchestration│ │ Monitor  │ │  Execution   │
    │  API :4201  │ │ API:4203 │ │  API :4202   │
    └──────┬──────┘ └─────┬────┘ └──────┬──────┘
           │              │             │
           │   ┌──────────┼─────┐       │
           │   │   Shared DB    │       │
           ▼   ▼                ▼       │
    ┌──────────────┐    ┌───────────┐   │
    │  PostgreSQL  │    │   Redis   │   │
    │ (multi-schema│    │  (cache)  │   │
    └──────────────┘    └───────────┘   │
           │                            │
           ▼                            │
    ┌──────────────┐              ┌─────┘
    │   Workers    │              │
    │ Inbox/Outbox │◄── Dapr ────┘
    └──────────────┘
```

### Host Roles

| Host | Project | Port | Role |
|------|---------|------|------|
| **Orchestration** | `BBT.Workflow.Orchestration.HttpApi.Host` | 4201 | Public-facing: instance start, transitions, queries, definitions, functions |
| **Execution** | `BBT.Workflow.Execution.HttpApi.Host` | 4202 | Internal: stateless task invoker (HTTP, scripts, Dapr). Called by Orchestration via Dapr |
| **Monitor** | `BBT.Workflow.Monitor.HttpApi.Host` | 4203 | Read-only: dashboard/observability queries. Shares DB + cache, no event bus |
| **Inbox Worker** | `BBT.Workflow.Workers.Inbox` | — | Distributed event consumer (domain events from bus) |
| **Outbox Worker** | `BBT.Workflow.Workers.Outbox` | — | Transactional outbox publisher (domain events to bus) |
| **DbMigrator** | `BBT.Workflow.DbMigrator` | — | EF Core schema migrations at deploy time |
| **Init** | `VNext.Init.Host` (Node.js) | 3005 | Package/definition publisher (bootstrap tooling) |

### What Monitor Does NOT Do

Monitor is **strictly read-only** and does NOT:
- Run transition pipeline or execution steps
- Publish or consume domain events (no Dapr pub/sub)
- Run background jobs or scheduled tasks
- Invoke task executors (HTTP, script, Dapr bindings)
- Manage inbox/outbox message processing
- Run database migrations
- Handle CloudEvents or Dapr subscriptions

### Docker Compose

Monitor is a first-class container in the Docker stack:
- `docker-compose.dev.yml`: `vnext-monitoring-app` on port 4203, Dapr sidecar `vnext-monitoring`
- Components path: `etc/monitoring/dapr/components` (dev) or `etc/workers/monitoring/dapr/components` (base compose)
- Environment: `.env.monitoring.dev` / `.env.monitoring.stage`
- Observability: OpenTelemetry collector → OpenObserve/Jaeger; Prometheus `/metrics` endpoint; Grafana dashboard

### Shared Infrastructure

Monitor connects to the **same** PostgreSQL database and Redis cache as Orchestration:
- **PostgreSQL**: Multi-schema (one schema per workflow). `ConnectionStrings:Default` in appsettings.json
- **Redis**: `IDistributedCache` for component definitions and runtime state. `Redis` section in appsettings.json
- **Dapr**: Optional secret store (Vault), state store (Redis), lock store. Configured via Dapr component YAML files
- **Telemetry**: OTLP exporter for traces, metrics, logs. `Telemetry` section in appsettings.json



# endpoints folder

`.http` ve Postman collection `.json` dosyaları kökteki bu `endpoints` klasöründe tutulur. Yalnızca monitoring endpoint'leri.

**Zorunlu:** Her yeni/değişen endpoint dört dosyada güncellenmelidir:
1. `endpoints/vnext-monitor.http` — `.http` çağrı örneği
2. `endpoints/vnext-monitor.postman_collection.json` — kullanıcı senaryosu odaklı koleksiyon
3. `endpoints/vnext-monitor-endpoints.postman_collection.json` — faz bazlı saf endpoint listesi (tekrarsız, hızlı test amaçlı)
4. `docs/features/monitoring-features.md` — kullanıcı dokümanı

