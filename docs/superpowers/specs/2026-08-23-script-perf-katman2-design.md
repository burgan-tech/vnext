# Katman 2 — Serialization/ScriptContext Optimizasyonları (Design Spec)

**Tarih:** 2026-08-23 · **Durum:** Onaylandı (brainstorming oturumu) · **Branch:** `feature/script-perf-katman0` (tek branch — kullanıcı kararı)
**Baz:** `ai-docs/script-perf-analysis-2026-08-23.md` (B1-B3, B6-B10) · Baseline'lar: mikro `test/BBT.Workflow.Benchmarks/baselines/2026-08-23-master.md`, makro vnext-example `core/Workflows/script-perf-lab/README.md` (Katman 1 sonrası: instance başına ~65 MB alloc — bu katmanın hedefi)

## Amaç

ScriptContext ve instance-data yollarındaki JSON churn'ünü kaldırmak: append zincirinin ~8 doküman geçişini ~2'ye indirmek (B9), FanOut/parallel branch klonunu copy-on-write'a çevirmek (B6, 500 LOH alloc → ~0), instance-data okumalarını memoize etmek (B1-B3), audit'ten task tanımını çıkarmak (B8) ve küçük kaçakları kapatmak (B7, B10c, B10d). **Versiyonlama sözleşmesi korunur**: her task satırı anında persist, full-merge model, immutable satırlar.

## Alınan kararlar

| Karar | Seçim |
|---|---|
| B2/B3 expando memo'su | **Memoize + davranış değişimi kabul** (Katman tartışması, 2026-08-23): script mutasyonları transition içinde görünür olur; release note + migrations.json |
| B8 audit | **Task tanımı yerine referans** `{key, version, domain, flow, taskType}`; ScriptCode persist edilmez |
| B6 branch | **Copy-on-Write** (Yaklaşım A): parent paylaşımı + ilk yazımda parça-bazlı kopya; read-only sarmalayıcı (B) politikaya aykırı diye reddedildi |
| B2/B3 memo yeri | **Instance-entity seviyesi** (Yaklaşım A): pipeline-geneli kazanım, tek davranış; kendini-doğrulayan anahtar sayesinde invalidasyon hook'suz |
| B9 derinliği | Cerrahî (model korunur): `JsonNode`-DOM gibi derin yeniden tasarım REDDEDİLDİ — anında-persist satır sözleşmesiyle çelişir |
| B5 | Kapsam DIŞI — analizde yanlışlanmıştı (MergeToBody in-place, O(yeni içerik)) |

---

## 1. B1 + B2/B3 — Parse ve expando memo'ları

### 1.1 `JsonData.JsonElement` memo'su (B1)

- `_jsonElement` lazy alanı; emsal aynı sınıftaki `NormalizedJson` (`_normalizedJson`). `Json` yalnız ctor'larda atanır (doğrulanmış) → koşulsuz güvenli. `JsonSerializer.Deserialize<JsonElement>` document-bağımsız element döndürür → yaşam süresi sorunu yok. Benign-race yazım yeterli.
- 22 gerçek erişim noktasının tamamı kazanır; `JsonData.Merge` içi çift-parse ve `InstanceDataWriteService`'teki schema-validation parse'ı dahil.
- **Benchmark bağımlılığı:** `InstanceDataAccessBenchmarks.ParseJsonElement` yorumu gereği AYNI değişiklikte taze-`JsonData`-per-invocation'a çevrilir (aksi hâlde memo'lu yolu ölçüp yanlış "hızlanma" raporlar).

### 1.2 Instance-entity attribute memo'su (B2/B3) — kendini-doğrulayan anahtar

- `InstanceData.Attributes`: satır-içi lazy memo (`_attributes ??= Data.JsonElement.ToDynamic()`). Satırlar immutable → invalidasyon gerekmez.
- `Instance.Data`: memo `(en-son-satır kimliği, expando)` çifti olarak saklanır; her okuyuşta güncel latest satır referansıyla karşılaştırılır — eşleşmezse yeniden hesaplanır. **Invalidasyon hook'u YOK** (append noktalarına coupling sıfır); yeni satır append edilince latest değişir, memo kendiliğinden bayatlar. Latest-satır tespiti mevcut karşılaştırıcıyla yapılır; "satır listesi yalnız büyür" varsayımı plan aşamasında koddan doğrulanır (satır silme/replace yolu varsa anahtar satır-referansı üzerinden kalır — yine doğru).
- **Davranış değişimi (kabul edilmiş):** aynı expando ağacı erişimler arasında paylaşılır → script'in `context.Instance.Data.x = 5` yazımı bugün her erişimde taze ağaç geldiği için kaybolurken, artık aynı transition içindeki SONRAKİ okumalara görünür olur. Persist DAVRANIŞI DEĞİŞMEZ: kalıcılık hâlâ yalnız `ScriptResponse.Data` delta'sı üzerinden. Pinleyici test iki yönü de belgeler (görünürlük VAR, persist YOK). `migrations.json` kaydı + release note.

## 2. B9 — Tek-geçişli append + hash: `JsonCanonicalizer`

### 2.1 Tasarım

- Yeni bileşen `JsonCanonicalizer` (Domain, Shared/Merging yanına): `(JsonElement baseDoc, JsonElement delta)` → **tek `Utf8JsonWriter` geçişinde** derin-merge + kanonik (sıralı-anahtar) yazım; SHA-256 aynı çıktı buffer'ı üzerinden. Çıktı: `(normalizedJson, dataHash)`.
- Append akışı: base = `LatestData.JsonElement` (B1 memo'lu) → canonicalizer → `JsonData` **önceden-normalize kurucusuyla** oluşturulur (`_normalizedJson` ve hash dolu) → downstream `NormalizedJson`/`ComputeDataHash` çağrıları bedava; schema validation yeni JsonData'nın memo'lu `JsonElement`'ini paylaşır. **~8 doküman geçişi → ~2** (1 merge+canonical yazım + 1 validation/memo parse'ı).

### 2.2 Byte-parite ZORUNLULUĞU

- Merge semantiği `ObjectMerger.MergeValues` ile, kanonikleştirme mevcut `NormalizeJson` çıktısıyla **byte-byte** aynı olmalı. Sayılar ham metniyle yazılır (`GetRawText` → `WriteRawValue`); anahtar sıralama kuralı (comparer/kültür) mevcut koddan birebir alınır.
- Kanıt: eski yol **test-oracle** olarak korunur; kenar-durum korpusu (sayı formatları `1.0/1e5/-0`, unicode, escape'ler, null/boş, derin iç içe, diziler, duplicate-merge senaryoları) + script-perf-lab gerçek payload'ları üzerinde property-parite testleri.
- `DataHash` tüketicileri audit edilir (satırlar-arası hash karşılaştırması var mı); parite hedefi tüketiciden bağımsız korunur.

> **Implementasyon düzeltmesi (2026-08-23, koddan doğrulandı):** legacy `PlanAppend`'in **head-null
> (ilk append) dalı `Merge`'i tamamen atlar** — Content = delta olduğu gibi, NormalizedJson yalnız
> sort uygular (camelCase/sayı-reformat YOK; o dönüşümler Merge'in Expando round-trip'inden gelir).
> Bu yüzden head-null dalı canonicalizer'dan GEÇİRİLMEZ — iki pipeline'da da birebir aynı kalır;
> parite testi (`..._WhenHeadIsNull`, PascalCase anahtar + lexical `1.50`) bunu pinler.

### 2.3 Kill-switch

- `Workflow:InstanceData:LegacyAppendPipeline` (default `false`): `true` → eski çok-geçişli yol aynen çalışır. Persist edilen veri yolu olduğu için ucuz üretim sigortası. COW (B6) bellek-içi olduğundan flag almaz. Flag'ın iki konumu da testlidir; kaldırılması ileriki sürümün işi (deprecations kaydı gerekmez — internal config, dokümante edilir).

## 3. B6 — Copy-on-Write branch

### 3.1 Paylaşım + `EnsureOwned`

- `CreateParallelBranch` derin kopyayı bırakır; branch parent'ın şu parçalarını **referansla** paylaşır: Body (expando ağacı), `TaskResponse`/`OutputResponse` sözlükleri, `EventPayload`, `MetaData`.
- Her mutasyon giriş noktası branch'te `EnsureOwned(part)` bekçisinden geçer: ilk yazımda YALNIZ o parça mevcut klon yardımcılarıyla özel kopyaya alınır. **Yazıcıların tam envanteri plan aşamasında `ScriptContext`'in public yüzeyinden çıkarılır** ve spec'in ekine işlenir; envanter-dışı kalan bir yazıcı kalmaması için branch modunda "paylaşılan parçaya yazım" debug-assert'i eklenir.
- Eşzamanlılık: paylaşılan parçalara hiçbir yazım gitmez (yazımlar owned kopyaya) → paylaşılanlarda yalnız eşzamanlı okuma; parent join'e kadar kendi mutasyonlarını yapmaz (TaskCoordinator akışından plan aşamasında pinlenir). ExpandoObject saf-okuma paylaşımı güvenlidir; assert bunu da korur.
- FanOut'un tipik item mapping'i (yalnız `ItemInputHandler`) hiç yazmaz → maliyet ~sıfır; **500 item LOH klonu → 0**. `TaskCoordinator` paralel grupları değişmeden çalışır (merge, branch'in owned parçalarını okur); yazan mapping bugünkü izolasyonun aynısını görür.

### 3.2 Instance: shallow-snapshot

- `Instance.CreateSnapshot` satır İÇERİKLERİNİ kopyalamaz: satırlar immutable olduğundan yalnız **liste kopyası** yapılır (satır objeleri paylaşılır). Entity-durum izolasyonu (status vb.) bugünkü gibi snapshot'ta kalır. Mevcut implementasyon zaten shallow ise madde no-op olarak raporlanır.

## 4. B8 + küçükler

- **B8:** `TaskExecutionEngine`'in `SetRequest` payload'ında tam task tanımı yerine `{ key, version, domain, flow, taskType }` referansı (+`InputResponse` aynen). `Task` anahtar adı korunur (tüketici uyumu), içeriği küçülür; ScriptCode transition record'a kopyalanmaz. Transition-record `Request` tüketicileri plan aşamasında taranır; `migrations.json` kaydı.
- **B7:** `JsonEquivalent` → `JsonElement.DeepEquals` (net10) — çift-serialize sıfırlanır.
- **B10c:** `ScriptContextBuilder`'da `CurrentTransition` body+header parse'ı `Lazy<>`.
- **B10d:** `SetBody` girdisi zaten expando ise serialize+reparse atlanır.
- **B8-yan:** `TaskExecutionEngine`'deki ikinci response serialize'ı `RawInvocationResultJson` ile aynıysa yeniden kullanılır; değilse dokunulmaz (plan aşamasında eşitlik kontrolü).

## 5. Kapsam dışı

- Append/versiyonlama modelinin derin yeniden tasarımı; `IgnoreCycles`'ın global options'tan çıkarılması (B10b — tüketici etkisi belirsiz, ayrı iş); Katman 3 kalemleri; metrik/API yüzeyinde değişiklik.

## 6. Doğrulama

- **Parite korpusu (omurga):** eski merge+normalize oracle'ına karşı byte-parite property testleri (üretilmiş kenar durumları + lab payload'ları). `DataHash` tüketici audit'i.
- **COW:** branch yazımı parent'ı değiştirmez; parent join sonrası yazabilir; eşzamanlı branch'ler izole; FanOut discard yolunda paylaşılan parçalara **referans-eşitliği** (kopyasızlık kanıtı); debug-assert tetiklenme testi.
- **Memo:** append sonrası memo bayatlar; mutasyon-görünürlüğü iki yönlü pinlenir (görünür ama persist edilmez).
- **Mikro:** `AppendPath`/`ParallelBranch`/`InstanceDataAccess` yeniden; `ParseJsonElement` benchmark'ı taze-instance'a çevrilir (aynı değişiklikte).
- **Makro:** lab aynı parametrelerle; hedef (rapor): alloc dağında belirgin küçülme (~65 MB/instance'tan aşağı), 16KB p50/p95 düşüşü; `miss=+0` ve execution sayıları değişmez.
- **Integration:** en yüksek regresyon riski katmanı — script-perf-lab + **chain-busy + fan-out suite'leri** lokal runtime'da (CLAUDE.local.md politikası). İsim-diff regresyonu (master worktree yöntemi).
- Kill-switch her iki konumda testli.

## 7. Başarı kriterleri

- [x] Parite korpusu yeşil; DataHash audit sonucu kayıtlı.
- [x] COW izolasyon + kopyasızlık testleri yeşil; FanOut yolunda klon allocation'ı ~0 (mikro `ParallelBranch` + makro LOH ile kanıtlı).
- [x] Mikro/makro önce-sonra tabloları kayıtlı; integration suite'ler (script-perf-lab, chain-busy, fan-out) yeşil.
- [x] Davranış kayıtları migrations.json'da (expando görünürlüğü, B8 şekli); kill-switch testli.
- [x] Metrik/API yüzeyi değişmedi; isim-diff temiz.

## 8. Riskler

1. **Kanonikleştirme byte-parite kaçağı** (persist edilen normalize/hash farklılaşır) → parite korpusu + kill-switch + DataHash audit.
2. **COW mutasyon-yüzeyi eksik envanteri** (bekçisiz yazıcı paylaşılana yazar) → plan aşamasında tam envanter + branch-modu debug-assert + izolasyon testleri.
3. **Expando paylaşımının beklenmedik okuyucusu** (memo'lu ağacı mutate edip başka okuyucuyu etkileyen mevcut RUNTIME kodu — script değil) → plan aşamasında `Instance.Data`/`Attributes` tüketicilerinde mutate eden var mı taraması; varsa o nokta savunmalı kopyaya alınır.
4. **B8 tüketici kırılması** → tarama + `Task` alan adı korunumu + migrations kaydı.
5. Katman genişliği (Domain'in kalbi) → görev-başına iki aşamalı inceleme + geniş integration seti; kill-switch yalnız B9'a (en riskli persist yolu).
