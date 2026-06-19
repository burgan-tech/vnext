# vNext Yük Testi İyileştirme — Faz Planı (Spec Seti)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Preprod yük testinde gözlenen Npgsql connection-pool tükenmesi ve transient DB hatalarını, mimari kök nedeni (DB transaction'ının uzak çağrı boyunca açık tutulması) ve destekleyici faktörleri (resilience yok, health-check baskısı, ölçüm yok, konfig) gidererek ortadan kaldırmak.

**Architecture:** Senkron transition pipeline'ı `TransitionRunner.ExecuteWithScopeAsync` tüm auto-chain'i (adımlar + post-commit + uzak OnExecute görevleri) **tek `RequiresNew` UoW** içinde çalıştırıyor; bağlantı, 30–60 sn'lik Dapr/HTTP round-trip'i boyunca havuzdan pinli kalıyor. Çözüm, çağrıyı ertelemeden transaction sınırını state-mutasyon adımlarına daraltmak + destekleyici sertleştirmeler.

**Tech Stack:** .NET 10, EF Core + Npgsql, Aether UoW (`IUnitOfWorkManager`, `RequiresNew`), Dapr service invocation, OpenTelemetry/Prometheus, PostgreSQL + PGBouncer (transaction mode), xUnit + NSubstitute + Shouldly.

**Kaynak analiz:** [ai-docs/load-test-analysis-2026-06-19.md](../../../ai-docs/load-test-analysis-2026-06-19.md)

---

## Kritik Değerlendirme (başlamadan oku)

1. **Kod vs Ops ayrımı.** Bazı düzeltmeler repo kodu (test edilebilir), bazıları preprod/ops konfigürasyonu (repo'da yok — env/secret/K8s). Connection string (`Host/Port/Max Pool Size`), Postgres `max_connections`, K8s probe periyotları **repo'da bulunmuyor**. Bu kalemler "kod" fazı değil, **ops runbook** olarak ele alınır; değerleri DevOps doğrulamalı/uygulamalı.
2. **P0 refactor riskli ve geniştir.** Transaction tüm auto-chain'i kapsıyor. Bite-sized adımlardan önce **karakterizasyon testleri** (mevcut commit sınırlarını sabitleyen) yazılmalı; aksi halde atomiklik/regresyon riski yüksek. Bu yüzden Faz 4'ün ilk görevi tasarım+karakterizasyondur.
3. **Ölçüm önce.** Refactor'un etkisini kanıtlamak için pool metrikleri (Faz 1) refactor'dan (Faz 4) önce gelir.
4. **Branch.** `master` üzerinde implementasyon yapılmayacak; her faz için feature branch / worktree açılır (execution handoff'ta).

---

## Faz (Spec) Sırası ve Bağımlılıklar

| Faz | Spec başlığı | Tür | Bağımlılık | Risk | Doğrulama |
|----|----|----|----|----|----|
| 0 | Ortam doğrulama & konfig runbook | Ops | — | Düşük | DevOps onayı |
| 1 | DB pool gözlemlenebilirliği (metrikler) | Kod | — | Düşük | Unit + metrik panosu |
| 2 | DB resilience (EnableRetryOnFailure + akıllı sınıflandırma) | Kod | 1 | Orta | Unit + load |
| 3 | Health-check baskısının azaltılması | Kod | 1 | Düşük | Unit + probe testi |
| 4 | **P0 — Transaction sınırını daralt (Seçenek A)** | Kod | 1,2 | **Yüksek** | Karakterizasyon + unit + load |
| 5 | Konfig uygulama & yük testi tekrarı | Ops+Test | 0–4 | Orta | Load test karşılaştırma |

> Her faz **kendi başına çalışan, test edilebilir** bir teslimat üretir. Faz başına bite-sized adımlar, o faza başlarken (writing-plans ile) detaylandırılır; aşağıda her spec'in kapsamı, dokunulacak dosyalar, kabul kriterleri ve test stratejisi tanımlıdır.

---

## Faz 0 — Ortam Doğrulama & Konfig Runbook (Ops)

**Goal:** Repo'da olmayan preprod gerçeklerini doğrulamak ve hedef konfig değerlerini netleştirmek.

**Doğrulanacaklar (DevOps ile):**
- [ ] Uygulama connection string'i: host/port **PGBouncer (6432)** mı yoksa doğrudan Postgres (**5432**) mı? (Log `:5432` gösteriyor — baypas şüphesi.)
- [ ] Etkin Npgsql parametreleri: `Maximum Pool Size`, `Timeout`, `Connection Idle Lifetime`, `Multiplexing`, `Max Auto Prepare`.
- [ ] Postgres `max_connections` ve mevcut bağlantı kullanımı (pg_stat_activity).
- [ ] PGBouncer `default_pool_size`(100) + `reserve_pool_size`(50) ile (instance×poolSize) oranı.
- [ ] K8s liveness/readiness `periodSeconds`/`timeoutSeconds`/`failureThreshold`.

**Hedef konfig (öneri, Faz 5'te uygulanır):**
- App → **PGBouncer 6432** (transaction mode).
- `Maximum Pool Size` instance başına **20–30** (16 instance × 25 ≈ 400 client conn; PGBouncer 100+50 reserve ile çoğullanır).
- `Max Auto Prepare=0` veya `No Reset On Close=true` (transaction-mode + `max_prepared_statements=0` ile uyum) — Faz 0'da prepared statement davranışı doğrulanır.

**Kabul:** Yukarıdaki değerler bir tabloda belgelenir; hedef değerler DevOps tarafından onaylanır. **Kod değişikliği yok.**

---

## Faz 1 — DB Pool Gözlemlenebilirliği

**Goal:** Npgsql connection-pool durumunu (busy/idle/waiting, açma süresi) ve PGBouncer metriklerini panoya taşımak; refactor öncesi/sonrası kıyas için baz çizgi.

**Files:**
- Modify: `src/BBT.Workflow.Application/.../IWorkflowMetrics` implementasyonu (mevcut `IWorkflowMetrics` + `Meter` altyapısı; tam yol Faz başında doğrulanır).
- Modify: DB DI kaydı `src/BBT.Workflow.HttpApi.Shared/Microsoft/Extensions/DependencyInjection/WorkflowApiBaseServiceCollectionExtensions.cs` — `NpgsqlDataSourceBuilder` ile `EnableMetrics`/event-counter köprüsü.
- Test: `test/BBT.Workflow.Application.Tests/...Metrics...`

**Changes (kapsam):**
- Npgsql'in `Microsoft.Extensions.Diagnostics` / EventCounters çıktısını (`ado.net`/`Npgsql` meter: `db.client.connections.usage`, `...connections.pending_requests`) OTel `MeterProvider`'a ekle (`AddMeter("Npgsql")`).
- Mümkünse PGBouncer `SHOW POOLS/STATS` için ayrı bir scrape (prometheus pgbouncer-exporter) — ops tarafı; kodda yalnızca app-pool metrikleri.

**Kabul:** Yük altında pool busy/idle/pending grafikleri görünür; `pending_requests > 0` (kuyruk) gözlemlenebilir.

**Test stratejisi:** Meter kaydının DI'da var olduğunu doğrulayan unit test; manuel olarak `/metrics` endpoint'inde Npgsql sayaçlarının çıktığının teyidi.

**Risk:** Düşük (yalnızca ekleme).

---

## Faz 2 — DB Resilience (EnableRetryOnFailure + Akıllı Sınıflandırma)

**Goal:** Gerçek geçici DB hatalarında dayanıklılık; **pool exhaustion'ı transient sayıp retry ETMEMEK** (kuyruğu büyütmesin).

**Files:**
- Modify: `src/BBT.Workflow.HttpApi.Shared/.../WorkflowApiBaseServiceCollectionExtensions.cs:111,134` — `UseNpgsql(..., o => o.EnableRetryOnFailure(...))`.
- Modify: aynı kayıt — `MessagingDbContext` ve Inbox (`workers/.../InboxWorkerServiceCollectionExtensions.cs:97`).
- Create: özel `NpgsqlRetryingExecutionStrategy` alt sınıfı veya `errorCodesToAdd`/`ShouldRetryOn` ile pool-exhaustion'ı **dışlayan** strateji.
- Test: `test/BBT.Workflow.Infrastructure.Tests/.../ExecutionStrategyTests.cs` (yeni).

**Changes:**
- `EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: 5s)` — değerler config'ten okunur.
- `ShouldRetryOn`: `NpgsqlException` içinde "pool has been exhausted" / `TimeoutException(pool)` → **false**; gerçek geçici (deadlock 40P01, serialization 40001, connection reset 08006) → true.

**Kabul:** Unit test: pool-exhaustion exception'ı retry edilmiyor; deadlock/serialization retry ediliyor. Load: "transient failure" log hacmi düşer, geçici kesintide transition komple düşmez.

**Risk:** Orta — yanlış sınıflandırma retry fırtınası yapar; testle sabitlenmeli.

---

## Faz 3 — Health-Check Baskısının Azaltılması

**Goal:** `/ready` DB probe'unun her çağrıda taze bağlantı açıp havuzu/PGBouncer'ı yormasını engellemek; flapping'i azaltmak.

**Files:**
- Modify: `src/BBT.Workflow.HttpApi.Shared/Microsoft/Extensions/DependencyInjection/HealthChecksServiceCollectionExtensions.cs:26` (`AddNpgSql(...)`).
- Modify: `src/BBT.Workflow.HttpApi.Shared/Microsoft/AspNetCore/Builder/WorkflowHealthCheckMapExtensions.cs` (gerekirse cache/predicate).
- Modify: Inbox/Outbox host kayıtları — worker'larda DB readiness'i **kaldır** (trafik yönlendirmeye etkisi yok).
- Test: `test/...HealthCheck...` predicate/timeout testi.

**Changes (kademeli):**
1. `AddNpgSql`'e kısa `timeout: TimeSpan.FromSeconds(2)` ver; EF `NpgsqlDataSource`'u paylaştır (yeni bağlantı açma yerine).
2. Sonucu cache'le: health-check evaluation interval (ör. 10 sn) veya `CachedHealthCheck` sarmalı.
3. Worker'larda DB readiness check'i kaldır; yalnızca `self`.

**Kabul:** Unit: `/live` yalnızca `self`; worker'larda `database` check yok. Manuel: probe sırasında yeni bağlantı açılmadığı (cache aktifken) gözlenir.

**Risk:** Düşük. (Dikkat: readiness'i tamamen kaldırmak gerçek DB kesintisinde pod'u trafikte tutar — cache'li/timeout'lu yaklaşım tercih edilir.)

---

## Faz 4 — P0: Transaction Sınırını Daralt (Seçenek A) — YÜKSEK RİSK

**Goal:** Uzak OnExecute görevini (Dapr → Execution) **açık DB transaction'ı tutmadan** senkron çalıştırmak; transaction yalnızca state-mutasyon adımlarını kapsasın.

**Files (okuma/değişiklik — kesin sınır Görev 4.1'de netleşir):**
- `src/BBT.Workflow.Application/Execution/Services/TransitionRunner.cs` (UoW sahibi — `ExecuteWithScopeAsync`)
- `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/TransitionPipeline.cs` (`RunAsync`/`RunChainAsync`)
- `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/TransitionExecutor.cs` (`ExecuteOneAsync` step loop)
- Steps: `RunOnExecuteTasksStep.cs` (order 30), `CreateTransitionRecordStep.cs` (order 20, `saveChanges:true`), `SetBusyStep.cs`, `ChangeStateStep.cs`
- Test: `test/BBT.Workflow.Application.Tests/Execution/Transitions/Pipeline/*`

**Hedef commit sınırları:**

| Faz | Adımlar | UoW |
|---|---|---|
| Pre-guard | SetBusy(19), CreateTransitionRecord(20) | kısa `RequiresNew` → commit, bağlantı bırak |
| Remote | ResourceLock(25), RunOnExecuteTasks(30) | **UoW YOK** — Dapr/HTTP bağlantı tutmadan |
| Persist | ChangeState(50)…Finalize(110) + task sonucu yazımı | tek `RequiresNew` → atomik commit |
| Post-commit | job enqueue / event publish | mevcut transaction-dışı yol |

**Görevler (sıra):**
- **4.1 Tasarım + karakterizasyon (zorunlu ilk):** Mevcut UoW sahipliğini (TransitionRunner ↔ TransitionPipeline ↔ Executor) tam haritalandır; auto-chain'in tek transaction'ı nasıl paylaştığını belgele. Mevcut davranışı sabitleyen karakterizasyon testleri yaz (state change + transition record + task result atomik commit; hata → fault). **Çıktı: kısa tasarım notu + yeşil testler.**
- **4.2** Remote fazı transaction dışına alacak şekilde sınırı uygula; her adımın `saveChanges`/repository çağrısını yeni UoW sınırına eşle.
- **4.3** Error boundary değerlendirmesinin Persist UoW içinde çalıştığını doğrula/ayarla.
- **4.4** Idempotency: remote faz tekrar/duplicate'e dayanıklı (ChainReaper backstop ile uyum).

**Kabul:**
- Karakterizasyon + mevcut pipeline testleri yeşil (regresyon yok).
- Yeni test: RunOnExecuteTasks sırasında **açık transaction/connection tutulmadığı** (UoW yokluğu) doğrulanır.
- Load: pool-exhaustion log hacmi ~sıfıra iner; p95/p99 latency iyileşir.

**Risk:** Yüksek — atomiklik bölünmesi, kısmi ilerleme. Karakterizasyon testleri ve idempotency invariant'ları zorunlu. Async yol (`AsyncTransitionStrategy`) zaten bu disipline sahip; referans alınır.

---

## Faz 5 — Konfig Uygulama & Yük Testi Tekrarı (Ops + Test)

**Goal:** Faz 0 hedef konfigini uygulamak ve aynı JMeter senaryosuyla iyileşmeyi kanıtlamak.

**Adımlar:**
- [ ] PGBouncer (6432) bağlantısı + `Maximum Pool Size` 20–30 uygula (env/secret).
- [ ] Faz 3 health + Faz 2 resilience canlıya alındıktan sonra aynı yük profili (20 thread / 300 sn) ile tekrar koş.
- [ ] Kademeli yük artışı (20 → 50 → 100 thread) ile gerçek kapasiteyi ölç.

**Kabul (kıyas, baz: 2026-06-19):**
- `pool has been exhausted` ve `transient failure` logları **~%95+ azalma**.
- p95 ≤ ~800 ms korunur; max latency 30 sn pikleri kaybolur.
- DB health `Unhealthy` olayları yok.

---

## Self-Review Notları
- **Spec coverage:** Analiz dökümanındaki tüm öneri başlıkları (P0-refactor→Faz4, P0-config→Faz0/5, P1-resilience→Faz2, P1-health→Faz3, P2-observability→Faz1) bir faza eşlendi.
- **Ops kalemleri** (connection string, max_connections, probe periyodu) bilinçli olarak Faz 0/5'te ops görevi; repo'da TDD edilmez.
- **Faz 4** için bite-sized adımlar, Görev 4.1 (tasarım+karakterizasyon) tamamlanmadan yazılmayacak — aksi placeholder olurdu.
