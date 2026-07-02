---
name: vnext-features
description: Scope is repo-wide domain knowledge (not bound to a single workspace). vNext Runtime platform referansı. Workflow, state, transition, task, schema, view, function ve extension kavramlarını, JSON tanım yapılarını ve C# mapping/rule pattern'lerini kapsar. vNext bileşenleri oluşturulurken veya düzenlenirken bu skill'i kullanın. Trigger this skill whenever vNext workflow/task/schema/view/function/extension JSON or `.csx` mapping/rule files are being created or edited anywhere in the monorepo (or in target vnext domain repositories).
---

# vNext Skill

> **Scope:** Repo-wide domain reference. vNext bileşenleri (workflow, task, schema, view, function, extension JSON ve `.csx` mapping/rule dosyaları) ile çalışırken her workspace'te tetiklenir; tek bir `apps/*` veya `packages/*` ile sınırlı değildir.

Bu dosya, vNext Runtime'ı hızlı ama eksiksiz sayılabilecek bir çerçevede anlamak için hazırlanmış kompakt bir referanstır. Amaç, vNext ile çalışan birinin platformun ana modelini, JSON tanımlarını, desteklenen yapılarını ve genişleme noktalarını tek yerden kavramasıdır.

## 1. vNext Nedir?

vNext, iş akışlarını JSON ile tanımlanan, versiyonlanan ve runtime'da state machine olarak çalışan workflow'lar üzerinden yöneten bir platformdur.

Temel yaklaşım:

- Süreçler birer `workflow/flow` olarak tanımlanır.
- Workflow içindeki ilerleme `state` ve `transition` yapıları ile kontrol edilir.
- İş yapan parça `task`'tır.
- Veri kontratı `schema` ile tanımlanır.
- UI veya istemci gösterimi `view` ile verilir.
- Okuma/erişim amaçlı runtime endpoint'leri `function` ile sunulur.
- Response zenginleştirme `extension` ile yapılır.
- Tekrarlı kod yerine `reference` ve `version strategy` kullanılır.

Kısacası vNext:

- süreç modelleme,
- süreç orkestrasyonu,
- entegrasyon,
- stateful runtime,
- versiyonlu tanım yönetimi

işlerini tek çatı altında toplar.

## 2. Zihinsel Model

vNext'i doğru anlamanın en kısa yolu şu akışı bilmektir:

1. Bir workflow JSON'u yayınlanır.
2. Runtime bu workflow'un bir `instance`'ını başlatır.
3. Instance bir state'te durur.
4. Kullanıcı, event, timer veya otomatik kural bir transition tetikler.
5. Transition öncesi/sonrası task'lar çalışır.
6. Veri schema kurallarına göre taşınır ve doğrulanır.
7. Gerekirse subflow/subprocess başlatılır.
8. Runtime state'i değiştirir, view ve function çıktıları buna göre güncellenir.

Bu yüzden vNext'te ana odak "sayfa" değil, "durumdan duruma ilerleyen süreç"tir.

## 3. Ana Elemanlar

### 3.1 Workflow / Flow

Workflow, sürecin ana tanımıdır. Zorunlu olarak:

- `type`
- `startTransition`
- `states`

alanlarını içerir.

Desteklenen workflow tipleri:

- `C`: Core
- `F`: Flow
- `S`: SubFlow
- `P`: SubProcess

Sık kullanılan ek alanlar:

- `labels`
- `timeout`
- `functions`
- `features`
- `extensions`
- `sharedTransitions`
- `cancel`
- `schema`
- `errorBoundary`

Ne zaman hangisi:

- `F`: ana iş akışı
- `S`: parent ile bağlı çalışan alt akış
- `P`: bağımsız, fire-and-forget benzeri alt süreç
- `C`: sistemsel/çekirdek tanımlar

### 3.2 State

State, instance'ın o an bulunduğu adımdır.

State tipleri:

- `1`: Initial
- `2`: Intermediate
- `3`: Finish
- `4`: SubFlow

State alt tipleri:

- `0`: None
- `1`: Success
- `2`: Error
- `3`: Terminated
- `4`: Suspended
- `5`: Busy
- `6`: Human

Bir state içinde tipik olarak şunlar bulunur:

- `key`
- `stateType`
- `subType`
- `labels`
- `transitions`
- `onEntries`
- `onExits`
- `view`
- `subFlow`
- `errorBoundary`

Önemli not:

- `SubFlow` state'i parent-child ilişki kurar.
- Runtime, alt akışlarda `effectiveState` mantığı ile dışarıya daha anlamlı durum gösterebilir.

### 3.3 Transition

Transition, state değişimini başlatan tanımdır.

Ana alanlar:

- `key`
- `from`
- `target`
- `triggerType`

Opsiyonel alanlar:

- `timer`
- `rule`
- `schema`
- `availableIn`
- `labels`
- `view`
- `mapping`
- `onExecutionTasks`

Trigger tipleri:

- `0`: Manual
- `1`: Automatic
- `2`: Scheduled
- `3`: Event

Önemli davranışlar:

- `startTransition` başlangıç geçişidir, `from` içermez.
- `availableIn`, ortak transition'ların birden fazla state'te kullanılmasını sağlar.
- `target: "$self"` deseni, özellikle subflow sonrası parent data güncelleme gibi senaryolarda kullanılır.
- Geçiş payload'ı mapping yoksa doğrudan instance data'ya merge edilir.

### 3.4 Task

Task, gerçek işi yapan birimdir. vNext'te entegrasyonun ve işlem yürütmenin temel taşıdır.

Task'lar şu yerlerde çalışabilir:

- state `onEntries`
- state `onExits`
- transition `onExecutionTasks`
- function tanımları
- extension tanımları

Çalışma sırası:

- Aynı `order`: paralel
- Farklı `order`: küçükten büyüğe sıralı

Ana task aileleri:

- `Http`
- `DaprService`
- `DaprPubSub`
- `Script`
- `Trigger`
- `GetInstances`
- `Notification` (özel mapping; aşağıda uyarı)
- `Condition` (sistemsel — manuel JSON tanımı yok)
- `Timer` (sistemsel — manuel JSON tanımı yok)

Trigger görev ailesinde öne çıkanlar:

- `StartTask`
- `DirectTriggerTask`
- `GetInstanceDataTask`
- `SubProcessTask`

Task response tarafında standart olarak şu tip bilgiler bulunur:

- `Data`
- `StatusCode`
- `IsSuccess`
- `ErrorMessage`
- `Headers`
- `Metadata`
- `ExecutionDurationMs`
- `TaskType`

Bu yapı sayesinde task'lar sadece çağrı yapmak için değil, workflow orchestration için de kullanılır.

#### Özel davranışlı task tipleri (kritik)

Aşağıdaki dört task tipi diğer task'lerden farklı kurallara tabidir; workflow oluştururken veya test ederken her zaman bu davranışlara uy:

- **Human Task (tip 5) — KULLANMA, geçici özellik.** vnext-runtime ekibi tarafından **kaldırılacak** olarak işaretlendi. Yeni veya mevcut workflow'larda Human Task `onEntries` referansı oluşturma; mevcutsa zincirden çıkar. Manuel onay senaryosu için Human Task yerine boş `onEntries` + manuel `triggerType: 0` transition'ı olan intermediate state (stateType 2) kullan. Mevcut Human Task referansı olan workflow runtime'da `Task:Unknown:human-task - Operation is not valid due to the current state of the object` hatasıyla `status: F` (Faulted) düşer.

- **Notification Task (tip 10) — kullanım onayı zorunlu.** Diğer task'lerden farklı çalışır (Dapr binding üzerinden hub'a publish, özel platform mapping `mapping.type: "G"`, özel altyapı bağımlılığı). Bir workflow'a Notification Task eklemeden veya işlevini değiştirmeden önce **AI olarak kullanıcıya açıkça sor**: "Notification Task normal task'lardan farklı çalışır (Dapr binding + `mapping.type: G`); onu eklemek istediğinden emin misin?". Onay almadan ekleme. Onaylandığında `onEntries.mapping`'i `{ "type": "G" }` olarak yaz; özel `IMapping` `.csx` ekleme.

- **Condition Task (tip 8) — manuel JSON tanımı YOKTUR.** `triggerType: 1` (auto) transition'ın `rule` alanı çalıştırıldığında runtime tarafından arka planda mantıksal olarak üretilir. **Asla `Tasks/.../*.json` ile `attributes.type: "8"` olarak tanımlama**; bunun yerine sadece auto transition'a `rule.location` + `IConditionMapping` `.csx` bağla.

- **Timer Task (tip 9) — manuel JSON tanımı YOKTUR.** `triggerType: 2` (scheduled) transition'ın `timer` alanı çalıştırıldığında runtime tarafından üretilir. **Asla `Tasks/.../*.json` ile `attributes.type: "9"` olarak tanımlama**; bunun yerine sadece scheduled transition'a `timer.location` + `ITimerMapping` `.csx` bağla. `Handler` `TimerSchedule.FromDateTime/FromDuration/FromCron/Immediate` döndürmeli (object initializer ile dönüş Faulted'a yol açar).

Detaylı tuzaklar ve doğrulanmış kalıplar: `vnext-workflow-creation` skill §6.1 (Notification), §6.3 (Human Task), §6.7 (Condition/Timer Task), §2.1 (TimerSchedule); test hizalaması: `vnext-tests-as-code` skill "Test edilirken özel davranışlar" bölümü.

### 3.5 Schema

Schema, veri kontratıdır. Hem workflow verisini hem task/function/view gibi tanım tiplerini standardize eder.

Schema tanımı tipik olarak şu alanları taşır:

- `key`
- `version`
- `domain`
- `flow`
- `flowVersion`
- `tags`
- `attributes`

`attributes.type` destekleri:

- `workflow`
- `task`
- `function`
- `view`
- `schema`
- `extension`
- `headers`

Schema motoru JSON Schema Draft 2020-12 tabanlıdır.

Desteklenen ana JSON Schema özellikleri:

- primitive tipler: `string`, `number`, `integer`, `boolean`, `array`, `object`, `null`
- string doğrulamaları: `minLength`, `maxLength`, `pattern`, `format`
- formatlar: `email`, `uri`, `date`, `date-time`, `time`, `uuid`, `ipv4`, `ipv6`
- sayısal doğrulamalar: `minimum`, `maximum`, `exclusiveMinimum`, `exclusiveMaximum`, `multipleOf`
- array alanları: `items`, `minItems`, `maxItems`, `uniqueItems`
- object alanları: `properties`, `required`, `additionalProperties`
- koşullu yapılar: `if/then/else`, `oneOf`, `anyOf`, `allOf`

Flow seviyesinde `master schema` kullanılabilir. Bu, instance data'nın kalıcı kontratıdır. Yan ürün geliştirirken en önemli referans çoğu zaman budur.

Ek olarak:

- property bazlı görünürlük/authorization kurguları desteklenir.
- transition bazlı schema ile kullanıcı aksiyon payload'ı ayrı doğrulanabilir.

### 3.6 View

View, istemcinin ne göstereceğini tanımlar. UI kodu olmak zorunda değildir; render edilebilir bir temsil tanımıdır.

Ana alanlar:

- `key`
- `flow`
- `domain`
- `version`
- `type`
- `content`
- `display`
- `labels`
- `platformOverrides`

Desteklenen view tipleri:

- `Json`
- `Html`
- `Markdown`
- `DeepLink`
- `Http`
- `URN`

Display modları:

- `full-page`
- `popup`
- `bottom-sheet`
- `top-sheet`
- `drawer`
- `inline`

Platform override destekleri:

- `web`
- `ios`
- `android`

View seçimi:

- state view
- transition view
- rule-based view selection
- platform override
- dil/label seçimi

Yan ürün geliştirirken view'dan alınması gereken mesaj:

- vNext UI'yı dikte etmez,
- ancak UI için yapılandırılabilir bir gösterim katmanı sunar.

### 3.7 Function

Function, workflow instance'ı okumak veya workflow ile etkileşmek için sistemsel API yüzeyidir.

Temel built-in function'lar:

- `state`
- `data`
- `view`

Ne sağlarlar:

- `state`: mevcut state, mümkün transition'lar, subflow korelasyonları
- `data`: instance data, etag, extension datası
- `view`: state/transition bazlı view içeriği

Function'lar:

- istemcinin runtime iç yapıyı bilmeden veri almasını sağlar,
- long-polling ve ETag gibi senaryoları destekler,
- authorization ve role bazlı görünürlüğe uyum sağlar.

### 3.8 Extension

Extension, instance response'unu zenginleştiren yardımcı bileşendir.

Function ile farkı:

- Function dış endpoint sunar.
- Extension dış endpoint sunmaz.
- Extension çıktısı `extensions` alanında response'a eklenir.

Extension tipleri:

- `1`: Global
- `2`: GlobalAndRequested
- `3`: DefinedFlows
- `4`: DefinedFlowAndRequested

Scope tipleri:

- `1`: GetInstance
- `2`: GetAllInstances / history benzeri okuma uçları
- `3`: Everywhere

Kullanım amacı:

- harici bilgi ekleme
- hesaplanmış veri ekleme
- profil, limit, referans bilgi, session gibi zenginleştirme

### 3.9 Mapping

Mapping, vNext'in veri dönüştürme katmanıdır. Task'a giden veriyi ve task'tan dönen veriyi runtime context ile bağlar.

Ana mapping türleri:

- input mapping
- output mapping
- transition mapping
- timer mapping
- condition mapping
- subflow mapping
- subprocess mapping

Kritik interface'ler:

- `IMapping`
- `ITimerMapping`
- `ISubProcessMapping`
- `ISubFlowMapping`
- `IConditionMapping`
- `ITransitionMapping`

Script tarafında tipik çalışma context'i:

- request body
- headers
- query parameters
- route values
- instance data
- workflow definition
- transition definition
- task response'ları
- metadata

Script motoru Roslyn/C# tabanlıdır. Native C# ve encode edilmiş script kullanımı desteklenir.

**Koleksiyon API lambda tuzağı (kritik):** ScriptBase'in `ListFilter`, `ListFirst`, `ListLast`, `ListSelect`, `ListRemove`, `ListAny`, `ListCount` gibi koleksiyon API'leri lambda (predicate/selector) overload'larında `x.status`, `x.name`, `x.age` gibi **doğrudan dynamic property erişimi kullanılmamalıdır**. Runtime serialize/deserialize döngüsü (JSON → iç temsil → tekrar dynamic) sonrası `CreateObject()` + `SetProperty()` ile oluşturulan koleksiyon elemanları artık `ExpandoObject` olmayabilir (`Dictionary<string, object>` veya runtime iç tipi). Bu tiplerde C# dynamic dispatch (`x.status`) çalışmaz → `RuntimeBinderException` fırlatılır. Instance ilgili state'te takılı kalır (status: "A", Faulted değil — mapping catch bloğu hatayı yakalıyor ama önceki state verilerini kaybediyor veya runtime mapping'i sessizce atlıyor). Lambda'larda her zaman `GetPropertyValue<T>(x, "propertyName")` kullanılmalıdır. Detaylı tuzak analizi ve doğru/yanlış örnekler: `vnext-workflow-creation` skill §10.

### 3.10 Error Boundary

Error handling hiyerarşik çalışır:

- task seviyesi
- state seviyesi
- global workflow seviyesi

Ana aksiyonlar:

- `Abort`
- `Retry`
- `Rollback`
- `Ignore`
- `Notify`
- `Log`

Retry policy ile:

- `maxRetries`
- `initialDelay`
- `backoffType`
- `backoffMultiplier`
- `maxDelay`
- `useJitter`

tanımlanabilir.

Yan ürün geliştirirken bu önemli çünkü vNext sadece "happy path" değil, hata ve retry akışını da model seviyesinde taşır.

### 3.11 Reference ve Versioning

vNext'te tekrar yerine referans kullanılır. Tanımlar birbirine doğrudan gömülmek yerine version'lı reference ile bağlanır.

Tam referans yapısı:

```json
{
  "key": "customer-lookup",
  "domain": "crm",
  "version": "1.2.0",
  "flow": "sys-tasks"
}
```

Kullanıldığı yerler:

- task referansı
- subflow referansı
- function referansı
- extension referansı
- schema referansı
- view referansı

Sistemsel akışlar:

- `sys-flows`
- `sys-views`
- `sys-functions`
- `sys-tasks`
- `sys-extensions`
- `sys-schemas`

Versioning yaklaşımı:

- semver
- explicit version referansı
- version strategy ile latest/major/minor benzeri seçim

Bu sayede runtime ve tanım yönetimi ayrışır.

### 3.12 Instance ve Persistence

Runtime'da çalışan şey tanım değil, `instance`'tır.

Instance tarafında tipik kavramlar:

- `id`
- `key`
- `flow`
- `domain`
- `flowVersion`
- `etag`
- `tags`
- `attributes`
- `metadata`
- `data`
- `extensions`

Persistence modeli özetle:

- workflow instance kayıtları master data tarafında tutulur,
- transition/task/action/job geçmişi ayrı tablolarda izlenebilir,
- domain bazlı veritabanı ayrımı bulunur.

Bu yapı vNext'i tek bir workflow engine olmaktan çıkarıp çok domain'li enterprise runtime haline getirir.

## 4. JSON Nasıl Oluşturulur?

vNext'te çoğu tanım ortak bir zarf yapısı kullanır:

```json
{
  "key": "my-definition",
  "version": "1.0.0",
  "domain": "banking",
  "flow": "sys-flows",
  "flowVersion": "1.0.0",
  "tags": ["banking", "example"],
  "attributes": {}
}
```

Pratikte dikkat edilmesi gerekenler:

- `key`: benzersiz ve okunabilir olsun
- `version`: semver olsun
- `domain`: sahiplik alanını anlatsın
- `flow`: bunun hangi sistem koleksiyonuna ait olduğunu göstersin
- `attributes`: asıl işi burada tanımlarsın

## 5. Minimal JSON İskeletleri

### 5.1 Minimal Workflow

```json
{
  "key": "account-opening",
  "version": "1.0.0",
  "domain": "banking",
  "flow": "sys-flows",
  "flowVersion": "1.0.0",
  "tags": ["banking", "onboarding"],
  "attributes": {
    "type": "F",
    "startTransition": {
      "key": "start",
      "target": "draft",
      "triggerType": 1
    },
    "states": [
      {
        "key": "draft",
        "stateType": 1,
        "subType": 6,
        "transitions": [
          {
            "key": "submit",
            "from": "draft",
            "target": "completed",
            "triggerType": 0
          }
        ]
      },
      {
        "key": "completed",
        "stateType": 3,
        "subType": 1
      }
    ]
  }
}
```

### 5.2 Transition İçinde Task Kullanımı

```json
{
  "key": "submit",
  "from": "draft",
  "target": "completed",
  "triggerType": 0,
  "onExecutionTasks": [
    {
      "order": 1,
      "task": {
        "key": "notify-customer",
        "domain": "notification",
        "version": "1.0.0",
        "flow": "sys-tasks"
      }
    }
  ]
}
```

### 5.3 Minimal Schema

```json
{
  "key": "account-opening-master-schema",
  "version": "1.0.0",
  "domain": "banking",
  "flow": "sys-schemas",
  "flowVersion": "1.0.0",
  "tags": ["workflow", "schema"],
  "attributes": {
    "type": "workflow",
    "schema": {
      "type": "object",
      "properties": {
        "customerId": { "type": "string" },
        "amount": { "type": "number", "minimum": 0 }
      },
      "required": ["customerId"]
    }
  }
}
```

### 5.4 Minimal View

```json
{
  "key": "draft-form-view",
  "version": "1.0.0",
  "domain": "banking",
  "flow": "sys-views",
  "flowVersion": "1.0.0",
  "tags": ["view"],
  "attributes": {
    "type": "Json",
    "display": "full-page",
    "content": {
      "component": "AccountOpeningForm"
    }
  }
}
```

### 5.5 Minimal Extension

```json
{
  "key": "extension-customer-profile",
  "version": "1.0.0",
  "domain": "crm",
  "flow": "sys-extensions",
  "flowVersion": "1.0.0",
  "tags": ["profile"],
  "attributes": {
    "type": 4,
    "scope": 1,
    "task": {
      "order": 1,
      "task": {
        "key": "get-customer-profile",
        "domain": "crm",
        "version": "1.0.0",
        "flow": "sys-tasks"
      }
    }
  }
}
```

## 6. vNext Hangi Yapıları ve Feature'ları Destekliyor?

### 6.1 Süreç Modelleme

- version'lı workflow
- state machine
- start transition
- finish state
- manual/auto/event/scheduled transition
- shared transition
- cancel flow
- timeout
- subflow
- subprocess
- parent-child correlation

### 6.2 Orkestrasyon ve Entegrasyon

- HTTP çağrıları
- Dapr service invocation
- Dapr pub/sub
- script execution
- başka workflow başlatma
- doğrudan transition tetikleme
- instance data okuma
- instance listeleme / filtreleme / sıralama (sort/orderBy JSON formatı v0.0.37+)
- timer ve condition tabanlı otomasyon

### 6.3 Veri ve Doğrulama

- JSON Schema Draft 2020-12
- master schema
- transition schema
- headers schema
- field-level visibility
- camelCase veri kullanım pratiği
- mapping ile custom payload transform

### 6.4 UI ve Client Entegrasyonu

- state view
- transition view
- rule-based view selection
- Json/Html/Markdown/DeepLink/Http/URN view tipleri
- display modları
- platform override
- localization/labels

### 6.5 Runtime API Yetenekleri

- state polling
- data fetch
- view fetch
- permissions/authorize endpoint'leri
- ETag / conditional fetch
- extension ile enriched response
- instance listeleme sıralama: `sort` / `orderBy` query parametresi **JSON formatında** (v0.0.37+)
  - Tek alan: `?sort={"field":"createdAt","direction":"desc"}` (descending / azalan) veya `{"field":"createdAt","direction":"asc"}` (ascending / artan)
  - Çoklu alan: `?sort={"fields":[{"field":"status","direction":"asc"},{"field":"createdAt","direction":"desc"}]}`
  - `direction` verilmezse varsayılan `"asc"` (ascending)
  - **Düz string** (`-createdAt`, `createdAt`) formatı runtime tarafından güvenilir yorumlanmaz; her zaman JSON kullan
  - Sayfalama: `page` ve `pageSize` query parametreleri ile birlikte kullanılır
  - Referans: `vnext-runtime/doc/tr/flow/instance-filtering.md` "OrderBy / Sort (v0.0.37+)"

### 6.6 Kurumsal Özellikler

- semver ve version strategy
- domain bazlı ayrışma
- ayrı veritabanı / schema yapıları
- audit/history izleme
- retry/backoff/jitter
- role/queryRole bazlı yetkilendirme
- hata boundary hiyerarşisi

## 7. Yan Ürün Geliştirecek Biri İçin Yol Haritası

vNext üzerinde yan ürün geliştirirken genelde şu giriş noktalarından biri kullanılır:

- workflow JSON üretici/editörü
- schema/view validator
- runtime client SDK
- instance monitor dashboard
- transition execution paneli
- workflow diff/version karşılaştırıcı
- task kütüphanesi yöneticisi
- extension/function katalog aracı

En kritik doğrular:

- Süreci state machine olarak düşün.
- Kalıcı veri kontratını `master schema` üzerinden kur.
- Tekrarlı gömme yerine `reference` kullan.
- UI'ı workflow'dan değil, `view + state + schema` üçlüsünden türet.
- Entegrasyon mantığını `task + mapping` ile taşı.
- Okuma API'leri için `function`, response zenginleştirme için `extension` kullan.

## 8. Kısa Tasarım Kuralları

- Workflow'ları küçük ve anlamlı tut.
- State isimleri business meaning taşısın.
- Transition isimleri aksiyon fiili gibi olsun.
- Task'ları tekrar kullanılabilir tasarla.
- Aynı concern'ü function ve extension arasında karıştırma.
- Schema'yı gerçeğin kaynağı kabul et.
- Subflow ile subprocess farkını net koru: biri bağlı, biri bağımsız.
- Error boundary tanımını en yakın seviyede çöz, global'i fallback olarak bırak.

## 9. Sonuç

vNext, JSON tabanlı, versiyonlu, stateful bir workflow runtime platformudur. Ana yapı taşları:

- workflow
- state
- transition
- task
- schema
- view
- function
- extension
- mapping
- reference/versioning
- instance/persistence
- error boundary

Bir kişi bu dosyayı baz alarak:

- vNext'in neyi çözdüğünü,
- workflow JSON'un nasıl kurulduğunu,
- hangi feature'ların desteklendiğini,
- nereden genişletme yapılacağını

anlayıp vNext için araç, entegrasyon, editör, SDK veya izleme ürünü geliştirmeye başlayabilir.
