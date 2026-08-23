# Instance-Data Sayısal Hassasiyet Fix'i (Design Spec)

**Tarih:** 2026-08-23 · **Durum:** Onaylandı (brainstorming oturumu) · **Branch:** `feature/script-perf-katman0`
**Kaynak:** Katman 2 parite çalışmasında keşfedilen pre-existing kayıp (chip `task_e9b9ab37`) · İlgili spec: `2026-08-23-script-perf-katman2-design.md` §2

## Problem

Instance-data append'i her sayıyı `TryGetInt32 → GetDouble` merdiveninden geçiriyor
(`ExpandoObjectJsonConverter.Read`, legacy; `JsonCanonicalizer`, yeni pipeline — parite için bilinçli
replika). Sonuçlar:

| Girdi | Bugün persist edilen | Kayıp |
|---|---|---|
| `9223372036854775807` (`long.MaxValue`) | `9223372036854775808` | **int64'e sığmayan değer — bozulma** |
| `9007199254740993` (2^53+1) | `9007199254740992` | sessiz off-by-one |
| `0.12345678901234567890` | `0.12345678901234568` | 3 hane |
| `1234567890123456.78` | `1234567890123456.8` | 3 kuruş |

"JS-number uyumluluğu" gerekçesi `1.0`→`1` ve `2.50`→`2.5` normalizasyonunu savunur; yukarıdaki üç
maddeyi savunmaz:

1. int64 dışına taşan bir tamsayı üretmek normalizasyon değil, bozulmadır (downstream `GetInt64`/`bigint` taşar).
2. **Dokunulmayan alanlar değişir:** `status` alanına yapılan bir append, dokümanın başka yerindeki
   `amount`'u yeniden yazar (merge tüm ağacı round-trip'ten geçirir).
3. **1. satır ile 2. satır aynı fikirde değil:** ilk append `Merge`'i atladığı için verbatim kalır,
   sonrakiler yeniden biçimler → sahte audit diff'i + bir kerelik dedupe kaçağı.

Erişilebilirlik kanıtlı: örnek domain'de 38 korumasız `number` alanı (money-transfer `amount`,
future-pay `approvedLimit`/`apr`, account-opening limitleri) ve şema doğrulaması **reformat sonrası**
koştuğu için hiçbirini reddetmiyor. GraphQL filtre katmanı ise değerleri Int64→decimal okuyor —
filtre ile depolanan değer uyuşmuyor.

## Alınan kararlar

| Karar | Seçim |
|---|---|
| Yaklaşım | **Fix + kill-switch flag**, default = bugünkü davranış (kullanıcı kararı) |
| Kanonik sayı formu | **decimal-koruyan + trailing-zero normalize** — `1.0`→`1`, `2.50`→`2.5` bugünle AYNI; yalnız bozuk değerler düzelir. Ham-metin passthrough reddedildi (aynı değerin iki hash'i → dedupe zayıflar; script görünümü ile disk ayrışır) |
| Legacy pipeline (kill-switch yolu) | **Dokunulmaz** — `LegacyAppendPipeline` acil geri dönüş mekanizmasıdır, oraya düşmek bugünkü davranışa dönmek demektir. `ObjectMerger`/strateji zincirine plumbing yapılmaz, mevcut parite ağı bozulmaz |
| Default çevirme | Bu turda DEĞİL — opt-in; default çevrildiğinde `migrations.json` kaydı + `known-issues.fixedIn` |

## 1. Kanonik sayı formu — `JsonNumberPolicy`

Domain'de `JsonCanonicalizer`'ın yanına: `public enum JsonNumberPolicy { Legacy, PreservePrecision }`.

`PreservePrecision` altında bir sayı token'ı şu sırayla yazılır:

1. **`TryGetInt64` başarılıysa** → `long` olarak birebir (`WriteNumberValue(long)`).
2. **Aksi hâlde `TryGetDecimal` başarılıysa** → decimal'e parse, **trailing-zero'suz invariant format**
   (`"0.#############################"`, `CultureInfo.InvariantCulture`) ile ham değer olarak yazılır.
3. **Hiçbiri değilse** → bugünkü `GetDouble` yolu (decimal aralığı dışı, ör. `1e40`). Davranış ve
   istisna yüzeyi bugünle aynı.

`Legacy` altında bugünkü merdiven (`TryGetInt32 → GetDouble`) aynen korunur.

**Tasarımın çekirdek özelliği:** `1e5`, `-0`, `2.50`, `1.0`, `0.10` gibi sıradan değerler her iki modda
**aynı metni** üretir. `PreservePrecision`, çıktıyı yalnızca bugün hassasiyet kaybettiği yerde
değiştirir — hash churn'ü tam olarak bozuk kümeye sınırlar ve kanonik formun (aynı değer → aynı hash)
anlamını korur.

## 2. Flag ve bağlama

- `WorkflowExecutionOptions.InstanceDataWrite` → `PreserveNumericPrecision` (bool, **default `false`**),
  mevcut `LegacyAppendPipeline`'ın yanında; XML doc'unda flag'i açmanın bir kerelik hash etkisi yazılı.
- `InstanceDataWriteService.PlanAppend` politikayı `JsonCanonicalizer.MergeAndCanonicalize(base, delta, policy)`
  çağrısına geçirir. `MergeAndCanonicalize`'ın mevcut iki-parametreli imzası `Legacy` default'u ile korunur
  (mevcut çağıranlar ve testler değişmez).
- **Etkileşim:** `LegacyAppendPipeline = true` iken `PreserveNumericPrecision` **yok sayılır** (legacy yol
  hiç dokunulmadığı için). Dokümante edilir ve testle pinlenir.
- **Head-null (ilk append)** dalı zaten verbatim; değişmez. Yan kazanç: `PreservePrecision` açıkken 1. satır
  ile 2. satır yüksek hassasiyetli değerlerde artık aynı fikirde — problem tanımındaki (3) asimetrisi kapanır.

## 3. Doğrulama

1. **Mevcut parite ağı korunur:** `JsonCanonicalizerParityTests`'in 12 korpus vakası + 200 rastgele çifti
   `Legacy` modunda koşmaya devam eder; hiçbir mevcut beklenti değişmez ("yeni pipeline == eski pipeline"
   byte-parite garantisi aynen yaşar).
2. **"PreservePrecision yalnız bozuk değerlerde farklıdır" invaryantı** (bu değişikliğin asıl bekçisi):
   sıradan-değer korpusu + rastgele çiftler `PreservePrecision` modunda da koşar ve çıktının `Legacy`
   çıktısıyla **birebir aynı** olması assert edilir. Rastgele üretici iki havuza ayrılır: **sıradan**
   (invaryant: eşitlik) ve **bozuk** (beklenti: düzeltilmiş değer, tablodan). Bugünkü üretici int64-aşan
   tamsayı ve 20-hane ondalık ürettiği için bu ayrım zorunludur.
3. **Bozuk küme için tam beklenti tablosu:** `long.MaxValue`, `9007199254740993`,
   `0.12345678901234567890`, `1234567890123456.78` — her biri için beklenen `NormalizedJson` **ve** SHA1;
   gerçek `InstanceData.ComputeDataHash` ile çapraz kontrol.
4. **Write-service testleri:** (a) flag açıkken satır içeriği hassasiyeti korur; (b) flag açık **+**
   `LegacyAppendPipeline` açık iken çıktı legacy'dir (flag yok sayıldı).
5. **Kültür bağımsızlığı:** format string'in `InvariantCulture` ile sabitlendiği testle pinlenir.

## 4. Dokümantasyon

- **`known-issues.json`** (mevcut şekliyle: id/affectedVersions/severity/component/path/title/workaround/fixedIn):
  default `Legacy` iken fidelity kaybının sürdüğünü, etkilenen değer sınıflarını, workaround olarak
  `PreserveNumericPrecision` flag'ini ve para alanları için "string olarak taşı" önerisini belgeler.
  `fixedIn` default çevrildiğinde doldurulur.
- **`migrations.json`:** bu turda kayıt YOK (default değişmiyor → kullanıcı-görünür davranış değişikliği yok).
  Default çevrildiğinde kayıt yazılır.
- Flag'in XML doc'u: bir kerelik hash etkisi + `LegacyAppendPipeline` ile etkileşimi.

## 5. Kapsam dışı

- `JsonDocumentExtensions.ConvertToDynamic`'in Int64 merdiveni (ScriptContext yolları — script'lerin
  gördüğü CLR değerleri, persist yolu değil).
- Legacy converter (`ExpandoObjectJsonConverter.Read`) — karar gereği.
- Mevcut satırların geri-dönük düzeltilmesi: **migration yok**, satırlar okundukları gibi kalır.
- Şema tarafında sayısal koruma (ör. `x-` ipucu ile para alanlarını string'e zorlamak) — ayrı iş.

## 6. Başarı kriterleri

- [ ] `Legacy` modunda mevcut tüm parite testleri değişmeden yeşil.
- [ ] "PreservePrecision == Legacy (sıradan değerler)" invaryant testi yeşil.
- [ ] Bozuk küme beklenti tablosu yeşil; hash'ler gerçek `ComputeDataHash` ile uyumlu.
- [ ] İki flag'in etkileşimi testli; default davranış değişmemiş (regresyon: isim-diff temiz).
- [ ] `known-issues.json` kaydı yazılı; flag XML doc'u hash etkisini söylüyor.

## 7. Riskler

1. **Flag'i açmanın bir kerelik maliyeti:** bugün bozuk değer taşıyan instance'ların sonraki
   append'inde hash farklılaşır → bir fazladan versiyon satırı + Monitor'da bir kerelik hayalet diff.
   Veri kaybı yok; geri dönüş flag'i kapatmak. Dokümante edilir.
2. **Kanonik formu kazara geniş biçimde değiştirmek** → invaryant testi (§3.2) tam bunun bekçisi.
3. **decimal aralığı dışı değerler** bugünkü double davranışında kalır — dokümante, davranış değişmez.
4. **Kill-switch konumunda fix kaybolur** — bilinçli karar; kill-switch'in tanımı bugünkü davranışa dönmek.
