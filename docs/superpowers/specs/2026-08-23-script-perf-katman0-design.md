# Katman 0 — Script Performans Ölçüm Altyapısı (Design Spec)

**Tarih:** 2026-08-23 · **Durum:** Onaylandı (brainstorming oturumu) · **Baz analiz:** `ai-docs/script-perf-analysis-2026-08-23.md`

## Amaç

Script compiler ve ScriptContext performans çalışmasının (Katman 1–3) tüm optimizasyonlarının etkisini kanıtlanabilir kılmak. Bugün: script cache hit/miss oranı görünmüyor, compile histogramı hit'lerle kirli, `script_executions_total` compile path'inde artıyor (yanıltıcı isim), `RecordScriptExecutionDuration` / `RecordScriptRuntimeError` tanımlı ama sıfır caller. Benchmark projesi yok, makro baseline yok.

Katman 0 hiçbir optimizasyon içermez; yalnız ölçüm üretir.

## Alınan kararlar

| Karar | Seçim |
|---|---|
| Metrik stratejisi | **Koordineli düzeltme** — semantik düzeltilir, vnext + helm-charts dashboard'ları birlikte güncellenir; deprecation dönemi yok |
| Kapsam | **Mikro + makro baseline** — BenchmarkDotNet + metrik fix'leri + vnext-example'da yük altında baseline |
| Makro senaryo | **Yeni `script-perf-lab` akışı** (mevcut senaryolar değil) |
| Execution ölçüm noktası | **Huni noktaları** (proxy/decorator değil); hit/miss evaluator'dan raporlanır |
| Kapsam dışı | Optimizasyon (Katman 1+), span ekleme (kullanıcı kararı, bkz. memory `script-compile-cache-cold-cost`), IL cache / warmup |

---

## 1. Metrik seti ve kayıt noktaları

### 1.1 Evaluator sözleşme değişikliği

`IEvaluator.CompileToInstanceAsync<T>` sonucu hit/miss bilgisini taşır: sonuç tipi instance'a ek olarak `Compiled` (bool — bu çağrıda gerçek Roslyn derlemesi yapıldı mı) ve `CompileDuration` (yalnız `Compiled=true` iken dolu) alanlarını içerir. Evaluator'ın tek production çağıranı `ScriptEngine`'dir; plan aşamasında ikinci bir çağıran olmadığı doğrulanacak. Test double'ları güncellenir.

### 1.2 Metrik tablosu

| Metrik | Tip | Kayıt yeri | Label'lar | Not |
|---|---|---|---|---|
| `script_compilations_total` | counter | `ScriptEngine` | `result=hit\|miss`, `status=success\|error` | Yeni |
| `script_compilation_duration_seconds` | histogram | `ScriptEngine` | `status` | Mevcut metrik; artık **yalnız gerçek derlemede** (`Compiled=true`) kaydedilir |
| `script_executions_total` | counter | Huni noktaları | `scriptType`, `status` | **Semantik değişir**: compile path'inden çıkar, gerçek script çalıştırmasını sayar |
| `script_execution_duration_seconds` | histogram | Huni noktaları | `scriptType` | Ölü `RecordScriptExecutionDuration` bağlanır |
| `script_runtime_errors_total` | counter | Huni catch'leri | `scriptType` | Ölü `RecordScriptRuntimeError` bağlanır |
| `script_cache_entries` | gauge | Evaluator `_typeCache` boyutu | — | Yeni; evaluator sayıyı expose eder |

`scriptType` değerleri: `transition-mapping`, `task-input`, `task-output`, `condition`, `function`.

**Kardinalite kuralı:** mapping key / workflow key / instance id label olarak KULLANILMAZ. Label seti sabit ve küçüktür.

### 1.3 Huni noktaları (execution ölçümü)

| Huni | Kapsadığı | scriptType |
|---|---|---|
| Task executor ortak katmanı (input/output mapping invoke — `TaskExecutionEngine`/`TaskExecutorBase` seviyesinde tek nokta; kesin yeri plan belirler) | Tüm task input/output mapping'leri (~20 çağrı/10-task transition) | `task-input`, `task-output` |
| `TransitionDataMapper` | Transition mapping | `transition-mapping` |
| `ScriptConditionEvaluator` | Task condition'ları | `condition` |
| `FunctionAppService` | Function mapping/output handler | `function` |

Trafiğin ~%90+'ı kapsanır. Kapsanmayan uçlar (StateNotificationDispatcher, SubflowStarter/OutputMapping, ScriptTimerEvaluator, ResourceLockStep, EventAppService) bilinçli olarak Katman 0 dışıdır; aynı desenle sonradan eklenebilir. Ölçüm `try/finally` + `Stopwatch` ile yapılır; exception yolunda `script_runtime_errors_total` artırılır ve exception aynen yeniden fırlatılır (davranış değişmez).

### 1.4 Dashboard ve meta güncellemeleri

- vnext: `etc/docker/config/grafana/dashboards/workflow-metrics.json` — mevcut `rate(script_executions_total[5m])` paneli yeni semantiğe göre güncellenir; **cache hit-ratio paneli** eklenir (`script_compilations_total` üzerinden).
- vnext-helm-charts: `charts/vnext/templates/common/monitoring-config/grafana-dashboard-config.yaml` — aynı güncellemeler (vnext PR merge sonrası ayrı PR).
- vnext-meta: `migrations.json`'a metrik semantik değişikliği kaydı (eski sorgu → yeni sorgu eşlemesiyle).

### 1.5 Unit test kapsamı

- Fake/stub evaluator ile: hit'te `result=hit` sayacı artar ve compile histogramına örnek DÜŞMEZ; miss'te `result=miss` + histogram örneği düşer; compile hatasında `status=error`.
- Her hunide: başarılı çalıştırmada duration + `script_executions_total` kaydı; exception'da `script_runtime_errors_total` + exception'ın aynen yayılması.
- Gauge: cache'e ekleme sonrası boyutun yansıması.

---

## 2. BenchmarkDotNet projesi

- Yeni proje: `test/BBT.Workflow.Benchmarks` — console app, net10.0, BenchmarkDotNet + `MemoryDiagnoser`. `dotnet test`'e girmez; CI'da koşmaz. Manuel koşum: `dotnet run -c Release --project test/BBT.Workflow.Benchmarks -- --filter <suite>`.
- Ölçümler **public API üzerinden** yapılır (production'ın ödediği gerçek yol). `InternalsVisibleTo` başlangıçta eklenmez; ancak bir suite public API'den izole edilemezse plan aşamasında gerekçesiyle eklenir.

| Suite | Hedef | Parametreler |
|---|---|---|
| `CompileHitPath` | Sıcak cache'te `CompileToInstanceAsync` (A1+A3+A4 toplam hit bedeli) | script 1/4/16 KB × helper'lı(2)/helper'sız |
| `InstanceDataAccess` | `Instance.Data` / `InstanceData.Attributes` (B1–B3) | doküman 10/50/200 KB |
| `ParallelBranch` | `ScriptContext.CreateParallelBranch` (B6) | Body 10/50 KB × 10/100/500 item |
| `AppendPath` | `JsonData.Merge` + `NormalizedJson` + hash (B9) | doküman 10/50/200 KB, ardışık 10 append |
| `AuditSerialize` | Task tanımı + response audit serialize (B8) | orta boy task tanımı + 50 KB response |

- Baseline çıktısı: `test/BBT.Workflow.Benchmarks/baselines/<koşum-tarihi>-master.md` (BenchmarkDotNet markdown exporter; dosya adı koşum günü stamplenır) — commit edilir. Katman 1–3 aynı suite'lerle bu dosyaya karşı kıyaslanır; her katman kendi tarihli baseline dosyasını ekler.

---

## 3. script-perf-lab (vnext-example) ve makro baseline

### 3.1 Akış tasarımı

`vnext-example/core/Workflows/script-perf-lab/` — sıcak yolları bilinçli tetikleyen tek akış:

- ~10 script-ağırlıklı task'lı auto-transition zinciri (ScriptTask ağırlıklı; 1–2 MockLab HTTP çağrısı temsilî). → ~27 compile-API çağrısı/transition profili gerçek pipeline'da oluşur.
- 2 helper'lı helper set (`scripts.helpers`) → A7 yolu.
- Her task instance data'ya parametrik boyutta chunk merge eder → doküman transition zinciri boyunca büyür → B9 append O(n²) profili.
- FanOutTask state'i, item sayısı girdiden parametrik → B6 klon/LOH yolu.

Üretici script (`build-script-perf-lab.py`, chain-busy/script-race-lab konvansiyonu) versiyonu nonce'a bağlar (`--version 1.0.<nonce>`) — publish 409 tuzağına karşı.

### 3.2 Yük scripti

`api-tests/script-perf-lab/perf-load.py` (stdlib-only): `--base-url --parallel --iterations --payload-kb --fanout-count`; çıktı latency p50/p95/p99 + hata sayısı. README zorunlu bölümleri: neyi denetliyor, neden var (bu spec + analiz linki), akış şeması, nasıl koşulur (komut + parametre), başarısızlık eşiği, sonucun okunması. `TEST-SCENARIOS.md` satırı **aynı commit'te**.

### 3.3 Baseline prosedürü

1. Altyapı kontrolü (ayaktaysa yeniden başlatma yok); gerekirse DbMigrator (`--launch-profile DbMigrator`); 4 app `--launch-profile http` (Katman-0 branch'i ile derlenmiş lokal runtime — image DEĞİL).
2. Akışı taze nonce'lu versiyonla publish et.
3. **Soğuk ölçüm:** o nonce'a ilk dokunan koşu — cold compile süreleri + `result=miss` sayıları. (Metodoloji: sıcak cache'le alınan "soğuk" ölçüm geçersizdir — bkz. memory `script-alc-double-compile-race` dersi.)
4. **Sıcak yük:** `perf-load.py --parallel 20`; eşzamanlı `dotnet-counters monitor` (Orchestration + Execution PID'leri): alloc rate, Gen0/1/2 sayıları, LOH boyutu, time-in-GC.
5. Prometheus snapshot: hit/miss oranı, execution duration dağılımı, cache boyutu.
6. Sonuçlar: senaryo README'sine baseline tablosu; vnext `test/BBT.Workflow.Benchmarks/baselines/` ile çapraz link.

### 3.4 Integration test

`tests/Core.IntegrationTests/Tests/ScriptPerfLab/` — akışın uçtan uca `Completed`'a ulaştığını pinleyen minimal xUnit (fan-out dahil). **Perf assert etmez**; sayılar doküman konusudur. `test.runsettings` ile `VNEXT_BASE_URL=http://localhost:4201`.

---

## 4. Teslimatlar ve sıralama

| # | Repo | İçerik | Bağımlılık |
|---|---|---|---|
| 1 | vnext, branch `feature/script-perf-katman0` | Evaluator outcome değişikliği → engine metrik yeniden yazımı → huni wiring → gauge → dashboard JSON → unit testler → vnext-meta migrations notu → Benchmarks projesi → mikro baseline md | — |
| 2 | vnext-example | script-perf-lab + üretici + perf-load.py + README + TEST-SCENARIOS.md + integration test; makro baseline koşumu (1 lokalde çalışırken) | 1 |
| 3 | vnext-helm-charts | Dashboard config metrik güncellemesi | 1 merge + release |

Adım 3 için kullanıcıya release senkronizasyonu hatırlatılır (runtime'daki metrik değişikliğinin Helm karşılığı — CLAUDE.local.md kuralı).

## 5. Başarı kriterleri

- [ ] Hit/miss oranı Grafana'da görünür; compile histogramı yalnız gerçek derleme örnekleri içerir.
- [ ] `script_execution_duration_seconds` scriptType bazında dolu; `script_runtime_errors_total` çalışır durumda.
- [ ] 5 benchmark suite koşuyor; mikro baseline md commit'li.
- [ ] Makro baseline dokümanı: soğuk/sıcak ayrımıyla alloc-rate, GC, LOH, latency p50/p95/p99 ve hit-ratio sayıları kayıtlı.
- [ ] Mevcut davranış değişmedi: metrik wiring'i exception semantiğini ve pipeline akışını etkilemiyor (unit + integration testler yeşil).

## 6. Riskler

- `IEvaluator` imza değişikliğinin bilinmeyen bir çağıranı çıkarsa kapsam genişler → plan aşamasında çağıran taraması ilk görevdir.
- Metrik isim/semantik değişikliği, vnext ile helm-charts release'i arasındaki pencerede dashboard'ı kısmen boş bırakır → kabul edilen trade-off (Bölüm 1 kararı).
- Huni noktasına eklenen Stopwatch/try-finally'nin kendisi maliyet ekler → ihmal edilebilir (çağrı başına ~ns mertebesi, allocation'sız desen kullanılacak); benchmark'lar bunu da yakalar.
