# Faz 4 — P0: Transaction Sınırını Daralt (Bite-Sized Plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`).
> **YÜKSEK RİSK.** Task 4.1 (tasarım + karakterizasyon) bir KAPIDIR — tamamlanıp incelenmeden 4.2+ refactor adımları YAZILMAYACAK/UYGULANMAYACAK.

**Goal:** Uzak OnExecute görevini (Dapr → Execution) **açık DB transaction'ı tutmadan** senkron çalıştırmak; DB bağlantısı 30–60 sn'lik ağ round-trip'i boyunca havuzdan pinli kalmasın. Transaction yalnızca state-mutasyon adımlarını kapsasın.

**Architecture (mevcut):** `WorkflowExecutionService.ExecuteAsync → TransitionRunner.RunAsync` (Polly retry → `RequiresNew` UoW açar) `→ core.ExecuteTransitionCoreAsync` (`WorkflowExecutionService`) `→ TransitionPipeline.RunAsync` (lock al + busy + `RunChainAsync`: `TransitionExecutor.ExecuteOneAsync` step döngüsü + post-commit + auto-chain continuation). `RunOnExecuteTasksStep` (order 30) hem uzak `ITaskCoordinatorExtended` çağrısı yapar hem `instanceRepository.UpdateAsync(saveChanges:true)` ile aynı transaction içinde persist eder. Tüm auto-chain TEK transaction'da. → DB bağlantısı uzak çağrı boyunca pinli.

**Hedef commit sınırları:**
| Faz | Adımlar | UoW |
|---|---|---|
| Pre-guard | SetBusy(19), CreateTransitionRecord(20) | kısa `RequiresNew` → commit, bağlantı bırak |
| Remote | ResourceLock(25), RunOnExecuteTasks(30) | **UoW YOK** — Dapr/HTTP bağlantı tutmadan |
| Persist | ChangeState(50)…Finalize(110) + OnExecute task sonucu yazımı | tek `RequiresNew` → atomik commit |
| Post-commit | job enqueue / event publish | mevcut transaction-dışı yol |

**Tech Stack:** .NET 10, Aether UoW (`IUnitOfWorkManager`, explicit `BeginTransactionAsync`), Polly (Faz 2 retry — KORUNACAK), xUnit + NSubstitute + Shouldly. Branch: `feature/phase4-transaction-boundary` (Faz 2+3 dahil).

**Spec kaynağı:** [2026-06-19-vnext-load-test-remediation.md](2026-06-19-vnext-load-test-remediation.md) Faz 4. Async yol (`AsyncTransitionStrategy`) zaten ayrı kısa UoW disiplinine sahip — referans.

## KARAR (2026-06-19, kullanıcı onaylı): Seçenek B — Adım-bazlı kısa UoW

Bulgu: `OnExecute(30)/OnExit(40)/OnEntry(60)` üçü de aynı kalıp — `taskCoordinator.ExecuteWithDetailsAsync` (uzak çağrı) + sonra `instanceRepository.UpdateAsync(instance, saveChanges:true)`. `TaskCoordinator` görevleri **kendi child DI scope'larında** çalıştırıp `InstanceTask` sonuçlarını orada yazıyor — yani görev sonuçları zaten ana transaction'da DEĞİL. Ana transaction yalnızca **Instance aggregate yazımlarını** (SetBusy, record, per-step instance UpdateAsync, ChangeState) tutuyor ve uzak çağrı boyunca **boşta-ama-açık** kalıyor → pinli bağlantı.

**Seçilen tasarım (B):** TransitionRunner'daki tek büyük `RequiresNew` UoW KALDIRILIR. Her adım kendi kısa UoW'unu yönetir; hiçbir `taskCoordinator` (uzak) çağrısı boyunca transaction açık kalmaz.

| Adım | Yapı |
|---|---|
| SetBusy(19), CreateTransitionRecord(20) | kısa UoW → erken commit (busy guard + record durable) |
| ResourceLock(25) | script-lock; ana transaction yok |
| OnExecute(30), OnExit(40), OnEntry(60) | uzak görev **transaction'sız** → instance yazımı **kendi kısa UoW'unda** commit → bağlantı bırakılır |
| ChangeState(50) | kısa UoW → commit |
| Finalize(110) | kısa UoW → record tamamla |

Çapraz-adım atomiklik yerine: erken transition record + `successfulTaskIds` idempotency + ChainReaper ile kurtarma (async yol felsefesi).

### B'nin getirdiği ve Task 4.1'in çözmesi gereken 3 ETKİLEŞİM
1. **Faz 2 retry çakışması (kritik):** Bir adım commit edildikten SONRA transient hata gelirse, `TransitionRunner.RunAsync`'i baştan retry etmek commit edilmiş adımları yeniden çalıştırır. → retry seam'i adım-bazına inmeli VEYA `ResumePointStepOrder`/`RequestResumeFrom` resume mekanizmasına dayanmalı. Mevcut resume desteği doğrulanacak.
2. **Kısmi ilerleme kurtarma:** Adım commit'i sonrası crash'te instance Busy + record var ama state yarım. `ResumePointStepOrder` persist ediliyor mu, ChainReaper/resume nasıl topluyor?
3. **Busy + lock + crash:** Erken Busy işareti crash'te takılı kalmasın (busy-timeout/sweeper teyidi). Dağıtık lock korunur (DB transaction'ından bağımsız).

---

## Kritik İlkeler (her görevde)
- **Atomiklik korunur:** state change + OnEntry + transition record + OnExecute task sonucu **aynı (Persist) UoW**'da commit edilmeli.
- **Idempotency:** Remote faz tekrar/duplicate'e dayanıklı olmalı (Faz 2 retry + ChainReaper backstop + `CreateTransitionRecordStep` duplicate-key guard ile uyumlu).
- **Faz 2 retry'ı bozma:** `TransitionRunner` retry sarımı ve `IsRetriableTransient` davranışı korunmalı; yeni UoW sınırları retry-güvenli olmalı.
- **Error boundary** değerlendirmesi Persist UoW içinde çalışmalı.
- **Lock semantiği:** TransitionPipeline'ın aldığı dağıtık kilit ve busy mark davranışı korunmalı.

---

## Task 4.1 — Tasarım + Karakterizasyon (KAPI; production edit YOK)

**Amaç:** Mevcut UoW sahipliğini ve commit sınırlarını TAM haritalandır; refactor öncesi davranışı sabitleyen testler yaz. Çıktı bir tasarım notu + yeşil karakterizasyon testleri.

**Files:**
- Create (doc): `ai-docs/phase4-transaction-boundary-design.md` (tasarım notu — gitignored olabilir; o zaman `docs/` altına da bir özet)
- Create (test): `test/BBT.Workflow.Application.Tests/Execution/Transitions/TransactionBoundaryCharacterizationTests.cs`
- Read (edit YOK): `TransitionRunner.cs`, `WorkflowExecutionService.cs`, `TransitionPipeline.cs`, `TransitionExecutor.cs`, steps: `SetBusyStep`, `CreateTransitionRecordStep`, `ResourceLockStep`, `RunOnExecuteTasksStep`, `ChangeStateStep`, `FinalizeTransitionStep`; ayrıca Aether `EfCoreTransactionSource.cs` ve `UnitOfWorkManager`.

**Tasarım notunda cevaplanacak sorular:**
- [ ] Açık transaction TAM olarak nerede açılıyor/commit ediliyor? (`TransitionRunner` UoW `BeginAsync` → Aether `EfCoreTransactionSource.CreateTransactionAsync` → `BeginTransactionAsync`; commit `TransitionRunner.uow.CommitAsync`.) Bağlantı ne zaman fiziksel olarak açılıyor (eager mı)?
- [ ] Auto-chain (RunChainAsync döngüsü + inline continuation) TEK transaction'ı mı paylaşıyor, yoksa her hop kendi UoW'unu mu alıyor? Lock ve busy mark transaction'a göre nerede?
- [ ] Hangi adımlar `saveChanges:true`/repository yazımı yapıyor ve ambient UoW'a mı güveniyor? (En az: SetBusy(19), CreateTransitionRecord(20, `InsertAsync saveChanges:true`), RunOnExecuteTasks(30, `instanceRepository.UpdateAsync saveChanges:true`), ChangeState(50), Finalize(110).)
- [ ] `RunOnExecuteTasksStep` uzak çağrıyı nasıl yapıyor (`ITaskCoordinatorExtended` → remote invoker, sync Dapr), ve sonucu/task kayıtlarını nereye yazıyor?
- [ ] Faz 2 retry sarımı (TransitionRunner) yeni sınırlarla nasıl etkileşir?
- [ ] **Önerilen kesim noktası:** Seçenek A'yı bu mimariye nasıl oturtacağız? (örn. transaction'ı core içinde adım gruplarına böl; remote fazı transaction dışına al; Pre-guard ve Persist için ayrı kısa UoW'lar — async yol nasıl yapıyorsa benzeri.) Somut bir hedef tasarım + dokunulacak metotlar listesi.
- [ ] Riskler + idempotency invariant'ları + geri-alma stratejisi.

**Karakterizasyon testleri (mevcut davranışı KİLİTLE — refactor sonrası da geçmeli):**
- [ ] **Step 1:** Bir transition'ın başarılı akışında state change + transition record + OnExecute task sonucunun **birlikte** kalıcı olduğunu doğrulayan test(ler). (Mevcut altyapı test desenleri: `TransitionPipelineTests`, `*StepTests`, `AsyncTransitionStrategyTests`.)
- [ ] **Step 2:** Persist sırasında bir hata olduğunda hiçbir kısmi state'in kalıcı olmadığını (rollback/fault) doğrulayan test.
- [ ] **Step 3:** (mümkünse) OnExecute remote çağrısının transition akışında çağrıldığını doğrulayan test (mevcut davranış: transaction içinde).
- [ ] **Step 4:** Testleri çalıştır, YEŞİL gör (bunlar mevcut davranışı anlatır).
- [ ] **Step 5:** Commit — `test(phase4): characterization tests locking current transition commit boundaries` + tasarım notu.

**Kabul:** Tasarım notu yukarıdaki tüm soruları somut dosya/satır referanslarıyla cevaplıyor; karakterizasyon testleri yeşil; **hiçbir production kodu değişmedi** (`git show --stat` yalnızca test + doc).

> **DURDUR:** Task 4.1 incelemesi (spec + quality) tamamlanmadan 4.2+ adımları AÇILMAYACAK. Tasarım notu, 4.2–4.4'ün bite-sized adımlarını yazmak için kullanılacak (controller tarafından).

---

## Task 4.2 — (SPEC; 4.1 sonrası detaylandırılacak) Remote fazı transaction dışına al
Hedef: ResourceLock(25) + RunOnExecuteTasks(30) açık transaction TUTMADAN çalışsın. Pre-guard adımları (SetBusy, CreateTransitionRecord) kendi kısa UoW'unda commit edilsin; Persist adımları (ChangeState…Finalize) + OnExecute task sonucu tek UoW'da. Bite-sized adımlar 4.1 tasarım notundan yazılacak. Faz 2 retry + lock + busy + error boundary korunacak.

## Task 4.3 — (SPEC) Error boundary + idempotency doğrulama
Error boundary değerlendirmesinin Persist UoW içinde çalıştığını doğrula/ayarla; remote faz idempotent (duplicate task execution'a karşı `successfulTaskIds`/`GetSuccessfulTaskIdsAsync` mekanizması korunur). Karakterizasyon testleri + yeni sınır testleri yeşil.

## Task 4.4 — (SPEC) Doğrulama
Build + tüm karakterizasyon/pipeline testleri yeşil (regresyon yok); yeni test: RunOnExecuteTasks sırasında **açık transaction/connection tutulmadığı**. Mümkünse yük/duman testiyle pool baskısının düştüğü gözlemi (Faz 1 metrikleri varsa).

---

## Self-Review Notları
- 4.1 KAPI: production edit yok; refactor planı 4.1 çıktısına dayanır (placeholder yazmaktan kaçınmak için).
- Atomiklik, idempotency, Faz 2 retry korunması ve lock/busy semantiği her adımda invariant.
- Async yol (`AsyncTransitionStrategy`) referans tasarım — sync yolu onun UoW disiplinine yaklaştırıyoruz.
