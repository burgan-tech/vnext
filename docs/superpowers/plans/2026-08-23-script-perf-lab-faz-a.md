# Faz A — script-perf-lab Makro Baseline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** vnext-example'a script-ağırlıklı `script-perf-lab` akışını eklemek ve Katman 0 metrikleriyle lokal runtime üzerinde soğuk/sıcak **makro baseline** almak (Katman 1-3 optimizasyonlarının gerçek-yük referansı + 21 executor ctor değişikliğinin DI smoke'u).

**Architecture:** Tek workflow: `perf-initial → perf-stage-1..10` (her stage'e girişte bir ScriptTask instance data'ya parametrik chunk merge eder — B9 O(n²) profili) `→ perf-fanout` (FanOutTask type 21, HTTP child → MockLab — B6 branch klonu) `→ perf-done`. Workflow 2 helper'lı `scripts.helpers` bildirir (A7 yolu). Üretici script (`build-script-perf-lab.py`, script-race-lab emsali) nonce'lu versiyonla JSON'ları üretir. Yük scripti `perf-load.py` soğuk/sıcak fazları koşar, `/metrics`'ten script_* snapshot'ı alır.

**Tech Stack:** vnext-example component JSON + .csx (IMapping/IFanOutMapping/IConditionMapping), Python 3 stdlib, xUnit + VNext.Testing.Sdk 0.0.6 (`WorkflowTestBase`), dotnet-counters, prometheus-net `/metrics` endpoint'leri.

**Spec:** `docs/superpowers/specs/2026-08-23-script-perf-katman0-design.md` §3 (onaylı — Faz A bu bölümün implementasyonudur)
**Repolar:** vnext-example `/Users/U0B006/Documents/repos/burgan-tech/vnext-example` (Task 1-3), vnext `/Users/U0B006/Documents/repos/burgan-tech/vnext` branch `feature/script-perf-katman0` (Task 4-5 koşum + sonuç).

**Kritik bilinenler (implementer bunları İHLAL EDEMEZ):**
- `definitions/publish` aynı key+version'ı içerik değişse de **409** ile reddeder → üreticide versiyon nonce'a bağlı (`1.0.<nonce>`), soğuk ölçüm İLK dokunuşta yapılır.
- Script'lerin kendi `using`'leri derlemede `DefaultUsings` + helper namespace'leriyle DEĞİŞTİRİLİR; `System.Text` o sette YOK → **.csx'lerde StringBuilder KULLANMA** (CS0246). `System.Linq` var.
- Aynı transition içinde aynı task TANIMI iki kez → ikincisi sessizce atlanır (journal `(TransitionId, TaskId)`). Stage'ler ayrı transition'lardan girildiği için tek paylaşılan ScriptTask tanımı güvenlidir.
- Publish sırası: helpers → tasks → workflow (referanslar publish anında çözülür), sonra `definitions/re-initialize`.
- MockLab route'ları PREFIX ile eşler; mevcut `fan-out-documents-collection.json` mock'u (`api/fan-out/documents/process`, `DOC-FAIL*` → 500, `DOC-SLOW` yavaş varyant) aynen yeniden kullanılır — yeni seed YAZILMAZ.
- TEST-SCENARIOS.md satırı senaryoyla **AYNI commit'te** (CLAUDE.local.md kuralı).
- vnext-example working tree'sine dokunmadan önce `git status` temiz olmalı; mevcut branch `feature/caller-role-provider` — İŞ YENİ BRANCH'TE yapılır, mevcut branch'e commit ATILMAZ.

---

## Task 1: Bileşenler + üretici + README + TEST-SCENARIOS satırı (vnext-example)

**Repo:** vnext-example. Önce branch:

- [ ] **Step 0: Branch aç**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext-example && git status --short
```
Temiz değilse DUR ve raporla. Temizse:
```bash
git switch -c feature/script-perf-lab master
```

**Files (hepsi yeni):**
- `core/Workflows/script-perf-lab/build-script-perf-lab.py` (üretici)
- `core/Workflows/script-perf-lab/src/{AlwaysTrueRule.csx, StageMapping{1..N}.csx → üretici yazar, FanOutItemMapping.csx}`
- `core/Workflows/script-perf-lab/script-perf-lab.json` (üretici çıktısı)
- `core/Workflows/script-perf-lab/README.md`
- `core/Mappings/script-perf-lab/{perf-chunk-helper.json, perf-stamp-helper.json, src/{PerfChunkHelper.csx, PerfStampHelper.csx}}` (json'lar üretici çıktısı)
- `core/Tasks/script-perf-lab/{script-perf-task.json, script-perf-fanout-task.json, perf-item-http-task.json}` (üretici çıktısı)
- Modify: `TEST-SCENARIOS.md` (satır ekle — aynı commit)

- [ ] **Step 1: Helper kaynakları**

`core/Mappings/script-perf-lab/src/PerfChunkHelper.csx`:

```csharp
using System;
using System.Linq;

namespace Perf.Helpers;

/// <summary>
/// script-perf-lab: deterministik, istenen boyutta chunk üretir. Amaç instance dokümanını
/// stage başına parametrik büyütmek (B9 append profili). StringBuilder bilinçli yok —
/// script derlemesinde System.Text using'i mevcut değil.
/// </summary>
public static class PerfChunkHelper
{
    public static string Build(int stage, int kb)
    {
        var unit = "s" + stage + "-0123456789abcdefghijklmnopqrstuvwxyz-";
        var repeat = (kb * 1024) / unit.Length + 1;
        return string.Concat(Enumerable.Repeat(unit, repeat)).Substring(0, Math.Max(1, kb * 1024));
    }
}
```

`core/Mappings/script-perf-lab/src/PerfStampHelper.csx`:

```csharp
using System;

namespace Perf.Helpers;

/// <summary>İkinci helper — helper-set'in çok üyeli (A7) yolunu tetiklemek için var.</summary>
public static class PerfStampHelper
{
    public static string Stage(int stage, string instanceId) =>
        "perf:" + stage + ":" + (instanceId ?? "none");
}
```

- [ ] **Step 2: Workflow .csx kaynakları**

`src/AlwaysTrueRule.csx`: chain-busy'ninkini aynen kopyala:
```bash
cp core/Workflows/chain-busy/src/AlwaysTrueRule.csx core/Workflows/script-perf-lab/src/
```

`src/FanOutItemMapping.csx` (fan-out-documents'ın `FanOutDocumentsMapping.csx` deseni — URL'i mevcut mock'a yönlendirir):

```csharp
using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// script-perf-lab fan-out item input: her item için mevcut fan-out-documents MockLab
/// mock'una (api/fan-out/documents/process) yönlendirir. Output handler bilinçli yok —
/// runtime'ın default paketlemesi (resultKey satırları + Summary) yeterli.
/// </summary>
public class FanOutItemMapping : ScriptBase, IFanOutMapping
{
    public Task<ScriptResponse> ItemInputHandler(WorkflowTask task, ScriptContext context, FanOutItem item)
    {
        var httpTask = task as HttpTask;
        if (httpTask != null)
        {
            var apiBaseUrl = GetConfigValue("Example:ApiBaseUrl", "http://localhost:3001");
            var url = httpTask.Url.Replace("API_BASEURL", apiBaseUrl);
            var documentId = item.ItemKey ?? item.Index.ToString();
            httpTask.SetUrl(url + "?documentId=" + Uri.EscapeDataString(documentId));
        }
        return Task.FromResult(new ScriptResponse());
    }
}
```

Üretici, stage mapping'lerini şu şablondan N kez yazar (`__N__` yerine 1..N) — `LeafUpdateDataMapping.csx` delta-merge sözleşmesi:

```csharp
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using Perf.Helpers;

/// <summary>
/// Stage __N__: instance data'ya chunkKb boyutunda deterministik chunk merge eder (delta-only).
/// chunkKb start body'den okunur; helper'lar (A7) chunk + stamp üretir.
/// </summary>
public class StageMapping__N__ : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var inst = context.Instance.Data as IDictionary<string, object>;
        var chunkKb = 4;
        if (inst != null && inst.TryGetValue("chunkKb", out var raw) && raw != null)
        {
            int.TryParse(raw.ToString(), out chunkKb);
        }

        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        var stage = (IDictionary<string, object>)new ExpandoObject();
        stage["stamp"] = PerfStampHelper.Stage(__N__, context.Instance.Id.ToString());
        stage["chunk"] = PerfChunkHelper.Build(__N__, chunkKb);
        target["stage__N__"] = stage;
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
```

Not: ayrı bir "seed" mapping'i gerekmiyor — `fanoutItems` start body'den gelir (`itemsPath: $.fanoutItems`).

- [ ] **Step 3: Üretici script**

`core/Workflows/script-perf-lab/build-script-perf-lab.py` — script-race-lab üreticisinin yapısını (ROOT/SRC/MAPPING_ROOT, `code()/ref()/label()/state()/auto()/envelope()` yardımcıları, `--nonce`/`--version` CLI'ı ve 409 uyarı yorumu DAHİL) kopyala ve şu farklarla uyarla:

1. Sabitler:
```python
WORKFLOW_KEY = "script-perf-lab"
CHUNK_HELPER = {"key": "perf-chunk-helper", "version": "1.0.0", "domain": "core", "flow": "sys-mappings"}
STAMP_HELPER = {"key": "perf-stamp-helper", "version": "1.0.0", "domain": "core", "flow": "sys-mappings"}
SCRIPT_TASK = {"key": "script-perf-task", "domain": "core", "version": "1.0.0", "flow": "sys-tasks"}
FANOUT_TASK = {"key": "script-perf-fanout-task", "domain": "core", "version": "1.0.0", "flow": "sys-tasks"}
```
2. CLI: `--nonce` (default 1), `--version` (default `1.0.<nonce>`), `--stages` (default 10), `--fanout-dop` (default 4), `--item-timeout` (default 20), `--batch-timeout` (default 120). Nonce her StageMapping kaynağının başına `// nonce: __NONCE__` yorumu olarak basılır (soğuk cache-key garantisi, race-lab deseni).
3. Stage mapping üretimi: yukarıdaki şablon `--stages` kez `src/StageMapping{N}.csx` olarak yazılır (`write_sources()` deseni).
4. Task bileşenleri (üretici `core/Tasks/script-perf-lab/` altına yazar):
   - `script-perf-task.json`: `"attributes": {"type": "7", "config": {}}` (fanout-stamp-before-task emsali; key/domain/flow/flowVersion/tags zarfı race-helper.json zarfıyla aynı kalıp, flow `sys-tasks`).
   - `perf-item-http-task.json`: fan-out-documents'ın child HTTP task'ı `core/Tasks/fan-out-documents/process-document-task.json`'ı OKU ve aynı şekle kendi key'inle (`perf-item-http-task`) kopyala (URL'de `API_BASEURL` + `api/fan-out/documents/process` kalır — mevcut mock'u kullanır).
   - `script-perf-fanout-task.json`: `fan-out-documents-task.json` şekli, config:
```json
{"mode": "inline", "itemsPath": "$.fanoutItems", "itemAlias": "item",
 "task": {"key": "perf-item-http-task", "domain": "core", "flow": "sys-tasks", "version": "1.0.0"},
 "execution": {"maxDegreeOfParallelism": <fanout-dop>, "itemTimeoutSeconds": <item-timeout>, "batchTimeoutSeconds": <batch-timeout>},
 "join": {"policy": "allSettled", "resultKey": "perfItemResults", "ordered": true}}
```
5. Workflow states (envelope `scripts={"helpers": [CHUNK_HELPER, STAMP_HELPER]}` ile — race-lab'de yalnız parent'a konması gibi):
   - `perf-initial` (stateType 1) → auto `auto-to-stage-1`.
   - `perf-stage-{N}` (stateType 2), N=1..stages: `on_entries=[task("StageMapping{N}.csx", task_def=SCRIPT_TASK)]`, auto → sonraki stage (son stage → `perf-fanout`). (Her stage ayrı transition'dan girilir → tek SCRIPT_TASK tanımı güvenli.)
   - `perf-fanout` (stateType 2): `on_entries=[{"order": 1, "task": FANOUT_TASK, "mapping": ref("FanOutItemMapping.csx")}]`, auto `auto-to-done` → `perf-done` (AlwaysTrueRule — başarı/kısmi ayrımı testte assert edilir, state dallanması yok).
   - `perf-done` (3,1), `perf-cancelled` (3,3). `cancel_target="perf-cancelled"`, `start_target="perf-initial"`.
6. Helper component json'ları race-lab `helper_component()` deseniyle (`encoding: "NAT"`, kod düz metin) `core/Mappings/script-perf-lab/` altına yazılır.
7. Tag'ler: `["integration-test", "script-perf-lab", "performance-baseline"]`.

- [ ] **Step 4: Üret + doğrula**

```bash
python3 core/Workflows/script-perf-lab/build-script-perf-lab.py --nonce 1
npm run validate 2>&1 | tail -15
```
Expected: üretilen dosya listesi + validate PASS. Validate script-perf-lab için hata verirse şema mesajına göre component JSON'unu düzelt (kural: `vnext.config.json` `allowUnknownProperties: false`); plugin şablonu ile runtime çelişirse **runtime davranışı esas** (CLAUDE.local.md §3).

- [ ] **Step 5: Senaryo README'si** — `core/Workflows/script-perf-lab/README.md`, CLAUDE.local.md zorunlu bölümleriyle:

- **Neyi denetliyor:** Script compiler + ScriptContext sıcak yollarının (compile-hit sabiti, helper set, instance-data append, FanOut branch klonu) yük altındaki maliyeti; Katman 0 metriklerinin (hit/miss, execution duration) gerçek akışta doğrulanması.
- **Neden var:** Katman 0 ölçüm altyapısının makro baseline'ı — Katman 1-3 optimizasyonlarının önce/sonra referansı (2026-08-23, vnext `feature/script-perf-katman0`; spec: vnext `docs/superpowers/specs/2026-08-23-script-perf-katman0-design.md`).
- **Akış şeması:** perf-initial → stage-1..10 (her girişte chunkKb merge — doküman lineer büyür, append maliyeti kareselleşir) → perf-fanout (N item × HTTP mock) → perf-done. Kritik adımlar: stage-10 (en büyük doküman üzerinde append) ve fanout (en büyük Body'nin item başına klonu).
- **Nasıl çalıştırılır:** integration test komutu + perf-load.py komutları (Task 2'deki README bölümünü referansla; ön koşullar: vnext altyapısı + 4 app + MockLab).
- **Beklenen sonuç / başarı kriteri:** integration test yeşil (perf-done, stage10 + perfItemResults dolu, helper stamp'leri var); yük koşusunda 0 Faulted; baseline tabloları bu README'nin "Sonuçlar" bölümüne işlenir.

- [ ] **Step 6: TEST-SCENARIOS.md satırı** (AYNI commit) — mevcut kolon düzeni ve üslupla (` · ` ayraçlı feature listesi, tarihli gerekçe, emoji durum):

```
| **script-perf-lab** | Script compile cache hit yolu (`CSharpEvaluator._typeCache`) · `scripts.helpers` çok üyeli helper set (A7) · instance-data append zinciri (`JsonData.Merge`/`NormalizedJson`, B9 O(n²) profili) · `FanOutTask` inline branch klonu (`CreateParallelBranch`, B6) · Katman 0 metrikleri (`script_compilations_total{result}`, `script_execution_duration_seconds{script_type}`) | Katman 0 ölçüm altyapısının makro baseline'ı — Katman 1-3 compiler/serialization optimizasyonlarının gerçek-yük önce/sonra referansı (2026-08-23, vnext `feature/script-perf-katman0`) | `Tests/ScriptPerfLab` (1 test) | `api-tests/script-perf-lab/perf-load.py` (soğuk/sıcak faz + p50/p95/p99 + /metrics snapshot) | 🆕 Baseline bekliyor |
```

- [ ] **Step 7: Commit**

```bash
git add core/Workflows/script-perf-lab core/Mappings/script-perf-lab core/Tasks/script-perf-lab TEST-SCENARIOS.md
git commit -m "feat(script-perf-lab): script-heavy baseline flow with helpers, growing data and fan-out

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 2: publish.py + perf-load.py + api-tests README (vnext-example)

**Files (yeni):** `api-tests/script-perf-lab/{publish.py, perf-load.py, README.md}`

- [ ] **Step 1: publish.py** — script-race-lab `publish.py`'ını kopyala, `COMPONENTS` listesini değiştir (SIRA: helpers → tasks → workflow):

```python
COMPONENTS = [
    REPO / "core" / "Mappings" / "script-perf-lab" / "perf-chunk-helper.json",
    REPO / "core" / "Mappings" / "script-perf-lab" / "perf-stamp-helper.json",
    REPO / "core" / "Tasks" / "script-perf-lab" / "script-perf-task.json",
    REPO / "core" / "Tasks" / "script-perf-lab" / "perf-item-http-task.json",
    REPO / "core" / "Tasks" / "script-perf-lab" / "script-perf-fanout-task.json",
    REPO / "core" / "Workflows" / "script-perf-lab" / "script-perf-lab.json",
]
```
Docstring'i senaryoya uyarla (sıra gerekçesi + "integration suite bunu kendisi yapar" notu korunur).

- [ ] **Step 2: perf-load.py** — `race-load.py`'ın iskeletini (http/start_one/settle_one/incident_text/ThreadPoolExecutor akışı, TERMINAL={"C","F","P"}, state function polling) temel al; farklar:

1. Argümanlar (fanout-load.py stiliyle): `--base-url` (default `http://localhost:4201`), `--parallel` (default 20), `--iterations` (default 3 — sıcak faz tur sayısı), `--payload-kb` (default 4 → start body `chunkKb`), `--fanout-count` (default 25), `--timeout` (default 300), `--publish`, `--skip-cold`.
2. Start body:
```python
body = {"testId": "perf-%d-%s" % (index, uuid.uuid4().hex[:8]),
        "chunkKb": args.payload_kb,
        "fanoutItems": [{"id": "DOC-%03d" % i} for i in range(args.fanout_count)]}
```
3. **Soğuk faz** (`--skip-cold` verilmedikçe): tek instance başlat, settle et, `coldLatencyS` raporla; publish yeni nonce ile yapılmadıysa uyarı bas: "soğuk faz ancak taze nonce'la anlamlı — bkz. üretici --nonce".
4. **Sıcak faz:** `--iterations` tur × `--parallel` instance; tur başına latency listesi topla; sonda `statistics.quantiles` ile p50/p95/p99 + durum sayımları (C/F/TIMEOUT) + Faulted'larda `incident_text`.
5. **Metrics snapshot:** sıcak fazdan önce ve sonra `GET {base}/metrics` (orchestration) ve `GET {base.replace(':4201', ':4202')}/metrics` (execution) çekip `script_` ile başlayan satırları `api-tests/script-perf-lab/results/metrics-{before|after}-{timestamp}.txt`'ye yaz (dizini oluştur; `results/` .gitignore'a eklenmez — sonuç dosyaları commit edilebilir, timestamp'lidir). Delta özetini stdout'a bas: `script_compilations_total` hit/miss toplamları, `script_execution_duration_seconds_count` scriptType kırılımı.
6. **Başarısızlık eşiği** (README'ye de yazılır): herhangi bir `F` → FAIL (exit 1); TIMEOUT oranı > %5 → FAIL; aksi rapor-only.

- [ ] **Step 3: api-tests README.md** — bağımlılık (Python 3 stdlib, çalışan lokal stack), komutlar parametreleriyle:

```bash
python3 api-tests/script-perf-lab/perf-load.py --publish --parallel 20 --iterations 3 --payload-kb 4 --fanout-count 25
```
Neyi ölçtüğü (soğuk compile + sıcak p50/p95/p99 + metrics delta), eşikler (0 Faulted; TIMEOUT ≤ %5), sonucun okunması (hit-ratio ≈ 1'e yakınsamalı; `script_execution_duration` scriptType kırılımı; sonuç dosyaları `results/`).

- [ ] **Step 4: Smoke (statik) + commit** — `python3 -m py_compile api-tests/script-perf-lab/*.py` temiz; commit:
```bash
git add api-tests/script-perf-lab && git commit -m "feat(script-perf-lab): publish + load/baseline scripts with metrics snapshot

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 3: Integration test (vnext-example)

**Files:** Create `tests/Core.IntegrationTests/Tests/ScriptPerfLab/ScriptPerfLabTests.cs`

- [ ] **Step 1: Test sınıfı** — `FanOutDocumentsTests`'in `WorkflowTestBase` desenini izle:

```csharp
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;
using Xunit;

namespace Core.IntegrationTests.Tests.ScriptPerfLab;

/// <summary>
/// script-perf-lab akışının uçtan uca doğruluğunu pinler: 10 stage'in chunk merge'leri,
/// helper çözümü (stamp), fan-out sonuç seti ve Completed'a ulaşma. PERF ASSERT ETMEZ —
/// sayılar api-tests/script-perf-lab/perf-load.py + README'nin işidir.
/// </summary>
public class ScriptPerfLabTests : WorkflowTestBase
{
    private const string Workflow = "script-perf-lab";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(180);

    public ScriptPerfLabTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task TenStages_WithHelpersAndFanOut_ReachDoneWithFullDataset()
    {
        var items = Enumerable.Range(0, 3).Select(i => new { id = $"DOC-{i:000}" }).ToArray();
        var instanceId = await StartAsync(Workflow, new { testId = $"it-{Guid.NewGuid():N}", chunkKb = 2, fanoutItems = items });

        await WaitForInstanceStateAsync(Workflow, instanceId, "perf-done", timeout: Budget);

        var attributes = await GetAttributesAsync(Workflow, instanceId);

        // 10 stage'in hepsi merge edilmiş ve helper stamp'i çözülmüş olmalı.
        for (var stage = 1; stage <= 10; stage++)
        {
            var node = attributes.GetProperty($"stage{stage}");
            Assert.StartsWith($"perf:{stage}:", node.GetProperty("stamp").GetString());
            Assert.True(node.GetProperty("chunk").GetString()!.Length >= 2 * 1024,
                $"stage{stage} chunk beklenen boyutta değil");
        }

        // Fan-out default paketlemesi: resultKey satırları + Summary.
        var results = attributes.GetProperty("perfItemResults").EnumerateArray().ToArray();
        Assert.Equal(3, results.Length);
        Assert.All(results, row => Assert.True(row.GetProperty("isSuccess").GetBoolean()));
        var summary = attributes.GetProperty("perfItemResultsSummary");
        Assert.Equal(3, summary.GetProperty("succeeded").GetInt32());
        Assert.Equal(0, summary.GetProperty("failed").GetInt32());
    }
}
```

Derleme uyumu: `WorkflowTestBase`'in gerçek üye adlarını (`StartAsync`, `WaitForInstanceStateAsync`, `GetAttributesAsync`) dosyadan doğrula — `tests/Core.IntegrationTests/Infrastructure/WorkflowTestBase.cs`; usings'i komşu FanOut testinden kopyala. Not: default output paketlemesinin alan adları (`isSuccess`, `succeeded`, `failed`) `FanOutDocumentsTests`'te pinli — aynı okuma desenini kullan.

- [ ] **Step 2: Derle** — `cd tests/Core.IntegrationTests && dotnet build 2>&1 | tail -3` → 0 error. (KOŞMA — koşum Task 4'te, lokal stack ayaktayken.)

- [ ] **Step 3: Commit**

```bash
git add tests/Core.IntegrationTests/Tests/ScriptPerfLab && git commit -m "test(script-perf-lab): end-to-end integration test for the baseline flow

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 4: Koşum — DI smoke + integration + makro baseline (İKİ repo, KULLANICI MAKİNESİNDE ağır adım)

> Bu task altyapı + 4 app + MockLab ayağa kaldırır. CLAUDE.local.md: mevcut durumu önce KONTROL ET, ayakta olanı yeniden başlatma. App'ler vnext reposunda `feature/script-perf-katman0` branch'inden, **mutlaka `--launch-profile http`** ile.

- [ ] **Step 1: Altyapı + MockLab kontrol/başlat**

```bash
docker ps --format '{{.Names}}' | sort
```
vnext altyapı container'ları (postgres/redis/dapr placement vb.) yoksa: `cd /Users/U0B006/Documents/repos/burgan-tech/vnext/etc/docker && ./run-docker.sh`. `mocklab` yoksa: `cd /Users/U0B006/Documents/repos/burgan-tech/vnext-example && docker compose up -d mocklab`.
Migration: Katman 0 şema değişikliği içermiyor → DbMigrator GEREKMEZ (yeni migration çıkarsa `--launch-profile DbMigrator` ile bir kez).

- [ ] **Step 2: 4 app'i başlat** (ayrı terminaller / arka plan süreçleri; vnext repo kökünden):

```bash
dotnet run --project orchestration/BBT.Workflow.Orchestration.HttpApi.Host --launch-profile http
```
```bash
dotnet run --project execution/BBT.Workflow.Execution.HttpApi.Host --launch-profile http
```
```bash
dotnet run --project workers/BBT.Workflow.Workers.Inbox --launch-profile http
```
```bash
dotnet run --project workers/BBT.Workflow.Workers.Outbox --launch-profile http
```
**DI smoke kontrolü:** dördü de hatasız ayakta + `curl -s http://localhost:4201/health` OK + `curl -s http://localhost:4201/metrics | grep -c script_` > 0. Bir app ctor/DI hatasıyla düşerse bu Katman 0 regresyonudur → DUR, hatayı raporla.

- [ ] **Step 3: Publish + integration test**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext-example
python3 api-tests/script-perf-lab/publish.py
VNEXT_BASE_URL=http://localhost:4201 dotnet test tests/Core.IntegrationTests --filter "FullyQualifiedName~ScriptPerfLabTests" 2>&1 | tail -5
```
Expected: publish 6/6 + re-initialize ok; test 1/1 PASS. (SDK `EnableDomainPublish` tüm domain'i ayrıca publish eder — zararsız, 409'lar beklenir.) FAIL ise incident'ı `perf-load.py`'daki `incident_text` deseniyle oku, kök nedeni raporla — SDK'da eksik yetenek görürsen kullanıcıya bildir, test tarafında workaround yazma (CLAUDE.local.md).

- [ ] **Step 4: SOĞUK ölçüm** — taze nonce ile yeniden üret + publish + ilk dokunuş:

```bash
python3 core/Workflows/script-perf-lab/build-script-perf-lab.py --nonce 2
python3 api-tests/script-perf-lab/perf-load.py --publish --parallel 1 --iterations 1 --payload-kb 4 --fanout-count 25 --timeout 300
```
`coldLatencyS` + metrics delta'da `result="miss"` sayısını kaydet (beklenen: stage mapping'ler + rule + fanout mapping ≈ 12-13 miss).

- [ ] **Step 5: SICAK yük + dotnet-counters**

Ayrı terminalde (PID'leri `dotnet-counters ps | grep -E "Orchestration|Execution"` ile bul):
```bash
dotnet-counters monitor -p <ORCH_PID> --counters System.Runtime --refresh-interval 5
```
(İkinci terminalde aynı komut `<EXEC_PID>` için.) Sonra:
```bash
python3 api-tests/script-perf-lab/perf-load.py --parallel 20 --iterations 3 --payload-kb 4 --fanout-count 25
```
Kaydet: alloc rate (MB/s), Gen0/1/2 sayıları, LOH size, % time in GC (koşu ortası temsili değerler + tepe), perf-load çıktısındaki p50/p95/p99 + durum sayımları + metrics delta (hit-ratio, scriptType kırılımlı execution count/duration). İkinci bir koşuyu `--payload-kb 16` ile tekrarla (append maliyetinin doküman boyutuyla kareselleşmesi baseline'da görünsün).

- [ ] **Step 6: Sonuçları senaryo README'sine işle** — "Sonuçlar (baseline, 2026-08-XX)" bölümü: koşum ortamı (makine, vnext commit SHA'sı), soğuk tablo, sıcak tablo (iki payload-kb için), dotnet-counters özeti, metrics delta özeti; `results/metrics-*.txt` dosyalarını commit et. TEST-SCENARIOS satırının Durum kolonunu güncelle: `✅ Aktif — 1/1 integration + baseline alındı (2026-08-XX, vnext <SHA>)`.

```bash
git add core/Workflows/script-perf-lab/README.md TEST-SCENARIOS.md api-tests/script-perf-lab/results
git commit -m "docs(script-perf-lab): macro baseline results (cold/warm, counters, metrics delta)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 5: vnext tarafına kapanış

**Repo:** vnext, branch `feature/script-perf-katman0`.

- [ ] **Step 1:** `test/BBT.Workflow.Benchmarks/baselines/2026-08-23-master.md`'ye kısa çapraz-link bölümü ekle: "Makro baseline: vnext-example `core/Workflows/script-perf-lab/README.md` § Sonuçlar (tarih + vnext-example commit SHA)". Plan durum tablosuna (`docs/superpowers/plans/2026-08-23-script-perf-katman0.md`) Faz A satırı ekle (✅ + iki repo commit SHA'ları). Spec §5'teki makro-baseline maddesi artık karşılanmış olur — işaretle.
- [ ] **Step 2:** Commit:
```bash
git add docs/superpowers test/BBT.Workflow.Benchmarks/baselines && git commit -m "docs(perf): link macro baseline results; mark Faz A complete

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```
- [ ] **Step 3:** Hafıza güncellemesi kontrolörün işi (memory: script-perf-analysis-2026-08 → Faz A tamam + sayılar + vnext-example branch adı).

---

## Başarı kriterleri (spec §3 + bu plan)

- [ ] `script-perf-lab` publish oluyor, integration test lokal runtime'da 1/1 yeşil (DI smoke dahil).
- [ ] Soğuk faz: taze nonce'la miss sayısı beklenen aralıkta; cold latency kayıtlı.
- [ ] Sıcak faz: 0 Faulted; p50/p95/p99 + iki payload-kb noktası; hit-ratio metrikten okunuyor.
- [ ] dotnet-counters (alloc/GC/LOH) her iki host için kayıtlı; metrics delta dosyaları commit'li.
- [ ] README + TEST-SCENARIOS güncel (aynı-commit kuralına uyuldu); vnext tarafı çapraz-linkli.

## Riskler

- FanOut inline modun HTTP child + 25 item × 20 paralel instance kombinasyonu MockLab'ı doyurabilir → item timeout'ları TIMEOUT değil `isSuccess=false` üretir; eşik ihlalinde `--fanout-count` düşürülüp koşum notu yazılır (parametre değişikliği baseline dokümanında açıkça belirtilir).
- `WorkflowTestBase` üye adları/imzaları SDK 0.0.6'ya göre yazıldı — derleme hatasında dosyadan uyarlanır; SDK'da eksik yetenek çıkarsa kullanıcıya bildirilir (workaround yok).
- Stage mapping'lerde `context.Instance.Data` her erişimde tam parse — bu bilinçli: ölçtüğümüz şeyin ta kendisi (B1/B3). Optimize edilmiş desen KULLANILMAZ.
