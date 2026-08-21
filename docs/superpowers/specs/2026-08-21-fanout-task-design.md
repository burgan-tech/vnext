# FanOutTask (Type 21) — Dinamik Paralel Task Yürütme Tasarımı

**Tarih:** 2026-08-21
**Durum:** Onaylandı (brainstorming oturumu çıktısı)
**Kaynak konsept:** `paralel_task.md` (harici konsept dokümanı)

## 1. Problem ve Amaç

İki ayrı eksik tespit edildi:

- **A — Statik paralellik:** Tasarım zamanında bilinen N task'ın eşzamanlı koşması.
  **Zaten çalışıyor.** `TaskCoordinator.ExecuteTaskGroupInParallelAsync`
  (`src/BBT.Workflow.Application/Tasks/Coordinator/TaskCoordinator.cs:285`) aynı `Order`'daki
  task'ları `Task.WhenAll` ile, task başına ayrı DI scope ve
  `ScriptContext.CreateParallelBranch()/MergeParallelBranch()` izolasyonuyla paralel koşuyor.
  Konsept dokümanındaki "pratikte sıralı" tespiti güncel kod için geçersiz; Faz 0 kapalı.
- **B — Dinamik fan-out:** Runtime'da gelen koleksiyon üzerinden N adet aynı task'ın koşması
  (N önceden bilinmez). **Platformda yok.** Bu spec B'yi çözer.

Yol gösterici prensip: **paralellik Execution/executor düzleminin işidir; instance'a yazma tek
writer'ın işidir.** N item paralel işlenir, instance data'ya **1 kez** yazılır.

## 2. Karar Kaydı

| # | Karar | Sonuç |
|---|---|---|
| 1 | İlk teslimat kapsamı | **Faz 1 = yalnız `inline` mod.** `mode` alanı şemada baştan yer alır (default `"inline"`); `"durable"` rezerve, Faz 1'de validator reddeder. |
| 2 | Inner task türü kısıtı | **Kısıtsız.** Validator hiçbir task türünü reddetmez (Human/Timer dahil); sorumluluk akış tasarımcısında, riskler dokümante edilir. Tek istisna: nested fan-out (bkz. #5). |
| 3 | Join policy kapsamı | **Dördü birden Faz 1'de:** `all`, `allSettled`, `quorum`, `firstSuccess`. |
| 4 | Eşzamanlılık tavanı | **Task-level `maxDegreeOfParallelism` (default 4) + process-level global bulkhead** `Workflow:FanOut:MaxConcurrentItems` (appsettings, default 64). Efektif eşzamanlılık = min(batch maxDop, global kalan slot). |
| 5 | Nested fan-out | **Yasak, derinlik 1.** Inner task type `21` ise validator tanım zamanında reddeder. Gerekçe tercih değil zorunluluk: global bulkhead altında dış item'lar tüm slotları tutarken iç item'ların slot beklemesi deadlock üretir. |
| 6 | Item sonuç yazımı | **Tek patch.** Ara sonuç instance data'ya yazılmaz; tüm item'lar bitince `OutputHandler` bir kez çağrılır, tek InstanceData patch'i oluşur. Item bazlı audit izi item InstanceTask journal kayıtlarında (bkz. §8). |
| 7 | ItemsPath / ItemsSelector | **İkisi de desteklenir, tam olarak biri kullanılır.** İkisi birden yazılmışsa validator hatası — "path öncelikli, script sessizce yok sayılır" davranışı yanıltıcı olurdu. |

## 3. Bileşen Tanımı ve Config Şeması

- **Enum:** `TaskType.FanOut = 21` (`TaskEnums.cs` — 17/18 konsept dokümanının aksine dolu:
  `StateStore = 17`, `CacheAside = 18`; son değer `DaprConversation = 20`).
- **Polimorfizm:** `WorkflowTask`'a `[JsonDerivedType(typeof(FanOutTask), "21")]`.
- **Domain sınıfı:** `FanOutTask : WorkflowTask` —
  `src/BBT.Workflow.Domain/Definitions/Tasks/FanOutTask.cs`; `Configure(JsonElement)` ile
  fail-fast parse + `ITaskClonable` clone/reset (desen: `SubProcessTask.cs:250-301`, pooling
  için zorunlu).

```jsonc
{
  "key": "process-documents-parallel",
  "version": "1.0.0",
  "domain": "core",
  "flow": "sys-tasks",
  "flowVersion": "1.0.0",
  "tags": ["fan-out", "parallel"],
  "attributes": {
    "type": "21",
    "config": {
      "mode": "inline",                    // "durable" rezerve; Faz 1'de reddedilir
      "itemsPath": "$.documents",          // instance data üzerinde JSONPath (opsiyonel — yoksa ItemSelector)
      "itemAlias": "document",             // opsiyonel; default input binding ve log okunabilirliği
      "task": {                            // inner task referansı — SubProcessTask referans deseni
        "key": "process-single-document",
        "domain": "core",
        "flow": "sys-tasks",
        "version": "1.0.0"
      },
      "execution": {
        "maxDegreeOfParallelism": 4,       // default 4, zorunlu alt sınır 1
        "itemTimeoutSeconds": 30,
        "batchTimeoutSeconds": 120
      },
      "join": {
        "policy": "allSettled",            // all | allSettled | quorum | firstSuccess
        "minSuccess": 8,                   // yalnız quorum'da anlamlı
        "resultKey": "documentResults",    // default output'un yazılacağı data anahtarı
        "ordered": true                    // sonuç dizisi item index sırasını korur (default true)
      },
      "errorBoundary": {                   // PER-ITEM retry/aksiyon kuralları
        "onError": [
          { "action": 1, "errorCodes": ["Task:503", "Task:429"], "priority": 1,
            "retryPolicy": { "maxRetries": 3, "initialDelay": "PT1S", "backoffType": 1, "useJitter": true } },
          { "action": 5, "errorCodes": ["*"], "priority": 999, "logOnly": true }
        ]
      }
    }
  }
}
```

**Global bulkhead config:**

```jsonc
// appsettings
"Workflow": { "FanOut": { "MaxConcurrentItems": 64 } }
```

Process genelinde tek `SemaphoreSlim`; Orchestration tarafında uygulanır. Helm chart'larında
karşılık env değişkeni eklenmelidir (CLAUDE.local.md kuralı: yeni zorunlu config'in Helm
karşılığı kontrol edilir — default'u olduğu için zorunlu değil, ama ayarlanabilir olmalı).

## 4. Yürütme Akışı (inline)

`FanOutTaskExecutor : TaskExecutorBase<FanOutTask>` — **orchestration-local** executor
(`src/BBT.Workflow.Application/Tasks/Executors/FanOut/`). Execution servisinde invoker
**gerekmez**: FanOut task'ın kendisi in-process çalışır; remote inner türler (Http, Soap,
Dapr*, StateStore) zaten kendi executor'ları üzerinden `IRemoteInvokerService` ile Execution'a
çıkar (`Tasks/Executors/Remote/RemoteInvokerService.cs:79`).

FanOut task'ın kendisi `TaskExecutionEngine`'den **normal bir task olarak** geçer — kendi
retry'ı, error boundary'si, InstanceTask journal'ı ve tek output application'ı bedavaya gelir.
Executor'ın içi:

1. **Items çöz.** `itemsPath` varsa instance data üzerinde JSONPath ile; yoksa
   `IFanOutMapping.ItemSelector(ctx)`. Boş koleksiyon = başarılı, boş sonuç seti (**fail
   değil**); `OutputHandler` yine çağrılır (`Total = 0`).
2. **Inner task tanımını yükle.** `IComponentCacheStore`'dan referansla (key/domain/flow/version);
   bulunamazsa fail-fast.
3. **Item başına:** `ItemInputHandler(ctx, item)` → item input `ScriptResponse`. Her item için
   ayrı DI scope (`IServiceScopeFactory.CreateAsyncScope()`) + `ScriptContext.CreateParallelBranch()`
   — `TaskCoordinator.ExecuteTaskGroupInParallelAsync:285`'teki EF DbContext izolasyon deseni
   birebir. Inner task **collect-only modda** çalıştırılır: engine'in per-item retry + error
   boundary makinesi uygulanır, ancak çıktı instance data'ya **append edilmez**. Mekanizma:
   `TaskExecutorContext`/engine execute seçeneklerine eklenecek **`SuppressDataApply`** bayrağı —
   `TaskExecutionEngine.ApplyOutputToContextAsync:483` bu bayrakla atlanır.
4. **Bounded WhenAll.** Batch içi `SemaphoreSlim(maxDegreeOfParallelism)` + global bulkhead
   semaforu. Edinim sırası: önce batch-lokal, sonra global (nested yasak olduğundan bu sırada
   deadlock yüzeyi yok). Her item `itemTimeoutSeconds`, batch toplamı `batchTimeoutSeconds`
   ile sınırlı (linked `CancellationTokenSource`).
5. **Join değerlendir** → `FanOutResult` (`ordered: true` ise `Index` sıralı — deterministik
   çıktı, `DataHash`/ETag stabilitesi).
6. **Tek yazma.** `OutputHandler(ctx, result)` **bir kez** çağrılır; dönen `ScriptResponse.Data`
   FanOut task'ın normal çıktısı olarak engine'in standart yolundan **tek** InstanceData patch'i
   üretir. `OutputHandler` yazılmamışsa default davranış: item sonuç dizisi `join.resultKey`
   altına, `{ total, succeeded, failed, timedOut }` özeti `{resultKey}Summary` altına yazılır —
   mapping tamamen opsiyoneldir.

**Kritik sözleşmeler:**

- Item branch context'leri sonuç toplandıktan sonra **atılır**; `MergeParallelBranch`
  çağrılmaz. (Aynı task key'i N kez koşunca `MergeDictionary` aynı `TaskResponse[key]`'de
  farklı değer görüp `InvalidOperationException` fırlatırdı — `Models.cs:742`. FanOut kendi
  toplama modelini `FanOutResult` üzerinden kurar.)
- `ItemInputHandler` **saf fonksiyondur** — instance data'ya yazamaz; tek yazma noktası
  `OutputHandler`. Bu, single-writer invariant'ının fan-out'a izdüşümüdür.
- FanOut sıradan bir task olduğu için her tetik bağlamında (OnEntry/OnExit/OnExecute, function
  multi-task, extension) ek çalışma gerektirmeden kullanılabilir; profil/pipeline değişikliği yoktur.

## 5. Mapping Kontratı — `IFanOutMapping`

Konum: `src/BBT.Workflow.Domain/Scripting/Contracts/IFanOutMapping.cs` (mevcut `IMapping`,
`ISubProcessMapping` ailesinin yanına).

```csharp
public interface IFanOutMapping
{
    /// itemsPath yoksa fan-out kaynağı. Default null döner (itemsPath kullanılıyor demektir).
    Task<IEnumerable<dynamic>?> ItemSelector(ScriptContext context)
        => Task.FromResult<IEnumerable<dynamic>?>(null);

    /// Her item için inner task input binding'i. SAF fonksiyon — instance data'ya yazamaz.
    Task<ScriptResponse> ItemInputHandler(ScriptContext context, FanOutItem item);

    /// Tüm item'lar bitince TEK kez çağrılır. Tek yazma noktası.
    Task<ScriptResponse> OutputHandler(ScriptContext context, FanOutResult result);
}

public sealed record FanOutItem(int Index, dynamic Value, string ItemKey);

public sealed record FanOutResult(
    int Total, int Succeeded, int Failed, bool TimedOut,
    IReadOnlyList<FanOutItemResult> Items);   // ordered=true → Index sıralı

public sealed record FanOutItemResult(
    int Index, string ItemKey, bool IsSuccess,
    dynamic? Data, string? ErrorCode, string? ErrorMessage,
    int Attempts, TimeSpan Duration);
```

- **`ItemKey` üretimi:** item objesinde `id` ya da `key` alanı varsa onun string değeri; yoksa
  index. Log, span attribute ve journal key'lerinde kullanılır.
- **Sıfır-script fan-out mümkün:** `itemsPath` + default input binding (item'ın kendisi
  `Data.{itemAlias}` — `itemAlias` yoksa `Data.item` — olarak inner task'a geçer) + `resultKey`
  default output. Mapping yalnız özelleştirme gerektiğinde yazılır.

## 6. Hata Semantiği ve Join Policy

İki katman, birbirine karışmaz:

**Item seviyesi.** Config'deki `errorBoundary.onError` kuralları her item'a **bağımsız**
uygulanır (engine'in mevcut `ExecuteWithErrorAwareRetryAsync` makinesi). Retry tükenirse item
`Failed` olarak sonuç setine girer; batch'i tek başına durdurmaz — policy karar verir.

**Batch seviyesi.**

| `join.policy` | Davranış | Kullanım |
|---|---|---|
| `all` | Bir item fail olduğu anda kalan item'lar linked-CTS ile iptal edilir, FanOut task fail olur | Atomik gereksinim |
| `allSettled` | Tüm item'lar beklenir; FanOut task her zaman success, başarı+hata sonuç setinde döner; dallanma akış tasarımcısının auto-transition condition'ında | Kısmi başarı — beklenen yaygın kullanım |
| `quorum` | `Succeeded >= minSuccess` ⇒ success; aksi halde fail | Skorlama, çoklu kaynak |
| `firstSuccess` | İlk başarılı sonuçta kalanlar iptal, o tek sonuç döner; hiçbiri başaramazsa fail | Yedekli kaynak / failover lookup |

- **Boş koleksiyon (0 item):** `all` ve `allSettled` başarılı (vacuously true / her zaman
  başarılı). `quorum` ve `firstSuccess` **başarısız** — ikisi de eşik politikasıdır
  (`succeeded >= threshold`) ve boş bir batch hiçbir eşiği karşılayamaz. `firstSuccess`
  tanımı gereği `quorum(minSuccess=1)`'dir; ikisinin boş batch'te farklı davranması tutarsız
  olurdu. (Bu kural implementasyon sırasında düzeltildi: ilk taslak "boş batch quorum'da
  başarılı" diyordu, bu `firstSuccess` ile çelişiyordu.)
- FanOut task fail olursa workflow'un **kendi** Task→State→Global error boundary zinciri
  normal şekilde devreye girer; fan-out bu zincire yeni kavram eklemez.
- **Batch timeout:** `batchTimeoutSeconds` dolarsa koşan item'lar iptal edilir, başlamamışlar
  `Cancelled` (Failed, `ErrorCode = "FanOut:ItemCancelled"`) sayılır, `FanOutResult.TimedOut = true`
  ile join policy yine değerlendirilir — `allSettled` akışı devam ettirir, `all` fail eder.
- Hata yönetimi task'tan state machine'e taşınır: `allSettled` + `{resultKey}Summary.failed`
  üzerinden `partial-failure` state'ine dallanma önerilen desendir; platform karar vermez.

## 7. Eşzamanlılık ve Bulkhead

- **Batch içi:** `execution.maxDegreeOfParallelism`, default **4** — düşük default bilinçli:
  tavansız fan-out downstream servisi DDoS'lar.
- **Process geneli:** `Workflow:FanOut:MaxConcurrentItems` (default **64**) — tüm eşzamanlı
  fan-out batch'lerinin item'ları tek global `SemaphoreSlim`'den slot alır. 100 instance × 5
  maxDop = 500 eşzamanlı çağrı senaryosunu 64'e sabitler.
- Domain-level dağıtık tavan (Redis sayaç) bilinçli olarak **kapsam dışı** — dağıtık sayaç
  latency'si her item'a biner; ihtiyaç doğarsa ayrı iş.
- `inline` modda distributed lock zaten tutulmaz (Busy-as-mutex modeli); uzayan tek şey
  transition süresi = `max(item)` ≈ `sum(item)/maxDop`.

## 8. Audit ve Journal — konsept dokümanından sapma

Konsept "1 InstanceTask + N InstanceAction" varsayıyordu; keşifte görüldü ki **`InstanceAction`
task yürütme yolunda hiç üretilmiyor** (yalnız EF migration'larda mevcut). Gerçek model:

- **1 parent InstanceTask** — FanOut task'ın kendisi (engine zaten yazıyor).
- **N item InstanceTask** — her item çalıştırması engine'den geçtiği için journal kaydı
  bedavaya gelir; key formatı **`{taskKey}#{index}`**, request/response/attempt izi item
  bazında. History ekranında item drill-down bu kayıtlar üzerinden doğal çalışır.

## 9. Observability (Faz 1'de, sonradan değil)

- **Span ağacı:** `task.fanout` (parent) → `fanout.item[{idx}]` (N child); attribute'lar:
  `item_key`, `attempt`, `duration`, `error_code`.
- **Metrikler:** `fanout.batch.size` (histogram), `fanout.item.duration` (histogram),
  `fanout.concurrency.active` (gauge — global bulkhead doluluk göstergesi),
  `fanout.item.failures` (counter, `error_code` etiketli).
- **Straggler tespiti:** `max(item.duration) / p50(item.duration)` oranı — fan-out'ta toplam
  süreyi tek yavaş item belirler; alarm eşiği olarak dokümante edilir.
- **Loglar:** `WorkflowLogs.cs`'e `LoggerMessage` extension'ları (10xxx task serisinde yeni
  EventId bloğu): `FanOutBatchStarted`, `FanOutItemFailed`, `FanOutBatchCompleted`,
  `FanOutBatchTimedOut`, `FanOutBulkheadSaturated` (Warning).

## 10. Validation

Yeni `FanOutTaskValidator` (`src/BBT.Workflow.Domain/Definitions/Validators/`) +
`WorkflowValidator`'dan çağrı (mevcut `ErrorBoundaryValidator`/`ScriptCodeValidator` deseni).
Ek olarak `FanOutTask.Configure()` içinde fail-fast parse.

1. Kaynak **XOR**: `itemsPath` ve mapping'de `ItemSelector` override'ı — ikisi birden ya da
   hiçbiri ⇒ hata. (Script tarafı tanım zamanında tespit edilemiyorsa: `itemsPath` yokken
   mapping da yoksa ⇒ hata; runtime'da `ItemSelector` null dönerse ⇒ item kaynağı yok hatası.)
2. Inner task referansı zorunlu (`key/domain/flow/version`) ve çözülebilir olmalı.
3. **Nested yasak:** inner task type `21` ⇒ tanım zamanında red (derinlik 1).
4. `mode` yalnız `"inline"` — `"durable"` ⇒ "not yet supported".
5. `policy = quorum` ⇒ `minSuccess >= 1` zorunlu; diğer policy'lerde `minSuccess` varsa uyarı.
6. `maxDegreeOfParallelism >= 1`; `itemTimeoutSeconds`/`batchTimeoutSeconds` pozitif;
   `itemTimeoutSeconds <= batchTimeoutSeconds`.
7. Inner task türü için **ek kısıt yok** (karar #2) — Human/Timer gibi türlerin inline
   fan-out'ta anlamlı olmadığı developer dokümantasyonunda uyarı olarak yazılır.

## 11. Dokunulacak Yerler (keşif çıktısı)

| Katman | Dosya/Konum | İş |
|---|---|---|
| Domain | `Definitions/Tasks/TaskEnums.cs` | `FanOut = 21` |
| Domain | `Definitions/Tasks/WorkflowTask.cs` | `[JsonDerivedType(typeof(FanOutTask), "21")]` |
| Domain | `Definitions/Tasks/FanOutTask.cs` (yeni) | Config parse + `ITaskClonable` |
| Domain | `Scripting/Contracts/IFanOutMapping.cs` (yeni) | Mapping kontratı + record'lar |
| Domain | `Definitions/Validators/FanOutTaskValidator.cs` (yeni) | §10 kuralları |
| Domain | `Logging/WorkflowLogs.cs` | Yeni LoggerMessage bloğu |
| Application | `Tasks/Executors/FanOut/FanOutTaskExecutor.cs` (yeni) | §4 akışı |
| Application | `Tasks/Coordinator/TaskExecutionEngine.cs` | `SuppressDataApply` bayrağı |
| Application | `TaskServiceCollectionExtensions.cs` | `AddTaskExecutor<FanOutTaskExecutor>()` |
| Host | appsettings + options sınıfı | `Workflow:FanOut:MaxConcurrentItems` |
| Meta | `vnext-meta/` (component-registry, features) | Yeni task type kaydı |
| Şema | `vnext-schema` task şeması | type 21 + config şeması (`mode` dahil) |
| Helm | vnext-helm-charts | Bulkhead env değişkeni karşılığı |

Execution servisi tarafında **değişiklik yok** (FanOut orchestration-local; remote inner
task'lar mevcut invoker'larıyla çalışır).

## 12. Fazlama

| Faz | Kapsam | Not |
|---|---|---|
| **1 (bu spec)** | `inline` mod, 4 join policy, per-item errorBoundary/retry, `ordered`, global bulkhead, observability, validator, `mode` alanı şemada | Tek başına sevk edilebilir |
| 2 | `durable` mod: `fan_out_batch`/`fan_out_item` tabloları, atomik `remaining` decrement, mailbox gather, `FanOutReaper` | Uzun süren item'lar; instance_commands mailbox'ının ilk gerçek müşterisi |
| 3 | Forge Studio fan-out görselleştirmesi | Tasarımcı deneyimi |

`mode` alanının şemada baştan bulunması Faz 2'yi breaking change olmaktan çıkarır
(notification → notifications dersi).

## 13. Test Stratejisi

**Unit** (`Domain.Tests` + `Application.Tests`):
- Join policy matrisi: 4 policy × {tam başarı, kısmi, tam fail, timeout}.
- `itemsPath` çözümü (boş koleksiyon, eksik path, iç içe path), ItemKey üretimi.
- Validator: XOR, nested red, durable red, quorum/minSuccess, timeout sıralaması.
- Bounded concurrency: maxDop hiç aşılmıyor; global bulkhead sınırı; `firstSuccess`/`all`
  iptal davranışı; `SuppressDataApply` ile instance data'ya item yazılmadığının doğrulanması.

**Integration** (`vnext-example`, CLAUDE.local.md politikası gereği — temel sürece yeni
primitif ekleyen major geliştirme):
- Yeni senaryo klasörü (örn. `fan-out-documents`): MockLab'a N dokümanlık batch;
  `allSettled` ile kısmi başarı → `partial-failure` state dallanması; tek InstanceData patch
  doğrulaması; N item InstanceTask journal kaydı doğrulaması.
- README (neyi denetliyor / neden var / akış / çalıştırma / başarı kriteri) +
  `TEST-SCENARIOS.md` satırı **aynı commit'te**.
- Yük testi Python scripti (`api-tests/fan-out-documents/`): eşzamanlı M instance × N item;
  ölçüm: global bulkhead tavanının tutması (`fanout.concurrency.active <= MaxConcurrentItems`),
  downstream'e giden eşzamanlı istek sayısı, straggler oranı.

## 14. Konsept Dokümanının Açık Kararlarının Kapanışı

| Konsept sorusu | Karar |
|---|---|
| itemsPath mi ItemSelector mı | İkisi de; tam olarak biri — ikisi birden ⇒ validator hatası |
| Item sonucu data'ya mı OutputHandler'a mı | Yalnız OutputHandler; audit item InstanceTask journal'ında |
| maxDop scope'u | Task-level + process-level global bulkhead; dağıtık domain tavanı kapsam dışı |
| SubProcess/StartFlow inner olabilir mi | Evet (kısıtsız); inline modda `sync: false` önerilir, dokümante edilir |
| Nested fan-out | Yasak, derinlik 1, validator reddeder |
| `ordered` maliyeti | Default true; 10-100 item ölçeğinde sorun değil, çok büyük batch'ler durable modun konusu |
| Durable cross-domain decrement | Faz 2'nin konusu; mailbox cross-domain stratejisiyle birlikte çözülecek |

## 15. Amendments (plan aşamasında netleşen sapmalar)

Detaylar `docs/superpowers/plans/2026-08-21-fanout-task.md` başındaki "Spec Amendments" bölümünde:

1. `ItemInputHandler(WorkflowTask task, ScriptContext context, FanOutItem item)` — IMapping
   konvansiyonu gereği input binding klonlanmış inner task'ın mutasyonudur; ScriptResponse audit'tir.
2. `FanOutItemResult.Attempts` kaldırıldı — engine retry sayısını sonuç üzerinden dışarı vermiyor;
   attempt görünürlüğü item journal kaydı ve retry span event'lerinde.
3. Validasyon bölünmesi: yapısal kurallar `FanOutTask.Configure()` (fail-fast), cross-component
   kurallar (nested, kaynak belirsizliği) executor preflight'ında. `WorkflowValidator` dokunulmaz —
   FanOut config'i workflow dokümanında değil task component'inde yaşar.
4. `TaskExecutorContext`'e `Origin` (TaskExecutionOrigin) eklenir — item'lar parent origin'ini miras alır.
5. Mapping yokken default input binding: item değeri branch context'e `SetBody(item.Value)` ile
   verilir; task config mutasyonu gerektiren inner türler `ItemInputHandler` yazmalıdır.
