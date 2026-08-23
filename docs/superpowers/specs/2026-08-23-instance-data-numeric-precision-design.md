# Instance-Data Sayısal Hassasiyet Fix'i (Design Spec)

**Tarih:** 2026-08-23 · **Durum:** Onaylandı (brainstorming oturumu) · **Branch:** `feature/script-perf-katman0`
**Kaynak:** Katman 2 parite çalışmasında keşfedilen pre-existing kayıp (chip `task_e9b9ab37`) · İlgili spec: `2026-08-23-script-perf-katman2-design.md` §2

## Problem

Instance-data append'i her sayıyı `TryGetInt32 → GetDouble` merdiveninden geçiriyor
(`ExpandoObjectJsonConverter.Read`, legacy; `JsonCanonicalizer`, yeni pipeline — parite için bilinçli
replika). Sonuçlar:

| Girdi | Bugün persist edilen | Kayıp |
|---|---|---|
| `9223372036854775807` (`long.MaxValue`) | `9.223372036854776E+18` | **değer int64 aralığının dışına çıkıyor — bozulma** (ölçülen gerçek çıktı; `Utf8JsonWriter` double'ı üstel yazıyor) |
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

**Tasarımın çekirdek özelliği:** `1e5`, `-0`, `2.50`, `1.0`, `0.10`, `1234.56` gibi sıradan değerler her
iki modda **aynı metni** üretir; kanonik formun (aynı değer → aynı hash) anlamı korunur.

> **Tasarım düzeltmesi (2026-08-23, plan aşamasında koddan bulundu — kullanıcı kararı: "üstel gösterim
> yok"):** yukarıdaki eşitlik, **bilimsel gösterimle biçimlenen değerler için geçerli DEĞİLDİR.**
> Legacy yol double formatlaması kullandığı için `0.00001` bugün `"1E-05"`, `1000000000000000000` ise
> `"1E+18"` olarak yazılıyor; `PreservePrecision` bunları düz gösterimle (`"0.00001"`,
> `"1000000000000000000"`) yazar. Kayıp yoktur, ama metin — dolayısıyla hash — değişir.
> **Kabul edilen sonuç:** kanonik form artık "üstel gösterim yok, düz ondalık" olarak tanımlanır; bir
> değerin tek temsili olur. Bedeli, flag açıldığındaki bir kerelik churn'ün bozuk kümenin yanı sıra
> E-gösterimli değerleri de kapsaması; tipik para/oran verisinde (`1234.56`, `0.075`) bu küme boştur,
> E-gösterimi ancak çok küçük (`<0.0001`) ya da çok büyük yuvarlak sayılarda devreye girer.
> Reddedilen alternatif: "yalnız kayıp varsa müdahale et" (legacy'nin biçimlendirme tuhaflığını kanonik
> tanıma kalıcı olarak dondurur + sayı başına çift hesaplama).
>
> **Aynı kararın ikinci sonucu (implementasyonda ölçüldü): negatif sıfır.** Ondalık kısmı olan bir
> negatif sıfır (`-0.0`, `-0.00`) bugün `-0` olarak yazılıyor; `PreservePrecision` altında `decimal`
> negatif sıfır taşımadığı için `0` yazılır. Bu da kayıp değil, **tek-temsil** kuralının doğal sonucu
> (`-0.0 == 0`) ve kanonik form tanımıyla tutarlı. Churn kümesine dahildir; `known-issues` kaydı
> "etkilenen değerler" listesinde anılır. Tamsayı `-0` her iki modda da `0`'dır (davranış değişmez).

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
2. **"PreservePrecision sıradan değerlerde Legacy ile aynıdır" invaryantı** (bu değişikliğin asıl bekçisi):
   sıradan-değer korpusu + rastgele çiftler `PreservePrecision` modunda da koşar ve çıktının `Legacy`
   çıktısıyla **birebir aynı** olması assert edilir. Üretici ve korpus **üç** havuza ayrılır:
   **sıradan** (invaryant: eşitlik — düz ondalık, `1.0`, `2.50`, `1e2` gibi tamsayıya çözülen üsteller),
   **bozuk** (beklenti tablosu: int64 taşması, 2^53+1, >15 hane ondalık) ve **E-gösterimli**
   (beklenti tablosu: `0.00001` → `"0.00001"`, `1e18` → düz gösterim; tasarım düzeltmesi §1). Mevcut
   `Corpus()` (12 vaka) `Legacy` modunda TAMAMEN korunur; sıradan alt kümesi ayrı bir üye olarak
   invaryant testine beslenir. Bugünkü rastgele üretici zaten int64-aşan tamsayı, 20-hane ondalık ve
   `e-`/`E+` biçimleri ürettiği için bu ayrım zorunludur.
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

1. **Flag'i açmanın bir kerelik maliyeti:** bugün bozuk değer **veya E-gösterimli değer** taşıyan
   instance'ların sonraki append'inde hash farklılaşır → bir fazladan versiyon satırı + Monitor'da bir
   kerelik hayalet diff. Veri kaybı yok; geri dönüş flag'i kapatmak. `known-issues.json` kaydı ve
   option'ın XML doc'u iki kümeyi de açıkça sayar.
2. **Kanonik formu kazara geniş biçimde değiştirmek** → invaryant testi (§3.2) tam bunun bekçisi.
3. **decimal aralığı dışı değerler** bugünkü double davranışında kalır — dokümante, davranış değişmez.
4. **Kill-switch konumunda fix kaybolur** — bilinçli karar; kill-switch'in tanımı bugünkü davranışa dönmek.
