# Faz 2 — DB Resilience (Reshaped) (Bite-Sized Plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Kısa, gerçekten geçici DB **bağlantı** hatalarında (PGBouncer anlık erişilemezliği, failover, socket reset) transition'ı sınırlı kez yeniden dene — **pool-exhaustion ve acquire-timeout HARİÇ** (bunları retry etmek doygunluğu büyütür). Exponential backoff + jitter ile thundering-herd'i önle.

**Architecture:** EF Core `EnableRetryOnFailure` bu kod tabanında KULLANILAMAZ — Aether UoW explicit `Database.BeginTransactionAsync` kullanıyor ([aether EfCoreTransactionSource.cs:36]), retrying execution strategy "user-initiated transactions" ile çakışır. Bunun yerine **uygulama-seviyesi Polly retry**, transition'ın UoW scope'unu saracak şekilde `TransitionRunner.ExecuteWithScopeAsync` seam'ine konur. Pool-exhaustion/connect failure'lar `uowManager.BeginAsync`'te (Npgsql `BeginTransactionAsync` bağlantıyı eager açar) — yani **core iş ve commit ÖNCESİ** — fırlar; bu noktada hiçbir şey commit edilmediği için tüm scope'u yeniden çalıştırmak güvenlidir. `PublishDeferredEventsAsync` kendi exception'ını zaten yutar (retry tetiklemez).

**Tech Stack:** .NET 10, Polly v8 (`ResiliencePipeline`, `RetryStrategyOptions`), Npgsql, xUnit + NSubstitute + Shouldly. Mevcut referans: `src/BBT.Workflow.Application/Resilience/ResultResiliencePipelineFactory.cs` (Polly kullanım deseni), `ResultRetryOptions`.

**Spec kaynağı:** [2026-06-19-vnext-load-test-remediation.md](2026-06-19-vnext-load-test-remediation.md) Faz 2 (yeniden şekillendirildi).

---

## Ön Kontroller (Görev 0 — subagent, kodlamadan önce)

- [ ] **Katman/Npgsql referansı:** `DbTransientErrorClassifier` Npgsql tiplerini (`NpgsqlException`, `PostgresException`) görmek isteyebilir. `BBT.Workflow.Application` Npgsql'i referanslıyor mu kontrol et (`grep -rin "using Npgsql" src/BBT.Workflow.Application | head`). 
  - Referanslıyorsa: classifier'ı Application'a koy.
  - Referanslamıyorsa (muhtemel): classifier'ı **tip-adı + mesaj + SQLSTATE (reflection'sız, public API)** üzerinden Npgsql'e bağımlı olmadan Application'da yaz; VEYA classifier'ı Infrastructure'a koyup bir arayüzle (`IDbTransientErrorClassifier`) Application'a enjekte et. Katman temizliğini koru; kararı PR notuna yaz.
- [ ] **Polly sürümü:** `Directory.Packages.props`'ta Polly sürümünü doğrula (v8 API: `ResiliencePipelineBuilder`, `RetryStrategyOptions`, `DelayBackoffType.Exponential`, `UseJitter`).
- [ ] **Logging:** Yeni log için `BBT.Workflow.Domain/Logging/WorkflowLogs.cs`'e `[LoggerMessage]` ekle (raw logger YASAK). EventId için mevcut desen (20xxx instances / 10xxx transitions). OnRetry → `Warning`.

---

## Task 1: DbTransientErrorClassifier (saf, TDD)

**Files:**
- Create: `src/BBT.Workflow.<Application|Infrastructure>/Resilience/DbTransientErrorClassifier.cs` (Ön-Kontrol kararına göre)
- Test: `test/BBT.Workflow.<Application|Infrastructure>.Tests/Resilience/DbTransientErrorClassifierTests.cs`

**Davranış:** `public static bool IsRetriableTransient(Exception ex)`:
- **FALSE** (asla retry):
  - Mesaj `"pool has been exhausted"` içeriyorsa (Npgsql connection-pool tükenmesi).
  - Bu exception'ı saran/iç `TimeoutException` ("operation has timed out") yalnızca pool-acquire kaynaklıysa → pool-exhaustion ile birlikte FALSE.
- **TRUE** (sınırlı retry):
  - `NpgsqlException` ve `IsTransient == true` (pool-exhaustion DEĞİLse).
  - İç `System.Net.Sockets.SocketException`, veya mesaj `"Failed to connect"` (PGBouncer/ağ anlık erişilemezliği).
  - `PostgresException.SqlState` ∈ { `08000`,`08003`,`08006`,`08001`,`08004` (connection_*), `57P01` (admin_shutdown), `57P03` (cannot_connect_now) }.
- Diğer her şey (ör. genel `InvalidOperationException`, business hataları) **FALSE**.
- `AggregateException`/iç exception zincirini düzleştirip değerlendir (inner'larda pool-exhaustion varsa FALSE kazanır).

- [ ] **Step 1: Failing testler** (Npgsql tipleri kurulamıyorsa, gerçek Npgsql exception örnekleri yerine mesaj/tip-adı tabanlı test kur; mümkünse gerçek `NpgsqlException`/`PostgresException` kullan):

```csharp
public class DbTransientErrorClassifierTests
{
    [Fact] public void Pool_exhaustion_is_not_retriable() {
        var ex = new InvalidOperationException(
            "An exception has been raised that is likely due to a transient failure.",
            MakePoolExhaustedException()); // helper builds an exception whose message contains "pool has been exhausted"
        DbTransientErrorClassifier.IsRetriableTransient(ex).ShouldBeFalse();
    }

    [Fact] public void Connect_failure_is_retriable() {
        var ex = MakeConnectFailure(); // NpgsqlException("Failed to connect to 10.0.0.1:5432", new SocketException())
        DbTransientErrorClassifier.IsRetriableTransient(ex).ShouldBeTrue();
    }

    [Fact] public void Generic_invalidop_is_not_retriable() {
        DbTransientErrorClassifier.IsRetriableTransient(new InvalidOperationException("nope")).ShouldBeFalse();
    }

    [Fact] public void Pool_exhaustion_wins_even_if_other_inner_looks_transient() {
        // exception chain containing both a socket-like and a pool-exhausted message → FALSE
        ...
    }
}
```
(Helper'ları gerçek Npgsql tipleriyle kurmak zorsa, classifier'ı mesaj/tip-adı sözleşmesi üzerinden test et — ama prod kodu gerçek tipi de doğru sınıflandırmalı. Implementer en sağlam yolu seçer.)

- [ ] **Step 2: FAIL gör.**
- [ ] **Step 3: Minimal implementasyon** — yukarıdaki kuralları uygula; iç exception'ları `while (ex.InnerException != null)` ile dolaş; `"pool has been exhausted"` görülürse hemen FALSE döndür (öncelikli).
- [ ] **Step 4: PASS gör.**
- [ ] **Step 5: Commit** — `feat(resilience): add DbTransientErrorClassifier (excludes pool exhaustion)`.

---

## Task 2: DbRetryOptions + DI binding

**Files:**
- Create: `src/BBT.Workflow.Domain/Resilience/DbRetryOptions.cs`
- Modify: DI kayıt yeri — `src/BBT.Workflow.Application/Microsoft/Extensions/DependencyInjection/WorkflowApplicationModuleServiceCollectionExtensions.cs` (mevcut `ResultRetryOptions` binding'inin yanında, ~line 158).

**DbRetryOptions:**
```csharp
public sealed class DbRetryOptions
{
    public const string SectionName = "DbRetry";
    public int MaxRetryAttempts { get; set; } = 3;     // küçük tut — amplifikasyonu önle
    public int BaseDelayMilliseconds { get; set; } = 100;
    public int MaxDelayMilliseconds { get; set; } = 2000;
    public bool UseJitter { get; set; } = true;
}
```

- [ ] **Step 1:** `DbRetryOptions` oluştur (XML docs ile).
- [ ] **Step 2:** `services.Configure<DbRetryOptions>(configuration.GetSection(DbRetryOptions.SectionName))` ekle; section yoksa default kalsın (`ResultRetryOptions` deseninin aynısı).
- [ ] **Step 3: Derle.**
- [ ] **Step 4: Commit** — `feat(resilience): add DbRetryOptions (bounded transient DB retry config)`.

---

## Task 3: TransitionRunner'a transient-bağlantı retry'ı sar (integration)

**Files:**
- Modify: `src/BBT.Workflow.Application/Execution/Services/TransitionRunner.cs`
- Modify: `src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs` (OnRetry için `[LoggerMessage]`)

**Davranış:**
- TransitionRunner ctor'una `IOptions<DbRetryOptions>` (veya bir `IDbTransientRetryPipelineFactory`) enjekte et; bir `ResiliencePipeline` kur:
```csharp
_pipeline = new ResiliencePipelineBuilder()
    .AddRetry(new RetryStrategyOptions
    {
        MaxRetryAttempts = opts.MaxRetryAttempts,
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = opts.UseJitter,
        Delay = TimeSpan.FromMilliseconds(opts.BaseDelayMilliseconds),
        MaxDelay = TimeSpan.FromMilliseconds(opts.MaxDelayMilliseconds),
        ShouldHandle = new PredicateBuilder().Handle<Exception>(DbTransientErrorClassifier.IsRetriableTransient),
        OnRetry = args => { logger.<WorkflowLogs OnRetry method>(...); return default; }
    })
    .Build();
```
- `ExecuteWithScopeAsync` çağrısını pipeline ile sar — her deneme **taze DI scope + RequiresNew UoW** alacak şekilde (yani retry, `scopeFactory.ExecuteWithWorkflowAsync(...)` çağrısının TAMAMINI sarmalı):
```csharp
public async Task<Result<TransitionOutput>> RunAsync(WorkflowExecutionContext context, CancellationToken ct = default)
{
    var hopResult = await _pipeline.ExecuteAsync(
        async token => await ExecuteWithScopeAsync(context, token), ct);
    ...
}
```
- **Güvenlik notu (kodda yorum olarak ekle):** Retry yalnızca `IsRetriableTransient` true olduğunda tetiklenir; bu hatalar `uowManager.BeginAsync` (eager `BeginTransactionAsync`) sırasında, core iş ve commit ÖNCESİ fırlar → hiçbir şey commit edilmemiştir → tüm scope'u yeniden çalıştırmak güvenli. `PublishDeferredEventsAsync` kendi exception'ını yutar; retry tetiklemez.
- **Pool-exhaustion**: classifier FALSE döndürür → asla retry edilmez (gereksinim).

- [ ] **Step 1:** `WorkflowLogs.cs`'e OnRetry için `[LoggerMessage]` ekle (Warning, unique EventId; params: `{TransitionKey}`, `{AttemptNumber}`, `{DelayMs}`).
- [ ] **Step 2:** TransitionRunner'a pipeline'ı kur + `RunAsync`'i pipeline ile sar. Mevcut `logger.LogError` (line 99) raw kullanımını da WorkflowLogs'a taşı (küçük temizlik; bu dosyaya dokunuyoruz).
- [ ] **Step 3: Test** — `test/BBT.Workflow.Application.Tests/Execution/Services/TransitionRunnerRetryTests.cs`:
  - `scopeFactory` ilk çağrıda retriable transient fırlatıp ikinci çağrıda başarı döndürdüğünde → `RunAsync` başarılı, scope 2 kez çağrıldı.
  - İlk çağrı **pool-exhaustion** fırlattığında → retry YOK (scope 1 kez çağrıldı), hata yukarı taşınır.
  - (NSubstitute ile `IServiceScopeFactory`/`ExecuteWithWorkflowAsync` davranışını taklit etmek zorsa, retry mantığını test edilebilir küçük bir sarmalayıcıya çıkar ve onu test et; implementer en sağlam yolu seçer ve gerekçeyi yazar.)
- [ ] **Step 4: Derle + test PASS.**
- [ ] **Step 5: Commit** — `feat(resilience): bounded transient-connection retry around transition UoW (excludes pool exhaustion)`.

---

## Task 4: Doğrulama + "pool-exhaustion hiçbir yerde retry edilmez" denetimi

- [ ] **Step 1:** `dotnet build src/BBT.Workflow.Application src/BBT.Workflow.Domain` → 0 error.
- [ ] **Step 2:** `dotnet test --filter "FullyQualifiedName~Resilience|FullyQualifiedName~TransitionRunnerRetry"` → yeşil.
- [ ] **Step 3: Audit (rapor, kod değişikliği opsiyonel):** Mevcut retry yollarını tara ve pool-exhaustion'ı retry edip etmediklerini raporla:
  - `DirectTriggerRemoteInvoker` (HTTP retry — DB exception görmez, OK),
  - Dapr job retry / `TriggerRetryOptions` / `RemoteOptions.MaxRetryAttempts`,
  - `ResultResiliencePipelineFactory` (yalnızca error-code bazlı; pool-exhaustion error-code üretmez → retry etmez, OK).
  - Bulguları kısa bir not olarak yaz; pool-exhaustion'ı retry eden bir yol bulunursa FLAG'le (bu fazda düzeltme opsiyonel, raporla).
- [ ] **Step 4: Regresyon** — `dotnet test test/BBT.Workflow.Application.Tests` → yeni testler yeşil; pre-existing 24 hata değişmedi (master ile aynı).

---

## Kabul Kriterleri (faz)
- `DbTransientErrorClassifier`: pool-exhaustion → FALSE, gerçek connect/transient → TRUE (unit testle kanıtlı).
- TransitionRunner transient connect hatasında sınırlı kez (default 3) backoff+jitter ile yeniden dener; pool-exhaustion'da denemez.
- Yeni log WorkflowLogs üzerinden (raw logger yok).
- Build + ilgili testler yeşil; regresyon yok.

## Kapsam Dışı / Sınırlar (şeffaflık)
- Bu faz yalnızca **birincil transition execution seam'ini** (TransitionRunner) kapsar. Inbox event-handler'ları ve AsyncTransitionStrategy'nin ayrı kısa UoW'ları (SetBusy, job-intent) bu retry'a dahil DEĞİL — gerekirse ayrı follow-up.
- Retry, doygunluğu ÇÖZMEZ; yalnızca kısa/gerçek geçici kesintilere dayanıklılık verir. Asıl çözüm Faz 4 (transaction sınırı) + Faz 5 (konfig).
