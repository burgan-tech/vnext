# vNext Monitor API — Kullanıcı Rehberi (Sunulan Monitoring Yetenekleri)

> Bu doküman, vNext Monitor API'nin (port **4203**, **salt-okunur**) kullanıcılara/dashboard'lara sunduğu **tüm monitoring yeteneklerini** ve her birinin **ne işe yaradığını / ne zaman kullanılacağını** anlatır. Amaç kullanıcıyı yönlendirmektir; iç kodlama detayı içermez.
>
> Tüm endpoint'ler **yalnızca okur**: hiçbir state değiştirmez, transition tetiklemez, task çalıştırmaz.
>
> İlgili dosyalar: çalıştırılabilir örnekler → [endpoints/vnext-monitor.http](../../endpoints/vnext-monitor.http) ve [Postman koleksiyonu](../../endpoints/vnext-monitor.postman_collection.json); filtreleme rehberi → [monitoring-filter-guide.md](monitoring-filter-guide.md).

---

## 0. Monitoring'i Nasıl Kullanırım? (Temeller)

- **Taban adres:** `http://<host>:4203`
- **Sürümlü endpoint'ler:** `…/api/v1.0/monitor/...` öneki ile.
- **Sürümsüz operasyonel endpoint'ler:** `/health`, `/ready`, `/live`, `/version`, `/metrics` (öneksiz).
- **Domain ve workflow:** Çoğu adres `{domain}` ve `{workflow}` içerir (ör. `core` / `account-opening-workflow`). Sorgu otomatik olarak ilgili workflow verisine yönlendirilir.
- **Instance kimliği:** `{instance}` hem iş anahtarı (business key) hem de GUID olabilir.
- **Sayfalama:** Liste endpoint'lerinde `?page=1&pageSize=10` (en fazla 100).
- **Filtreleme:** `filter` bir **JSON nesnesidir** (ayrıntı: §7 ve filtre rehberi).

### Ne sunuyoruz? (İhtiyaç → Yetenek)

| Kullanıcı ihtiyacı | Hangi endpoint(ler) |
|--------------------|---------------------|
| Çalıştırdığım workflow'ların **genel durumu** | Durum sayaçları, canlı state dağılımı, instance listesi |
| Bir sürecin **ilerlemesini** takip etme | Instance detay, anlık durum (state), geçiş zaman çizelgesi (timeline) |
| **Hata teşhisi** | Faulted listesi (filtre), fault detayı |
| Instance **verisi** ve değişimi | Data, data diff |
| **Alt süreçler** (subflow) | Hiyerarşi ağacı, parent'a ters navigasyon |
| Kaydettiğim **workflow/component tanımları** | Component definition (versiyonlu), bağımlılık analizi, component sayım özeti |
| Aktif **zamanlanmış job'lar** | Workflow veya domain genelinde aktif job listesi |
| Monitor **çalışma durumu** | Runtime config (secret'sız) |
| Esnek **arama** | GraphQL filtre altyapısı |
| **Operasyonel sağlık** | Health / ready / live / version / metrics |

---

## 1. Dashboard Açılışı (Genel Bakış)

Tek bakışta "süreçlerim nasıl gidiyor?" sorusunun cevabı.

### Durum Sayaçları — `GET {domain}/workflows/{workflow}/stats/instances`
Bir workflow için kaç instance'ın **Active / Busy / Completed / Faulted / Passive** olduğunu ve toplamı döndürür. Dashboard'ın ana sağlık widget'ı. İsteğe bağlı GraphQL filter ile sayaçları belirli bir veri kesimine daraltabilirsiniz.

**Parametre varyasyonları:**
- *(parametresiz)* → workflow'un tüm versiyonlarındaki instance sayıları
- `?version=1.0.0` → yalnızca o workflow versiyonuyla **başlatılmış** instance'ların sayıları
- `?filter={"createdAt":{"ge":"2026-05-18T00:00:00Z"}}` → yalnızca belirtilen tarihten sonra **başlatılan** instance'ların durum dağılımı
- `?filter={"currentState":{"eq":"approve"}}` → yalnızca belirtilen state'te bulunan instance'ların durum dağılımı
- `?filter={"and":[{"status":{"eq":"Faulted"}},{"createdAt":{"ge":"2026-01-01T00:00:00Z"}}]}` → birden fazla koşul AND ile birleştirilebilir

> `filter` ve `version` aynı anda kullanılamaz — `version` kısıtını filter içinde `{"flowVersion":{"eq":"1.0.0"}}` olarak yazabilirsiniz. İkisi birlikte verilirse 400 döner.

### Domain Durum Sayaçları — `GET {domain}/stats/instances`
Domain'deki **tüm workflow'lar** için Active / Busy / Completed / Faulted / Passive + Total değerlerini döndürür. Her workflow kendi şemasında tutulduğu ve tablolar zamanla milyonlarca satıra ulaştığı için bu sorgu varsayılan olarak **bir tarih penceresiyle sınırlandırılır**: filtre verilmezse **son 7 günde başlatılan** (createdAt) instance'lar sayılır. Yanıttaki `appliedFilter` alanı fiilen uygulanan filtreyi gösterir.

**Ne zaman kullanılır:** Dashboard açılışında domain genelinde "son dönemde ne oldu?" özetini görmek için. Daha geniş veya farklı bir dönem gerekiyorsa filtreyle aralık verilir.

**Parametre varyasyonları (`?filter=` — GraphQL filtresi):**
- *(parametresiz)* → varsayılan **son 7 gün** içinde başlatılanlar
- `?filter={"createdAt":{"ge":"2026-05-18T00:00:00Z"}}` → bu tarihten sonra başlatılanlar (örn. son 30 gün, son 3 ay)
- `?filter={"createdAt":{"between":["2026-03-01T00:00:00Z","2026-06-01T00:00:00Z"]}}` → belirli başlangıç–bitiş aralığı
- `?filter={"currentState":{"eq":"approve"}}` → filtrede tarih yoksa varsayılan **son 7 gün** otomatik olarak AND'lenir (sınırsız tam tablo taraması yapılmaz)

### Canlı State Dağılımı — `GET {domain}/workflows/{workflow}/stats/states`
Her workflow state'inde kaç instance bulunduğunu (Total/Active/Busy/Faulted) verir. "Süreç nerede yığılıyor / tıkanıyor?" sorusunu yanıtlar. UI bu sayıları workflow tanımından çizdiği grafiğe bindirir.

**Parametre varyasyonları:**
- *(parametresiz)* → tüm versiyonlardaki instance'lar baz alınarak state dağılımı
- `?version=1.0.0` → yalnızca o workflow versiyonuyla başlatılan instance'lar baz alınarak dağılım

### Aktif Instance Listesi — `GET {domain}/workflows/{workflow}/instances`
Instance'ları sayfalı listeler. Filtre/sıralama ile zenginleştirilebilir (§7).

---

## 2. Instance İzleme (Bir süreci uçtan uca takip et)

### Instance Detay — `GET {domain}/workflows/{workflow}/instances/{instance}`
Tek bir instance'ın metadata'sı ve **aktif alt-süreç korelasyonları**. "Bu instance kim, hangi durumda?" İş anahtarı veya GUID ile çağrılabilir.

### Anlık Durum + Yapılabilir Geçişler — `GET …/instances/{instance}/state`
Instance'ın şu anki state'i, status'u, **buradan tetiklenebilecek geçişler** ve aktif alt-süreçler — tek çağrıda. Detay endpoint'i "kim olduğunu", state endpoint'i "şu an ne yapılabileceğini" söyler.

### Zaman Çizelgesi — `GET …/instances/{instance}/timeline`
Instance'ın geçiş geçmişini kronolojik akış olarak verir; aynı endpoint parametrelerle daraltılır. Tek uçtan hem tüm akışı, hem tek bir geçişi, hem de tek bir task'ı alabilirsiniz.

**Her response'da `instance` bloğu bulunur** — parametre fark etmeksizin:
- `currentState` — motorun o anda bulunduğu state adı.
- `effectiveState` — dışa açılan state adı (SubFlow/Wizard dışında `currentState` ile aynıdır).
- `status` — instance'ın yaşam döngüsü durumu (`Active`, `Busy`, `Completed`, `Faulted`, `Passive`).

Bu bilgiler, workflow definition graph'ı üzerine "hangi state'ten geçildi, şu an nerede?" overlay'i uygulamak için kullanılır — ayrı bir `/state` çağrısına gerek kalmaz.

**Parametre varyasyonları (Postman'de ayrı ayrı görünür):**
- *(parametresiz)* → tüm geçiş akışı (zaman çizelgesi çizmek için).
- `?includeTasks=true` → her geçişin **task kayıtları da aynı yanıta gömülür** — performans/darboğaz analizi için ayrı sorgu gerekmez.
- `?transitionId={id}` → yalnızca **o tek geçişin** detayı.
- `?transitionId={id}&includeTasks=true` → o geçiş + **task kayıtları** birlikte (UI'da bir geçişe tıklanınca on-demand detay).
- `?taskId={id}` → yalnızca **tek bir task** kaydı (istek/yanıt/sonuç).

> `transitionId` veya `taskId` verilirse boş GUID olamaz (aksi halde 400). Var olmayan transition/task için 404 döner.

### Güncel Veri + Versiyon Geçmişi — `GET …/instances/{instance}/data`
Instance'ın güncel JSON verisi ve tüm versiyon geçmişi. `?version=1.1.0` parametresiyle belirli bir veri versiyonu doğrudan alınabilir.

#### Görünüm (view)

Instance'ın bulunduğu state'e (ya da belirli bir transition'a) tanımlı ekran görünümünü döner.

**Ne zaman kullanılır:** Dashboard veya detay sayfasında o anki adıma ait formu, HTML içeriği veya derin bağlantıyı yüklemek gerektiğinde.

**Varyasyonlar:**
- `GET …/instances/{instance}/view` — Mevcut state'in görünümü
- `GET …/instances/{instance}/view?transitionKey=onay` — Belirli bir transition'a ait görünüm

**Dönen bilgiler:** Seçilen görünüm anahtarı, türü (json/html/markdown/deepLink/http/urn), içerik, etiketler ve tüm aday görünümler.

---

## 3. Hata Teşhisi (Operatör)

### Faulted Listesi — `GET …/instances?filter={"status":{"eq":"Faulted"}}`
Hatalı instance'ları listeler (ayrı endpoint gerekmez, filtre yeterli). `sort` ile en son hatalananı öne alabilirsiniz.

### Incident Geçmişi — `GET …/instances/{instance}/incidents`

Error boundary her tetiklendiğinde pipeline bir **incident** kaydı oluşturur. Bu endpoint, instance'ın kayıtlı incident geçmişini döner (en yeni önce, maksimum 5 kayıt).

Her incident şunları içerir:
- Hatanın oluştuğu **state**, **transition** ve **task** (task yoksa null)
- **Hata kodu** ve **katmanı** (`Transport`, `Task`, `Pipeline`)
- HTTP **status kodu** (varsa)
- Error boundary'nin aldığı **aksiyon** (`Abort`, `Retry`, `Rollback`, `Notify`, `Log`, `Ignore`)
- Eşleşen boundary **seviyesi** (`Task`, `State`, `Global`)
- `isResolved` flag'i ve `resolvedAt` — retry ile ya da boundary geçişiyle çözülüp çözülmediği
- `retryCount` — kaç retry yapıldığı
- `traceId` — distributed trace ile korelasyon için OpenTelemetry trace ID

**Ne zaman kullanılır:** `/faults` endpoint'i error boundary olmayan fault'ları da kapsar ve anlık bozuk durumu gösterir. `/incidents` ise boundary'nin devreye girdiği geçmiş olayları — başarılı retry'larla çözülmüş olanlar dahil — listeler. İki endpoint birbirini tamamlar.

**Örnek kullanım:**
- Instance daha önce birkaç kez retry oldu mu? `isResolved: true` kayıtlara bak.
- Hata hangi boundary seviyesinde yakalandı? `boundaryLevel` alanını kontrol et.
- Distributed trace'e bağlan: `traceId` değerini Jaeger/Tempo'da ara.

### Fault Detayı — `GET …/instances/{instance}/faults`
Faulted bir instance'ın **neden ve nerede** düştüğünü tek noktada toplar: tamamlanamayan geçiş + içindeki başarısız task'lar (istek/yanıt dahil). Kök neden analizi için.

### Domain genelinde faulted instance listesi

`GET {domain}/instances/faulted?filter=<graphql-json>`

Bir domaindeki tüm workflow'ları tarar ve verilen zaman aralığında **Faulted** durumuna düşmüş
instance'ları tek bir listede döner. Her kayıt hangi workflow'a (flow) ait olduğunu taşır, böylece
istemci isterse workflow'a göre gruplayabilir.

- **Zorunlu:** `filter` içinde **sınırlı bir `createdAt` aralığı** (hem alt hem üst sınır) verilmelidir;
  verilmezse `400` döner. Faulted nadir bir durumdur ve zaman aralığı sonucu küçük tutar — bu yüzden
  sayfalama yoktur, liste tam döner.
- **status vermeyin:** status bu endpoint tarafından Faulted olarak sabitlenir; `filter` içinde status
  verirseniz `400` döner.
- **Ek filtre serbest:** `attributes.*` (iş verisi) ve diğer instance kolonlarıyla ek süzme yapabilirsiniz.

Örnek: `?filter={"createdAt":{"gt":"2026-06-01T00:00:00Z","lt":"2026-06-27T00:00:00Z"}}`

### SLA Aşımı — `GET …/instances?filter={"and":[{"status":{"eq":"Active"}},{"createdAt":{"lt":"<eşik>"}}]}`
Belirli süreden uzun süredir Active kalan instance'ları bulur. Filtre kombinasyonu ile çözülür.

---

## 4. Veri Analizi (Instance datalları)

### Data — `GET …/instances/{instance}/data`
Güncel veri + versiyon geçmişi. "Veri şu an ne?" `?version=1.1.0` ile yalnızca o versiyonun verisi alınabilir.

### Data Diff — `GET …/instances/{instance}/data/diff?from={v1}&to={v2}`
İki veri versiyonu arasında **eklenen / silinen / değişen** alanları gösterir. "Veri nasıl evrildi?" Bir geçişin veriyi nasıl değiştirdiğini görmek, hatalı veri akışını ayıklamak ve denetim için.

### JSON Veri İçinde Arama — `GET …/instances?filter={"attributes":{...}}`
Instance verisinin içinde değer arama (ör. `attributes.category = finance`). Bkz. §7.

---

## 5. Alt-Süreç / SubFlow Takibi

### Hiyerarşi Ağacı — `GET …/instances/{instance}/hierarchy`
Bir instance'ın tetiklediği **SubFlow/SubProcess'lerin çok seviyeli ağacını** (parent → child → grandchild) döndürür. Çocuklar farklı workflow/schema'da olabilir; bu endpoint tümünü (tamamlanmışlar dahil) gezerek getirir. "Bu süreç hangi alt süreçleri tetikledi, hangileri takıldı?"

> Tek seviyeli ve yalnızca aktif çocuklar için Instance Detay'daki korelasyonlar da yeterlidir; tam ağaç için `hierarchy` kullanın.

### Parent Instance — `GET …/instances/{instance}/parent`
Bir alt sürecin (subflow/subprocess) bağlı olduğu **üst instance'ı** döndürür. Hiyerarşide aşağıdan yukarıya ters navigasyon için kullanılır.

**Dönen bilgiler:** `parentInstanceId`, `key` (parent'ın iş anahtarı), `flow` (parent workflow adı), `domain`, `parentState` (parent'ın o anki state'i), `correlationType`.

**Ne zaman kullanılır:** Bir alt süreç instance'ını incelerken "Bu hangi üst sürecin parçası?" sorusunu yanıtlamak için. Root instance'ta (hiyerarşinin tepesi) `parent` alanı `null` döner.

---

### Instance Task Listesi — `GET …/instances/{instance}/tasks`
Bir instance'ın tarihsel olarak çalıştırdığı **tüm task kayıtlarını** `startedAt` sıralamasıyla döndürür.

**Dönen bilgiler:** `id` (task entity ID), `taskDefinitionKey`, `status`, `businessStatus`, `startedAt`, `finishedAt`, `durationMs`.

**Ne zaman kullanılır:** "Bu instance hangi task'ları çalıştırdı, hangisi takıldı, kaç ms sürdü?" gibi genel performans ve hata analizlerinde. Bir task'ın nerede tetiklendiğini veya tam detayını görmek için tek task detay endpoint'ini kullanın.

---

### Instance Task Detayı — `GET …/instances/{instance}/tasks/{taskId}`
Tek bir task çalıştırmasının **tam detayını** döndürür.

**Dönen bilgiler:**
- Temel: `id`, `taskDefinitionKey`, `status`, `businessStatus`, `startedAt`, `finishedAt`, `durationMs`
- `triggerContext` — task'ın workflow tanımındaki lifecycle konumu (best-effort; instance'ın başladığı flow versiyonu üzerinden çözülür):
  - `slot`: `OnExecute` (geçiş sırasında) | `OnExit` (kaynak state'ten çıkarken) | `OnEntry` (hedef state'e girerken)
  - `contextType`: `"Transition"` (OnExecute) | `"State"` (OnExit / OnEntry)
  - `contextKey`: slot sahibinin anahtarı — transition key veya state key
  - `order`: slot içindeki sırası (0 tabanlı)
  - `mappingScript`: varsa tanımdaki mapping ifadesi
- `definition` — task tanımının anahtarı, türü (Http, Script, Dapr…), versiyonu ve ham `config` bloğu (best-effort)
- `input` / `output` — task çalıştırıcısına gönderilen ve dönen payload'lar
- `invocationResult` — executor'ın ham yanıtı (output mapping öncesi; statusCode, headers, metadata içerir)

**Ne zaman kullanılır:** Belirli bir task'ın neden başarısız olduğunu, nerede tanımlandığını (OnEntry mi, OnExecute mi?), ne gönderdiğini ve ne aldığını incelemek için.

---

## 6. Workflow & Component Tanımı Keşfi

### Component Özet — `GET {domain}/components?type={tip}`
Yanıt yapısı `key` parametresine göre değişir:

**Liste sorgusu** (`key` verilmeden): o tipteki yayınlanmış bileşenlerin **sayfalı** özeti döner. Yanıtta `items` dizisi ve sayfalama bilgileri (`page`, `pageSize`, `hasNext`) bulunur. Her item; `key`, `version`, `domain`, `flow`, varsa `labels` (çok dilli etiketler), varsa `type` (bileşen tip ayrımcısı) ve `createdAt` / `modifiedAt` (bileşenin ilk yayınlanma ve son güncelleme zamanı) alanlarını içerir.

**Tek bileşen detayı** (`key` verildiğinde): `items` wrapper olmadan düz bir nesne döner. Ek olarak `flow` (bileşenin ait olduğu stream, örn. `sys-flows`), `versions` (o bileşenin tüm yayınlanmış versiyon string'lerinin listesi, yeniden eskiye sıralı) ve `createdAt` / `modifiedAt` alanları bulunur. Paging bu modda uygulanmaz.

> **Not:** `key` verildiğinde dönen `versions` alanı **yalnızca versiyon string listesidir** — tarih, isLatest veya flowVersion gibi metadata içermez ve sayfalı değildir. Versiyon başına tam metadata (yayınlanma tarihi, isLatest, flowVersion) veya sayfalı versiyon listesi için `GET {domain}/components/versions` endpoint'ini kullanın.

**Parametre varyasyonları:**
- `?type=sys-flows` → 1. sayfa, 20 kayıt (varsayılan)
- `?type=sys-flows&page=2&pageSize=20` → 2. sayfa (`page` aralığı 1–1000, `pageSize` aralığı 1–100)
- `?type=sys-flows&key=<key>` → tek bileşenin detayı (en son versiyon, `flow` + `versions` dahil); paging yok sayılır
- `?type=sys-flows&key=<key>&version=<ver>` → belirli versiyonun detayı (`versions` hâlâ tüm versiyonları içerir)

Desteklenen `type` değerleri: `sys-flows`, `sys-tasks`, `sys-schemas`, `sys-views`, `sys-functions`, `sys-extensions`, `sys-mappings`.

**Ne zaman kullanılır:** "Bu domain'de hangi flow'lar yayınlanmış?" (liste) veya "Bu bileşenin hangi versiyonları var, hangi stream'de?" (tek detay) soruları için. Çok sayıda bileşen olan domain'lerde `page`/`pageSize` ile navigasyon yapılabilir. Tam tanım JSON'u için `/components/definition` endpoint'i kullanılır.

#### Filtreler

Tüm tipler için kullanılabilir (ortak):

| Parametre | Operatör | Açıklama |
|---|---|---|
| `createdAt[gte]` | `>=` | İlk yayın tarihi alt sınırı (inclusive, ISO 8601 UTC) |
| `createdAt[lte]` | `<=` | İlk yayın tarihi üst sınırı (inclusive, ISO 8601 UTC) |
| `modifiedAt[gte]` | `>=` | Son güncelleme tarihi alt sınırı |
| `modifiedAt[lte]` | `<=` | Son güncelleme tarihi üst sınırı |
| `tags[contains]` | liste-içerir | Tag listesi bu değeri içeriyorsa eşleşir (büyük/küçük harf duyarsız) |
| `flowVersion[eq]` | tam eşleşme | Flow-stream format versiyonu tam eşleşme (ör. `1.0.0`) |
| `flowVersion[contains]` | içerir | Versiyon stringi bu değeri içeriyorsa eşleşir (ör. `1.0`) |
| `key[eq]` | tam eşleşme | Bileşen key'i tam eşleşme (büyük/küçük harf duyarsız) |
| `key[contains]` | içerir | Bileşen key'i bu değeri içeriyorsa eşleşir |
| `version[eq]` | tam eşleşme | Bileşen semantik versiyonu tam eşleşme (ör. `1.0.0`) |
| `version[contains]` | içerir | Bileşen semantik versiyonu bu değeri içeriyorsa eşleşir (ör. `1.0`) |

Tip bazlı (yalnızca ilgili tip için):

| Parametre | Operatör | Geçerli tipler |
|---|---|---|
| `definitionType` | tam eşleşme | sys-flows, sys-tasks, sys-schemas, sys-views, sys-extensions |
| `renderer` | tam eşleşme | sys-views |
| `display` | tam eşleşme | sys-views |
| `scope` | tam eşleşme | sys-functions, sys-extensions |
| `name[eq]` | tam eşleşme | sys-mappings |
| `name[contains]` | içerir | sys-mappings |

**Kural:** Aynı alan için hem `[eq]` hem `[contains]` verilirse API `400` döner (ör. `?key[eq]=x&key[contains]=y`, `?version[eq]=1.0.0&version[contains]=1.0`).

**Not:** `?key=abc` (düz, operatörsüz) tek bileşen lookup modudur — pagination olmadan tek öğe veya 404 döner. Filter modu değildir.

Başka bir type için desteklenmeyen bir filtre gönderildiğinde `400 Bad Request` döner ve hangi parametrenin o type için geçersiz olduğu açıklanır.

**Örnekler:**
```
# 2026 başından itibaren yayınlanan workflow tanımları
GET /monitor/{domain}/components?type=sys-flows&createdAt[gte]=2026-01-01T00:00:00Z

# "order" içeren key'e sahip workflow tanımları (kısmi arama)
GET /monitor/{domain}/components?type=sys-flows&key[contains]=order

# Tam key eşleşmesi ile liste modu
GET /monitor/{domain}/components?type=sys-flows&key[eq]=order-flow

# Belirli format versiyonuna sahip bileşenler
GET /monitor/{domain}/components?type=sys-flows&flowVersion[eq]=1.0.0

# "default" renderer kullanan view bileşenleri
GET /monitor/{domain}/components?type=sys-views&renderer=default

# Global scope'taki function tanımları
GET /monitor/{domain}/components?type=sys-functions&scope=global

# 400 — renderer sys-flows için desteklenmez
GET /monitor/{domain}/components?type=sys-flows&renderer=default

# Belirli semantic versiyon
GET /monitor/{domain}/components?type=sys-flows&version[eq]=1.0.0

# Tüm 1.0.x versiyonları
GET /monitor/{domain}/components?type=sys-flows&version[contains]=1.0

# 400 — aynı alan için hem [eq] hem [contains]
GET /monitor/{domain}/components?type=sys-flows&key[eq]=order-flow&key[contains]=order
```

### Component Versiyon Listesi — `GET {domain}/components/versions?type={tip}&key={key}`

Belirli bir bileşenin yayınlanmış **tüm versiyonlarını sayfalı** listeler. Her versiyon için tam metadata döner. `type` ve `key` her ikisi de **zorunludur**.

**Her versiyon kaydında:**
- `version` — semantik versiyon string'i (ör. `1.0.3-pkg.1.22.0+credit`)
- `isLatest` — bu versiyonun şu an aktif (en güncel) olup olmadığı
- `flowVersion` — flow-stream format versiyonu (ör. `1.0.0`); legacy bileşenlerde null
- `publishedAt` — bu versiyonun yayınlandığı UTC zaman damgası

Sonuçlar `isLatest` önce, ardından `publishedAt` azalan sırasıyla döner (en yeni versiyon başta).

**Parametre varyasyonları:**
- `?type=sys-flows&key=<key>` → o workflow'un tüm versiyonları, 1. sayfa (varsayılan pageSize: 20)
- `?type=sys-flows&key=<key>&page=2&pageSize=10` → 2. sayfa, 10 kayıt
- `?type=sys-tasks&key=<task-key>` → task bileşeni versiyonları (tüm tipler desteklenir)

**Ne zaman kullanılır:** "Bu workflow'un kaç versiyonu yayınlandı?", "En son versiyon hangisi, ne zaman yayınlandı?", "Eski versiyonlara ne zaman geçildi?" sorularını yanıtlamak için. Dashboard'da versiyon seçici veya yayın geçmişi göstermek için idealdir.

Desteklenen `type` değerleri: `sys-flows`, `sys-tasks`, `sys-schemas`, `sys-views`, `sys-functions`, `sys-extensions`, `sys-mappings`.

### Workflow Versiyon Listesi (alias) — `GET {domain}/workflows/{workflow}/versions`

`GET {domain}/components/versions?type=sys-flows&key={workflow}` ile **birebir aynı yanıtı** döner; workflow-centric route olduğundan `type` parametresi gerekmez, `{workflow}` segment'i key olarak kullanılır. Aynı sayfalama ve sıralama kuralları geçerlidir.

**Ne zaman kullanılır:** Workflow odaklı çalışırken `components/versions` yerine daha kısa ve okunabilir bir route tercih edildiğinde.

### Component / Workflow Definition — `GET {domain}/components/definition?type={tip}&key={key}&version={ver}`
Kullanıcının vNext'e kaydettiği tanımları **yayınlanmış haliyle, versiyonlu** okur. Workflow tanımı zaten state+transition yapısını içerir (grafik için ayrı endpoint'e gerek yoktur).

**Parametre varyasyonları:**
- `type=sys-flows` → o tipteki **tüm** tanımlar; `{ "pagination": {...}, "items": [...] }` formatında sayfalı liste döner.
- `type=sys-flows&key=<key>` → o anahtarın **en son versiyonu**; `items` dizisine sarılmadan **doğrudan definition nesnesi** döner.
- `type=sys-flows&key=<key>&version=<ver>` → **belirli versiyon**; yine doğrudan definition nesnesi döner (her tür için geçerlidir).

Desteklenen `type` değerleri: `sys-flows`, `sys-tasks`, `sys-schemas`, `sys-views`, `sys-functions`, `sys-extensions`.

### Workflow Bağımlılıkları — `GET {domain}/workflows/{workflow}/dependencies`
Bir workflow tanımının kullandığı **tüm bileşenleri** (task, schema, view, function, extension, subflow) ve her birinin hangi adımda referans edildiğini listeler. Tanım analizini otomatikleştirir; elle okumaya gerek kalmaz.

**Parametre varyasyonları:**
- *(parametresiz)* → en son yayınlanmış versiyonun bağımlılıkları
- `?version=1.5.0` → belirli bir tanım versiyonunun bağımlılıkları

**Dönen bilgiler:** `workflow` (workflow kimliği), `tasks`, `schemas`, `views`, `functions`, `extensions`, `subFlows` — her biri `key`, `version`, `domain` ve `referencedFrom` (hangi transition/state'den referans edildiği) alanlarını içeren listelerdir.

**Ne zaman kullanılır:** "Bu workflow'u güncellemeden önce hangi bileşenleri etkileyeceğim?" veya "Bu task/schema başka hangi flow'lar tarafından kullanılıyor?" soruları için. Etki analizi ve bileşen envanteri.

### Domain Component Sayım Özeti — `GET {domain}/stats/components`
Domain genelinde yayınlanmış her bileşen tipinden kaç adet olduğunu tek çağrıda döndürür.

**Dönen bilgiler:** `flows`, `tasks`, `schemas`, `views`, `functions`, `extensions` (her tip için ayrı sayı) ve bunların toplamı olan `total`.

**Ne zaman kullanılır:** "Bu domain'de kaç workflow, kaç task tanımı var?" gibi bir envanter özeti gerektiğinde ya da dashboard açılışında bileşen kataloğunun büyüklüğünü göstermek için.

---

## 6b. Aktif Job & Zamanlayıcı İzleme

Workflow'lara bağlı **zamanlanmış job'ları** (scheduled jobs, timer'lar) görüntülemek için kullanılır. Tetiklenmesi beklenen veya hâlâ çalışan zamanlayıcıları ve hangi instance için tanımlandıklarını gösterir.

### Workflow Bazlı Aktif Job'lar — `GET {domain}/workflows/{workflow}/jobs`
Belirli bir workflow altındaki aktif job kayıtlarını listeler.

**Dönen bilgiler:** Her kayıt için `jobId`, `name` (job adı), `flow` (workflow adı), `domain`, `instanceId` (job'un bağlı olduğu instance), `isActive`, `createdAt`, `modifiedAt`.

**Sayfalama:** Bu endpoint sayfalı sonuç döner. `page` (varsayılan: 1, en fazla 1000) ve `pageSize` (varsayılan: 20, en fazla 100) parametreleriyle hangi sayfanın isteneceği belirlenir. Yanıt standart sayfalama zarfında gelir: `pagination` nesnesi (`page`, `pageSize`, `hasNext`) ve `items` listesi.

**createdAt aralığı (opsiyonel):** `createdAt[gte]` ve `createdAt[lte]` parametreleri ile sonuçları belirli bir zaman dilimiyle daraltabilirsiniz. Her iki parametre de ISO 8601 UTC biçiminde girilir (örn. `2026-06-01T00:00:00Z`), sınırlar dahildir. Aralık verilmezse tüm aktif job'lar döner. Yalnızca biri verilirse API **400** döner — ikisi birlikte ya da hiç verilmemelidir.

**Ne zaman kullanılır:** "Bu workflow'un bekleyen zamanlayıcıları var mı?", "Hangi instance'lar hâlâ job kuyruğunda?" sorularını yanıtlamak için. Belirli bir güne ait job'ları görmek istediğinizde aralık parametrelerini ekleyin; çok sayıda kayıt varsa `page`/`pageSize` ile sayfalayın.

**Yanıt yapısı:**
```json
{
  "pagination": { "page": 1, "pageSize": 20, "hasNext": true },
  "items": [
    { "jobId": "...", "name": "...", "instanceId": "...", "flow": "...", "domain": "...", "isActive": true, "createdAt": "...", "modifiedAt": "..." }
  ]
}
```

**Örnekler:**
```
GET {domain}/workflows/{workflow}/jobs
GET {domain}/workflows/{workflow}/jobs?page=1&pageSize=20
GET {domain}/workflows/{workflow}/jobs?createdAt[gte]=2026-06-01T00:00:00Z&createdAt[lte]=2026-06-27T23:59:59Z&page=1&pageSize=20
```

### Domain Genelinde Aktif Job'lar — `GET {domain}/jobs`
Domain'deki tüm workflow'ların aktif job'larını tek çağrıda döndürür.

**createdAt aralığı (zorunlu):** Bu endpoint için `createdAt[gte]` ve `createdAt[lte]` parametrelerinin **ikisi birden verilmesi zorunludur**. Aralık verilmezse ya da yalnızca biri sağlanırsa API **400** döner. Sınırlar dahildir, ISO 8601 UTC biçiminde girilir. `gte` değeri `lte` değerinden büyük olamaz; olursa 400 döner.

**Sayfalama desteklenmez:** Bu endpoint `page` veya `pageSize` parametresi **kabul etmez**. Bu parametreler verilirse API **400** (`jobs.paginationNotSupported`) döner. Sayfalama ihtiyacı varsa workflow bazlı endpoint (`{domain}/workflows/{workflow}/jobs`) kullanılmalıdır.

**Ne zaman kullanılır:** Domain genelinde belirli bir zaman aralığındaki bekleyen/aktif zamanlayıcıları toplu olarak görmek için. Büyük domain'lerde tüm workflow'ların sonuçlarını kapsayan geniş bir görünüm sunar.

**Yanıt yapısı:** `pagination` alanı **yoktur** (yalnızca `items` döner):
```json
{
  "items": [
    { "jobId": "...", "name": "...", "instanceId": "...", "flow": "...", "domain": "...", "isActive": true, "createdAt": "...", "modifiedAt": "..." }
  ]
}
```

**Örnek:**
```
GET {domain}/jobs?createdAt[gte]=2026-06-01T00:00:00Z&createdAt[lte]=2026-06-27T23:59:59Z
```

---

## 7. Esnek Arama — GraphQL Filtre Altyapısı

Instance listesi (`…/instances`) **JSON nesnesi** biçiminde `filter` alır. Pek çok ihtiyaç yeni endpoint olmadan buradan karşılanır.

> **Söz dizimi:** `filter={"<alan>":{"<operatör>":<değer>}}` — eski `field op 'value'` string formu **değil**.

Örnekler:
```
filter={"status":{"eq":"Faulted"}}&sort={"field":"modifiedAt","direction":"desc"}
filter={"attributes":{"category":{"eq":"finance"}}}
filter={"and":[{"status":{"eq":"Active"}},{"createdAt":{"gt":"2026-06-08T00:00:00Z"}}]}
filter={"attributes":{"payment":{"amount":{"ge":1000}}}}
```

- **Operatörler:** `eq, ne, gt, ge, lt, le, between, like, match, startswith, endswith, in, nin, isNull`
- **`status`** yalnızca `eq, ne, in, nin` destekler.
- **Mantıksal:** `and, or, not`
- **Filtrelenebilir instance kolonları:** `key, flow, currentState, state, status, stateType, stateSubType, createdAt, modifiedAt, completedAt, isTransient`. JSON veri alanları `attributes.` öneki ile.
- **Gruplama (`groupBy`):** yalnızca **JSON veri path'i** (`attributes.*`) üzerinde çalışır; **status/currentState gibi instance kolonlarında çalışmaz** — bunlar için §1'deki **Durum Sayaçları** ve **Canlı State Dağılımı** endpoint'lerini kullanın.

Ayrıntılı örnekler: [monitoring-filter-guide.md](monitoring-filter-guide.md).

---

## 8. Sağlık & Operasyon

| Endpoint | Amaç |
|----------|------|
| `GET /monitor/health/detail` | Redis + Orchestrator + PostgreSQL detaylı sağlık |
| `GET /health` · `/ready` · `/live` | Özet sağlık ve Kubernetes probe'ları |
| `GET /version` | Çalışan sürüm |
| `GET /metrics` | Prometheus scrape endpoint'i |
| `GET /api/v1.0/config` | Runtime yapılandırma özeti (secret'sız) |

### Runtime Config — `GET /api/v1.0/config`
Monitor API'nin **çalışma zamanı yapılandırma özetini** döndürür. Connection string, secret veya token **asla dönmez**.

**Dönen bilgiler:** `applicationName` (appsettings'teki uygulama adı), `runtimeVersion` (assembly sürümü), `monitor.redisMode` (ör. `Standalone`/`Cluster`), `monitor.tracingEnabled`, `monitor.metricsEnabled`, `monitor.vaultEnabled`.

**Ne zaman kullanılır:** Hangi ortamda çalıştığını doğrulamak, telemetri toggle'larını kontrol etmek veya versiyon bilgisini almak için.

---

## 8b. Yetkilendirme Matrisi & İzin Sorgulama

Bir workflow tanımında kimin neyi yapabileceğini (state görüntüleme rolleri, transition tetikleme rolleri, function rolleri) **tanım bazında, kural değerlendirmesi yapmadan** okumak için kullanılır.

### Workflow Yetki Matrisi — `GET {domain}/workflows/{workflow}/permissions`
Tüm yetki bilgisini tek çağrıda döndürür: workflow geneli `queryRoles`, state bazlı görüntüleme rolleri, her transition için tetikleme rolleri ve function rolleri.

**Varyasyonlar:**
- *(parametresiz)* → tam matris; tüm roller döner.
- `?role=morph-idm.maker` → filtreli görünüm: **yalnızca bu role ait girdiler** döner. queryRoles, state rolleri, transition rolleri ve function rolleri içinden yalnızca o roleun geçtiği satırlar listelenir; diğerleri yanıtta yer almaz. "Bu rolün workflow genelinde hangi yetkisi var?" sorusunu yanıtlar.
- `?version=1.5.0` → belirli tanım versiyonunun yetki haritası.

### Instance Mevcut Durumu Üzerinden İzin Görünümü — `GET {domain}/workflows/{workflow}/instances/{instance}/permissions`
Instance'ın **o anki state'ine** özgü rol bilgilerini döner. Workflow matrisiyle aynı alan adlarını kullanır, ancak yalnızca mevcut durumla ilgili kesimleri içerir:

- **`queryRoles`** — workflow genelinde geçerli query rolleri (workflow matrisindeki `queryRoles` ile aynı alan adı).
- **`state`** — instance'ın bulunduğu state'in izin kaydı: `{ key, queryRoles }`. Workflow matrisindeki `states` dizisinin eleman yapısıyla aynıdır.
- **`transitions`** — o state'ten geçilebilecek transition'ların rol bilgileri (hem state'e bağlı hem de o state için geçerli shared transition'lar). Her transition nesnesi `key`, **`from`** (kaynak state), `target` (hedef state) ve `roles` alanlarını içerir.
- **`functions`** — workflow'a bağlı function'ların rol bilgileri.

**Varyasyonlar:**
- *(parametresiz)* → instance'ın çalıştığı flow versiyonuna ait izin bilgileri döner.
- `?version=1.5.0` → instance'ın versiyonu yerine belirtilen flow versiyonunun izin bilgileri döner. Versiyon karşılaştırması veya güncel tanımla denetim için kullanılır.
- `?role=morph-idm.maker` → yalnızca o role ait girdiler döner; diğer roller yanıtta yer almaz.

Kullanım amacı: "Bu instance şu an hangi rollere açık, kim ne yapabilir?" sorusunu instance'ın canlı durumu üzerinden yanıtlamak. Dashboard'da instance bazlı erişim denetimi göstergesi için idealdir.

### Transition Rolleri Alt Görünümü — `GET {domain}/workflows/{workflow}/permissions/transitions`
Matrisin yalnızca transition bölümünü döndürür. "Hangi geçiş hangi roller tarafından tetiklenebilir?" sorusunu yanıtlar; tam matrisi indirmeye gerek kalmaz.

### Function Rolleri Alt Görünümü — `GET {domain}/workflows/{workflow}/permissions/functions`
Matrisin yalnızca function bölümünü döndürür. Workflow'a bağlı function'ların rol gereksinimlerini görmek için.

---

## 8c. İstatistikler & Performans Analizi

Bir workflow'un ne kadar hızlı çalıştığını, nerede hata ürettiğini ve hangi adımların darboğaz oluşturduğunu görmek için kullanılır. Tüm istatistikler, o workflow'a ait instance verilerinden gerçek zamanlı hesaplanır.

### Hata İstatistikleri — `GET {domain}/workflows/{workflow}/stats/faults`
Faulted instance sayısını, hangi state ve task'ta hata yığıldığını ve son 1 saat / 24 saat / 7 günlük hata trendini döndürür. "Sorun büyüyor mu küçülüyor mu?" ve "hangi adım en çok hata üretiyor?" sorularını yanıtlar.

**Dönen bilgiler:** `totalFaulted`, `byState` (state bazlı sayımlar), `byTask` (başarısız task bazlı sayımlar), `trend` (zaman dilimine göre faulted instance sayısı).

### Task Çalıştırma İstatistikleri — `GET {domain}/workflows/{workflow}/stats/tasks`
Her task'ın kaç kez çalıştığını ve başarı/hata oranını gösterir. Hangi adımların hata ürettiğini ya da tutarsız çalıştığını görmek için.

**Dönen bilgiler:** `byTask` (her task için çalıştırma sayısı, başarı oranı, hata oranı).

### Instance Tamamlanma Süresi — `GET {domain}/workflows/{workflow}/stats/duration`
Tamamlanmış instance'ların ortalama, minimum ve maksimum süresini ve tamamlanma sayısını döndürür. Uzun süren süreçleri tespit etmek ve SLA karşılaştırması yapmak için.

**Dönen bilgiler:** `avgMs`, `minMs`, `maxMs` (milisaniye), `completedCount`.

### Geçiş İstatistikleri & Akış Yoğunluğu — `GET {domain}/workflows/{workflow}/stats/transitions`
Her transition'ın tetiklenme sayısını, tamamlanma oranını ve tetikleyici tipini (manuel / otomatik / zamanlanmış / olay) verir. Ayrıca "hangi state'ten hangi state'e ne sıklıkta geçildi" akış yoğunluğu (flow density) bilgisi döner — süreç haritasını sayılarla zenginleştirmek için.

**Dönen bilgiler:** `byTransition` (her geçiş için sayım, tamamlanma oranı, tetikleyici tipi dağılımı), `flowDensity` (state çifti bazlı geçiş sayısı).

---

## 8d. Function Tanımları

Function'lar vNext'te tanım-bazlı, istek anında çalışan bileşenlerdir (BFF / hesaplama endpoint'leri). Her çağrı geçici olduğundan çalıştırma geçmişi tutulmaz; Monitor bu endpoint'lerle yalnızca **tanım** bilgisini (key, version, scope, taskCount, roles) sunar.

### Domain-Scope Function Tanımları — `GET {domain}/functions/scope`

Domain genelinde yayımlanmış, `Domain` scope'undaki function tanımlarını listeler. Bu function'lar herhangi bir workflow'a kayıt gerekmeksizin domain genelinden çağrılabilir.

**Dönen bilgiler (her item):** `key`, `version`, `scope` (`"Domain"`), `taskCount`, `roles` (tanımlanmışsa).

**Kullanım:** "Bu domain'de hangi domain-scope function'lar mevcut?" sorusunu yanıtlar.

### Instance Workflow Function Tanımları — `GET {domain}/workflows/{workflow}/instances/{instance}/functions/scope`

Belirli bir instance'ın çalıştığı workflow versiyonuna kayıtlı tüm function tanımlarını listeler. Instance'ın `FlowVersion` bilgisi kullanılır; workflow güncellenmiş olsa bile instance'ın başlatıldığı versiyondaki tanımlar gösterilir.

**Dönen bilgiler:** Aynı schema — `key`, `version`, `scope`, `taskCount`, `roles`.

**Kullanım:** "Bu instance'ın workflow'u hangi function'lara sahip?" sorusunu yanıtlar. Yalnızca workflow'un `functions` listesinde açıkça kayıtlı olanlar döner.

---

## 9. Şu An Kapsam Dışı (Sonraki Fazlar)

- Function endpoint'leri (view, schema, authorize, authorization matrix, domain functions)
- Cross-schema / multi-domain birleşik özet (domain-wide sayaç şu an `public` schema ile sınırlı; parent instance cross-schema desteği de bu fazda)
- Audit trail, diagnostik endpoint'leri (cache/DB/script)
- ETag / koşullu GET (`If-None-Match`), WebSocket/SignalR canlı bildirim, veri export

Tam ileriye dönük harita ve gerekçeler: [docs/upcoming/vnext-monitoring-upcoming-features.md](../upcoming/vnext-monitoring-upcoming-features.md).
