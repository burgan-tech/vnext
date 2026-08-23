# Katman 2 — Serialization/ScriptContext Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Instance-data okumalarını memoize etmek (B1-B3), append zincirini tek-geçişli kanonik yazıma indirmek (B9, byte-parite + kill-switch), FanOut/parallel branch'i copy-on-write yapmak (B6), audit'ten task tanımını çıkarmak (B8) ve küçük kaçakları kapatmak (B7/B10c/B10d) — davranış sözleşmeleri korunarak (kabul edilmiş iki görünürlük değişimi hariç, ikisi de migrations.json'da).

**Architecture:** `JsonData` parse/normalize memo'ları + `Instance` üzerinde sayaç-anahtarlı kendini-doğrulayan attribute memo'su; `JsonCanonicalizer` merge+kanonik-yazım+SHA1'i tek `Utf8JsonWriter` geçişinde üretir ve eski yol test-oracle + runtime kill-switch olarak kalır; `ScriptContext` branch'i parça-sahipliği (`_ownership`) modeliyle parent'ı paylaşır, yazım funnel'ları ilk dokunuşta yalnız o parçayı `DynamicCloner` (yapısal, JSON'suz klon) ile kopyalar; `InstanceData.CreateSnapshot` immutable `JsonData`'yı paylaşan wrapper-kopyaya döner.

**Spec:** `docs/superpowers/specs/2026-08-23-script-perf-katman2-design.md` · **Branch:** `feature/script-perf-katman0`
**Keşifle netleşen plan-düzeltmeleri (spec'e göre):** (1) Instance shallow-snapshot = *wrapper kopyala, `JsonData`'yı paylaş* — çünkü `IsLatest`/`VersionNo` satır üzerinde mutate ediliyor (`MarkAsNotLatest`), çıplak satır paylaşımı bayrak sızıntısı yapar; `JsonData` ise gerçekten immutable ve pahalı kısım o. (2) B10d "expando passthrough" İPTAL — memo'lu instance-expando'sunu Body'ye alias eder, `ExpandoObjectMergeStrategy` target'ı in-place mutate ettiğinden instance verisini bozardı; yerine `DynamicCloner` (yapısal klon) gelir. (3) Kill-switch mevcut `WorkflowExecutionOptions.InstanceDataWrite` altına: **`WorkflowExecution:InstanceDataWrite:LegacyAppendPipeline`**. (4) Branch dict'leri (TaskResponse/OutputResponse/MetaData/Definitions) container-kopya + **değer paylaşımı** — değer-içi mutasyon sızıntısı, kabul edilmiş görünürlük-değişimi sınıfına migrations kaydıyla eklenir. (5) `ComputeDataHash` **SHA1**/lowercase-hex/40 karakter — canonicalizer aynısını üretmek ZORUNDA (`PlanAppend` dedupe'u yeni-hash-vs-`head.DataHash` karşılaştırır!).

**Değişmezler:** dış API/metrik yüzeyi değişmez; anında-persist satır sözleşmesi korunur; `CloneTransportMetadata` (Headers/RouteValues/QueryParameters) AYNEN kalır (testler pinli); tüm görevlerde hedefli test + kapanışta isim-diff.

---

## Task 1: Okuma memo'ları — `JsonData` + `InstanceData` + `Instance` (B1-B3)

**Files:**
- Modify: `src/BBT.Workflow.Domain/Shared/JsonData.cs`
- Modify: `src/BBT.Workflow.Domain/Instances/InstanceData.cs`
- Modify: `src/BBT.Workflow.Domain/Instances/Instance.cs`
- Modify: `test/BBT.Workflow.Benchmarks/InstanceDataAccessBenchmarks.cs` (ParseJsonElement taze-instance'a — dosyadaki yorumun gereği, AYNI commit)
- Test: `test/BBT.Workflow.Domain.Tests/Shared/JsonDataMemoTests.cs` (yeni), `test/BBT.Workflow.Domain.Tests/Instances/InstanceDataMemoTests.cs` (yeni)

- [ ] **Step 1: Failing testler**

`JsonDataMemoTests.cs`:

```csharp
using System.Text.Json;
using BBT.Workflow;
using Xunit;

namespace BBT.Workflow.Shared;

public class JsonDataMemoTests
{
    [Fact]
    public void JsonElement_IsMemoized_EqualAcrossAccesses()
    {
        var data = new JsonData("""{"a":1,"b":{"c":"x"}}""");
        var first = data.JsonElement;
        var second = data.JsonElement;
        // JsonElement bir struct — referans değil, memo'lanmış aynı belge üzerinden eşdeğerlik pinlenir:
        Assert.True(JsonElement.DeepEquals(first, second));
        Assert.Equal(first.GetRawText(), second.GetRawText());
    }

    [Fact]
    public void JsonElement_Memo_DoesNotAffectValueEquality()
    {
        var x = new JsonData("""{"a":1}""");
        var y = new JsonData("""{"a":1}""");
        _ = x.JsonElement;
        Assert.True(x.ValueEquals(y));
    }
}
```

`InstanceDataMemoTests.cs` (Instance kurulum kalıbını `InstanceTests.SeedData_*`'dan kopyala — instance nasıl yaratılıyor + data nasıl ekleniyorsa aynen):

```csharp
    [Fact]
    public void Attributes_IsMemoized_SameExpandoReference()
    {
        // InstanceData satırı immutable — Attributes artık instance-başına tek kez kurulur.
        var row = /* InstanceTests kalıbıyla tek satırlı instance kur, LatestData'yı al */;
        Assert.Same((object)row.Attributes!, (object)row.Attributes!);
    }

    [Fact]
    public void InstanceData_MemoInvalidates_OnAppend()
    {
        // Instance.Data: yeni satır append edilince memo bayatlar (sayaç-anahtarlı kendini-doğrulama).
        var instance = /* kur */;
        var before = (object)instance.Data!;
        /* ikinci satır ekle (SeedData/AcceptPersistedData — InstanceTests kalıbı) */
        var after = (object)instance.Data!;
        Assert.NotSame(before, after);
    }

    [Fact]
    public void Data_MutationVisibleWithinSameLatest_ButNotPersisted()
    {
        // KABUL EDİLMİŞ davranış değişimi (spec): aynı latest üzerindeki erişimler aynı ağacı paylaşır.
        var instance = /* kur: {"x":1} */;
        ((IDictionary<string, object?>)instance.Data!)["injected"] = 42;
        Assert.Equal(42, ((IDictionary<string, object?>)instance.Data!)["injected"]);
        // Persist edilen içerik değişmedi: satırın JsonData'sı hâlâ orijinal.
        Assert.DoesNotContain("injected", instance.LatestData!.Data.Json);
    }
```

- [ ] **Step 2: FAIL doğrula** — memo yokken `Assert.Same/NotSame` beklentileri kırmızı: `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~JsonDataMemoTests|FullyQualifiedName~InstanceDataMemoTests" 2>&1 | tail -5`.

- [ ] **Step 3: Implementasyon**

1. `JsonData.cs` — `JsonElement` memo'su (emsal `NormalizedJson`):

```csharp
    private JsonElement? _jsonElement;

    public JsonElement JsonElement =>
        _jsonElement ??= JsonSerializer.Deserialize<JsonElement>(Json, JsonSerializerConstants.JsonOptions)!;
```

Ek: **önceden-normalize iç kurucu** (Task 3'ün kullanacağı):

```csharp
    /// <summary>
    /// Canonicalizer çıktısı için: json ZATEN kanonik/normalize — NormalizedJson yeniden hesaplanmaz.
    /// </summary>
    internal static JsonData FromNormalized(string normalizedJson)
    {
        var data = new JsonData(normalizedJson);
        data._normalizedJson = normalizedJson;
        return data;
    }
```

2. `InstanceData.cs` — `Attributes` satır-içi memo:

```csharp
    private dynamic? _attributes;

    public dynamic? Attributes => _attributes ??= Data.JsonElement.ToDynamic();
```

3. `Instance.cs` — sayaç-anahtarlı memo (`Data` getter'ı; `LatestData` de aynı memo'dan yararlanır):

```csharp
    private int _dataMemoCount = -1;
    private InstanceData? _latestRowMemo;

    public dynamic? Data
    {
        get
        {
            lock (_dataListLock)
            {
                return LatestRowLocked()?.Attributes;
            }
        }
    }

    public InstanceData? LatestData
    {
        get { lock (_dataListLock) { return LatestRowLocked(); } }
    }

    /// <summary>
    /// _dataList append-only'dir (Add yalnız ctor/CreateSnapshot/AcceptPersistedData'da; Remove yok —
    /// keşifle doğrulandı), bu yüzden liste SAYISI değişmediyse latest satır da değişmemiştir:
    /// sıralama+Attributes maliyeti erişim başına değil, append başına ödenir.
    /// </summary>
    private InstanceData? LatestRowLocked()
    {
        if (_dataMemoCount != _dataList.Count)
        {
            _latestRowMemo = _dataList.OrderByDescending(x => x, InstanceDataVersionComparer.Instance).FirstOrDefault();
            _dataMemoCount = _dataList.Count;
        }
        return _latestRowMemo;
    }
```

4. Benchmark: `InstanceDataAccessBenchmarks.ParseJsonElement` gövdesi `new BBT.Workflow.JsonData(_json).JsonElement` (taze instance; `_json` string alanı GlobalSetup'ta üretilir) — dosyadaki uyarı yorumu "yapıldı" notuyla güncellenir. `ParseAndBuildExpando` da aynı şekilde taze-instance'a çevrilir (aynı gerekçe).

- [ ] **Step 4: PASS + regresyon** — yeni testler + `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~InstanceTests|FullyQualifiedName~InstanceDataTests|FullyQualifiedName~ScriptContextTests" 2>&1 | tail -3` (FindData/versiyon çözümleme suite'i memo'yla yeşil kalmalı); benchmark projesi Release build.

- [ ] **Step 5: Commit** — `perf(domain): memoize JsonData parse and instance-data attribute reads`

---

## Task 2: `JsonCanonicalizer` — parite-oracle'lı TDD (B9 çekirdeği)

**Files:**
- Create: `src/BBT.Workflow.Domain/Shared/Merging/JsonCanonicalizer.cs`
- Test: `test/BBT.Workflow.Domain.Tests/Shared/Merging/JsonCanonicalizerParityTests.cs` (yeni)

- [ ] **Step 1: Parite-oracle testlerini ÖNCE yaz** — oracle = mevcut yol birebir:

```csharp
using System.Text.Json;
using BBT.Workflow;
using BBT.Workflow.Shared.Merging;
using Xunit;

namespace BBT.Workflow.Shared.Merging;

public class JsonCanonicalizerParityTests
{
    /// <summary>Eski yol, PlanAppend'in bugünkü akışının birebir kopyası (test-oracle).</summary>
    private static (string NormalizedJson, string Hash) Oracle(string baseJson, string deltaJson)
    {
        var merged = new JsonData(baseJson).Merge(new JsonData(deltaJson));
        // InstanceData.ComputeDataHash internal — SHA1(NormalizedJson) lowercase hex; burada aynı
        // formül yerel olarak uygulanır ve ayrıca gerçek ComputeDataHash ile bir kez çapraz doğrulanır
        // (InternalsVisibleTo Domain.Tests'te zaten varsa onu kullan; yoksa formül + tek entegre test).
        var normalized = merged.NormalizedJson;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        return (normalized, hash);
    }

    public static IEnumerable<object[]> Corpus()
    {
        // Kenar durumları: sayı biçimleri ham korunur; ordinal anahtar sıralaması; dizi REPLACE;
        // null-delta anahtar silmez; unicode/escape; derin iç içe; boş obje/dizi; case-sensitive anahtarlar.
        yield return new object[] { """{"a":1}""", """{"b":2}""" };
        yield return new object[] { """{"a":{"x":1,"y":[1,2]}}""", """{"a":{"y":[9],"z":null}}""" };
        yield return new object[] { """{"n1":1.0,"n2":1e5,"n3":-0,"n4":0.10}""", """{"n5":2.50}""" };
        yield return new object[] { """{"tr":"şğüİı","esc":"a\"b\\c"}""", """{"emoji":"🙂"}""" };
        yield return new object[] { """{"Z":1,"a":2,"A":3}""", """{"m":{"Z":1,"a":2}}""" };
        yield return new object[] { """{"deep":{"deep":{"deep":{"v":[{"k":1},{"k":2}]}}}}""", """{"deep":{"deep":{"deep":{"v":[]}}}}""" };
        yield return new object[] { """{}""", """{"first":true}""" };
        yield return new object[] { """{"keep":1}""", """{}""" };
        yield return new object[] { """{"arr":[{"a":1}]}""", """{"arr":[{"b":2},{"c":3}]}""" };
        yield return new object[] { """{"s":"1.0","n":1.0}""", """{"s2":"2","n2":2}""" };
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void CanonicalizeMerge_ByteParity_WithLegacyPipeline(string baseJson, string deltaJson)
    {
        var expected = Oracle(baseJson, deltaJson);
        var actual = JsonCanonicalizer.MergeAndCanonicalize(
            new JsonData(baseJson).JsonElement, new JsonData(deltaJson).JsonElement);

        Assert.Equal(expected.NormalizedJson, actual.NormalizedJson); // BYTE-parite
        Assert.Equal(expected.Hash, actual.DataHash);
    }

    [Fact]
    public void RandomizedParity_SmallGeneratedDocuments()
    {
        // Deterministik tohumlu üretici: 200 rastgele (base, delta) çifti — obje/dizi/sayı/string/null
        // karışımı, max derinlik 4. Her biri için oracle == canonicalizer.
        var rng = new Random(42);
        for (var i = 0; i < 200; i++)
        {
            var baseJson = RandomJson(rng, depth: 0);
            var deltaJson = RandomJson(rng, depth: 0);
            var expected = Oracle(baseJson, deltaJson);
            var actual = JsonCanonicalizer.MergeAndCanonicalize(
                new JsonData(baseJson).JsonElement, new JsonData(deltaJson).JsonElement);
            Assert.Equal(expected.NormalizedJson, actual.NormalizedJson);
        }
    }
    // RandomJson: yerel statik üretici — obje kökü; anahtarlar {a..h, A..D}; değerler:
    // int/decimal-metin ("1.50")/string/bool/null/alt-obje/dizi. Kod bu dosyada tam yazılır.
}
```

- [ ] **Step 2: FAIL doğrula** (JsonCanonicalizer yok — derleme hatası).

- [ ] **Step 3: Implementasyon** — `JsonCanonicalizer.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BBT.Workflow.Shared.Merging;

/// <summary>
/// Merge + kanonikleştirme + veri hash'ini TEK yazım geçişinde üretir (B9). Çıktı, eski
/// Merge→NormalizedJson→ComputeDataHash zinciriyle BYTE-parite hedefler; parite
/// JsonCanonicalizerParityTests'teki oracle korpusuyla pinlidir. Kurallar (eski koddan birebir):
///  - obje+obje: anahtar bazında derin merge; delta anahtarı kazanır; null-delta değeri anahtarı
///    SİLMEZ ama değeri null yapar mı? — ObjectMerger.MergeValues(source==null ⇒ target) yalnız
///    KÖK için; anahtar-değeri null olan delta girdisi eski yolda ExpandoObject'e null olarak girer
///    ve anahtarı null'a çevirir → burada da aynı: null değer yazılır.
///  - dizi+dizi: delta dizisi TÜMÜYLE yerine geçer.
///  - tip çakışması: delta kazanır.
///  - kanonik yazım: obje anahtarları StringComparer.Ordinal sıralı; diziler pozisyonel; sayılar/
///    stringler HAM metniyle (WriteRawValue/GetRawText) — lexical biçim korunur;
///    encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping (eski NormalizeJson options'ı birebir).
///  - hash: SHA1, lowercase hex (InstanceData.ComputeDataHash ile aynı — PlanAppend dedupe'u buna bağlı).
/// Herhangi bir beklenmedik durumda çağıran LEGACY yola düşebilsin diye exception yutulmaz.
/// </summary>
public static class JsonCanonicalizer
{
    public readonly record struct CanonicalResult(string NormalizedJson, string DataHash);

    public static CanonicalResult MergeAndCanonicalize(JsonElement baseDoc, JsonElement delta)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            WriteMerged(writer, baseDoc, delta);
        }
        var bytes = buffer.WrittenSpan;
        var normalized = Encoding.UTF8.GetString(bytes);
        var hash = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        return new CanonicalResult(normalized, hash);
    }

    private static void WriteMerged(Utf8JsonWriter writer, JsonElement target, JsonElement source)
    {
        if (target.ValueKind == JsonValueKind.Object && source.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            // Ordinal-sıralı anahtar birleşimi; delta anahtarı kazanır, ortak anahtar derin-merge.
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var p in target.EnumerateObject()) keys.Add(p.Name);
            foreach (var p in source.EnumerateObject()) keys.Add(p.Name);
            foreach (var key in keys)
            {
                var inTarget = target.TryGetProperty(key, out var tv);
                var inSource = source.TryGetProperty(key, out var sv);
                writer.WritePropertyName(key);
                if (inTarget && inSource) WriteMerged(writer, tv, sv);
                else WriteCanonical(writer, inSource ? sv : tv);
            }
            writer.WriteEndObject();
            return;
        }
        // dizi+dizi ⇒ delta; tip çakışması ⇒ delta (eski davranış)
        WriteCanonical(writer, source);
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var p in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(p.Name);
                    WriteCanonical(writer, p.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer); // sayı/string/bool/null — ham lexical biçim korunur
                break;
        }
    }
}
```

> Parite tuzakları (testler yakalar, implementasyon uyarlar): (a) eski yolda duplicate-anahtarli girdi `ToDictionary` fırlatır → `NormalizeJson` catch'i ORİJİNALİ döndürür; canonicalizer'da duplicate anahtar `TryGetProperty` ilkini alır — korpusta duplicate-key senaryosu YOK çünkü kaynaklar hep serializer üretimi; yine de `keys` kümesi davranışı testte bir duplicate-json ile pinlenir ve FARK çıkarsa o girdi legacy-fallback koşuluna bağlanır. (b) Eski merge'in Expando yolunda anahtar null-değeri: oracle ne diyorsa o (test belirler; yorumdaki varsayım düzeltilir). (c) `element.WriteTo` string'leri kendi escape kurallarıyla yazar — encoder aynı olduğundan `Serialize(JsonElement)` ile aynı çıktı beklenir; korpustaki escape/unicode vakaları pinler.

- [ ] **Step 4: PASS** — tüm korpus + randomized 200 çift yeşil. Parite kırmızısı çıkarsa: ORACLE doğrudur, canonicalizer uyarlanır (asla tersi).

- [ ] **Step 5: Commit** — `feat(domain): single-pass JsonCanonicalizer with byte-parity oracle corpus`

---

## Task 3: Append pipeline'ı bağla + kill-switch (B9)

**Files:**
- Modify: `src/BBT.Workflow.Infrastructure/Data/InstanceDataWriteService.cs` (`PlanAppend` L170-189)
- Modify: `src/BBT.Workflow.Application/BackgroundJobs/Options/WorkflowExecutionOptions.cs` (`InstanceDataWriteOptions`'a flag)
- Modify: `src/BBT.Workflow.Domain/Instances/InstanceData.cs` (hash'i hazır alan iç ctor yolu)
- Test: `test/BBT.Workflow.Infrastructure.Tests/Data/InstanceDataWriteServicePipelineTests.cs` (yeni ya da mevcut write-service test dosyasına ekle — önce mevcut testleri bul: `grep -rn "InstanceDataWriteService" test/ --include="*.cs" | head`)

- [x] **Step 1: Failing testler** — mevcut write-service test kurulumunu kopyalayarak:

```csharp
    [Fact]
    public async Task Append_NewPipeline_ProducesSameRow_AsLegacy()
    {
        // Aynı (head, delta) çifti iki pipeline'dan geçirilir: Version/DataHash/NormalizedJson birebir.
        // (legacy: options.LegacyAppendPipeline=true; new: false — iki servis örneği.)
    }

    [Fact]
    public async Task Append_DuplicateDelta_IsSkipped_OnBothPipelines()
    {
        // Delta merge sonucu içerik değişmiyorsa iki pipeline da IsDuplicate=true üretir
        // (yeni hash'in ESKİ pipeline'ın yazdığı head.DataHash ile eşleşmesi dahil — çapraz test:
        // head legacy ile yazılır, append YENİ pipeline ile denenir → yine duplicate).
    }
```

(İkinci test bu görevin kalbidir: ESKİ yazılmış satırın hash'i ile YENİ pipeline hash'inin uyuşması = SHA1+byte-parite'nin uçtan uca kanıtı.)

- [x] **Step 2: Implementasyon**

1. `InstanceDataWriteOptions`'a (WorkflowExecutionOptions.cs L126-140 sınıfı):

```csharp
    /// <summary>
    /// Kill-switch: true ⇒ append eski çok-geçişli yolu kullanır (Merge→NormalizedJson→ComputeDataHash).
    /// Yeni tek-geçişli JsonCanonicalizer yolunda üretim sorunu görülürse geri dönüş sigortası.
    /// Default false. Kaldırılması ileriki sürümün işi.
    /// </summary>
    public bool LegacyAppendPipeline { get; set; }
```

2. `PlanAppend` (mevcut gövde `PlanAppendLegacy` olarak AYNEN korunur):

```csharp
    private AppendPlan PlanAppend(InstanceDataHeadRow? head, JsonData delta, VersionStrategy? versionStrategy)
    {
        if (_options.InstanceDataWrite.LegacyAppendPipeline)
            return PlanAppendLegacy(head, delta, versionStrategy);

        // head yoksa: delta tek başına kanonikleştirilir (base = boş obje — legacy'de Merge(empty, delta)
        // ne üretiyorsa parite testi onu pinler).
        var baseElement = head is null
            ? new JsonData("{}").JsonElement
            : new JsonData(head.Data).JsonElement;   // Task 1 memo'su: tek parse
        var result = JsonCanonicalizer.MergeAndCanonicalize(baseElement, delta.JsonElement);

        var isDuplicate = head is not null &&
            string.Equals(result.DataHash, head.DataHash, StringComparison.OrdinalIgnoreCase);
        var content = JsonData.FromNormalized(result.NormalizedJson);
        var version = InstanceData.IncrementVersion(head?.Version ?? /* legacy'deki head-yok başlangıcı */, versionStrategy ?? VersionStrategy.None);
        return new AppendPlan(content, version, isDuplicate, result.DataHash);
    }
```

> Mevcut `PlanAppend`'in head-yok dalını ve `AppendPlan` şeklini önce OKU ve birebir uyarla (yukarıdaki iskelet akışı gösterir; alan adları/null akışı gerçek koddan). `AppendPlan`'a hash taşınıyorsa `AppendCoreAsync`'te satır kurulumuna aktarılır.

3. `InstanceData` ctor'u: `DataHash = ComputeDataHash(data)` — `FromNormalized` JsonData'sında `NormalizedJson` memo'lu olduğundan bu çağrı zaten ucuz (tek SHA1). Ek iç parametreye GEREK YOKSA ekleme (YAGNI); profil Task 7'de doğrular, gerekirse o zaman.
4. Schema validation (`ValidateAgainstSchemaAsync` L365): `content.JsonElement` — Task 1 memo'suyla ek parse yok; dokunma.

- [x] **Step 3: PASS + iki-konum testi** — yeni testler + kill-switch=true ile aynı suite yeniden (iki konfigürasyonda da yeşil); mevcut write-service/instance testleri yeşil.

- [x] **Step 4: Commit** — `perf(instances): single-pass append pipeline behind LegacyAppendPipeline kill-switch`

---

## Task 4: COW branch + `DynamicCloner` + wrapper-snapshot (B6)

**Files:**
- Create: `src/BBT.Workflow.Domain/Scripting/DynamicCloner.cs`
- Modify: `src/BBT.Workflow.Domain/Scripting/Models.cs` (branch sahipliği + funnel'lar + Dispose)
- Modify: `src/BBT.Workflow.Domain/Instances/InstanceData.cs` (`CreateSnapshot` → JsonData paylaşımı)
- Test: `test/BBT.Workflow.Domain.Tests/Scripting/ScriptContextCowBranchTests.cs` (yeni), `InstanceDataMemoTests`'e snapshot testi

- [x] **Step 1: `DynamicCloner`** — JSON round-trip'siz yapısal klon (expando/dizi/leaf):

```csharp
namespace BBT.Workflow.Scripting;

/// <summary>
/// ToDynamic çıktısı grafikleri (ExpandoObject / List{object?} / leaf) için yapısal derin klon.
/// CloneDynamic'in JSON round-trip'inin (serialize+parse+expando) yerine geçer: leaf'ler (string,
/// sayı, bool, JsonElement) immutable olduğundan paylaşılır, yalnız konteynerler kopyalanır.
/// </summary>
public static class DynamicCloner
{
    public static object? DeepClone(object? value) => value switch
    {
        System.Dynamic.ExpandoObject expando => CloneExpando(expando),
        System.Collections.Generic.List<object?> list => list.ConvertAll(DeepClone),
        _ => value // leaf: string/sayı/bool/null/JsonElement — immutable, paylaş
    };

    private static System.Dynamic.ExpandoObject CloneExpando(System.Dynamic.ExpandoObject source)
    {
        var clone = new System.Dynamic.ExpandoObject();
        var target = (IDictionary<string, object?>)clone;
        foreach (var (key, value) in (IDictionary<string, object?>)source)
            target[key] = DeepClone(value);
        return clone;
    }
}
```

> ÖNCE `JsonDocumentExtensions.ToDynamic`'in dizi temsili doğrulanır (`List<object?>` mi `object?[]` mi, başka konteyner var mı) ve switch gerçek tiplere göre yazılır. Birim test: `DeepClone(ToDynamic(x))` ile `CloneDynamic(x)`'in JSON-eşdeğer olduğu + klonun mutasyonunun kaynağı etkilemediği (korpus: iç içe obje/dizi/leafler).

- [x] **Step 2: Failing COW testleri** — `ScriptContextCowBranchTests.cs`:

```csharp
    [Fact] public void Branch_NoWrites_SharesBodyByReference() { /* CreateParallelBranch sonrası branch.Body ReferenceEquals parent.Body */ }
    [Fact] public void Branch_WriteToBody_CopiesOnFirstWrite_ParentUntouched() { /* branch.SetStandardResponse → branch.Body != parent.Body (ref); parent içerik değişmedi (JSON karşılaştır) */ }
    [Fact] public void Branch_TaskResponseAdd_IsolatedFromParent() { /* container-kopya: branch dict'e ekleme parent'ta görünmez */ }
    [Fact] public void ConcurrentBranches_WriteIndependently() { /* 8 branch paralel SetStandardResponse (farklı key) → her biri kendi Body kopyasında; parent değişmedi */ }
    [Fact] public void Branch_Dispose_DoesNotClearSharedParts() { /* yazmamış branch dispose edilir → parent.Body/TaskResponse SAĞLAM */ }
    [Fact] public void MergeParallelBranch_Behavior_Unchanged() { /* mevcut Merge senaryosu: branch'te üretilen output parent'a merge olur; çakışmada exception — mevcut testlerden kopyala/uyarla */ }
```

(Mevcut `ScriptContextTests` branch testleri — transport metadata/comparer/dynamic round-trip — AYNEN yeşil kalmalı: Headers/RouteValues/QueryParameters klonu DEĞİŞMİYOR.)

- [x] **Step 3: Models.cs implementasyonu**

1. Sahiplik durumu:

```csharp
    /// <summary>COW branch parçaları: branch'in ilk yazımda kendine kopyaladıkları.</summary>
    [Flags]
    private enum OwnedParts { None = 0, Body = 1 }

    /// <summary>Null ⇒ bu context bir COW branch değil (kök ya da legacy). Branch'te parent'a işaret
    /// eder; yalnız sahiplik/dispose kararları için tutulur, parent'a geri yazım YOKTUR.</summary>
    private ScriptContext? _cowParent;
    private OwnedParts _owned;
```

(Dict'ler container-kopya olduğundan tek COW-korumalı parça Body'dir — enum tek bayrakla başlar;
ileride paylaşılan başka parça çıkarsa genişletilir.)

2. `CreateParallelBranch` yeni gövde: Body/EventPayload paylaş (`Body = Body` referans), `TaskResponse/OutputResponse/MetaData/Definitions` **container-kopya + değer paylaşımı** (`new Dictionary<>(source)` — comparer'ıyla), `Instance = Instance?.CreateSnapshot()` (Task 4 Step 5'in ucuzlamış hali), Headers/RouteValues/QueryParameters `CloneTransportMetadata` AYNEN, Incident/Mutations/Related blokları AYNEN; `_cowParent = this; _owned = OwnedParts.None;`

3. Funnel bekçisi — Body'ye yazan TEK nokta `MergeToBody` (SetBody/SetStandardResponse oradan geçer):

```csharp
    private void EnsureBodyOwned()
    {
        if (_cowParent is null || _owned.HasFlag(OwnedParts.Body)) return;
        Body = DynamicCloner.DeepClone(Body);
        _owned |= OwnedParts.Body;
    }
```

`MergeToBody` başına `EnsureBodyOwned();`. Dict'ler container-kopya olduğundan `TaskResponse[key]=` yazımları zaten izole (dict yazıcıları için EnsureOwned GEREKMEZ — kopya branşta yapıldı). `RefreshInstance` Instance'ı REPLACE eder — bekçi gerekmez.

4. `MergeDictionary`/`CloneDictionary`/`CloneMetadata`/`CloneDynamic`: `CloneDynamic` gövdesi `DynamicCloner.DeepClone`'a yönlendirilir (JSON round-trip ölür — merge yolu da hızlanır). `MergeParallelBranch` L753 `SetBody(cloned)` — `cloned` artık yapısal klon; `SetBody→MergeToBody→ToDynamic(content, ...)` içinden geçince expando'yu YENİDEN serialize eder — bunu önlemek için `MergeToBody` girişine tip kısa devresi eklenir:

```csharp
        // İçerik zaten ToDynamic-shape (Expando/List/leaf) ise serialize+parse'a gerek yok; ama
        // ALIAS'lamamak için yapısal klon üzerinden alınır (kaynağın sonraki mutasyonu Body'yi,
        // Body merge'leri kaynağı ETKİLEMEMELİ — B10d'nin güvenli hali).
        var newValue = content is System.Dynamic.ExpandoObject or System.Collections.Generic.List<object?>
            ? DynamicCloner.DeepClone(content)
            : ToDynamic(content, jsonOptions);
```

(Bu tek değişiklik B10d'yi de kapatır: `WithBody(context.Data)` yolu serialize+parse yerine yapısal klona düşer — 6 çağrı yeri kazanır: RunOnExecute/OnExit/OnEntry/ResourceLock/AutoConditionEvaluator/StartSubflowJobHandler.)

5. `Dispose`: branch'te (`_cowParent != null`) yalnız OWNED parçaları temizle; paylaşılanları null'lama/Clear'lama (parent'ı bozar). FanOut'un "dispose etme" notu geçerliliğini korur ama artık dispose da güvenlidir — FanOut yorumu güncellenir (Related memo notu ayrı gerekçe olarak kalır).
6. DEBUG assert: `#if DEBUG` altında, branch'te `_owned` bayrağı olmayan parçaya doğrudan alan yazımı olmadığını doğrulamak funnel'larla garanti — property setter'ları private olduğundan yazım yüzeyi funnel'lardan ibaret (envanter: L541/557/577/593/707 + dict indexer'ları). Dict indexer'ları container-kopya ile kapalı. Assert yerine bu envanter `ScriptContextCowBranchTests`'te "yazıcı yüzeyi değişirse kır" reflection testi ile pinlenir: public yazıcı metod listesi snapshot'ı.

- [x] **Step 4: `InstanceData.CreateSnapshot` wrapper'ı** — `Data = new JsonData(Data.Json)` yerine **`Data = Data` (referans paylaş)**: `JsonData` immutable ValueObject; satır başına parse+normalize ölür, memo'lar paylaşılır. `IsLatest/VersionNo` wrapper'da kopyalanır (bayrak izolasyonu KORUNUR — `MarkAsNotLatest` yalnız kendi kopyasını etkiler). Test: snapshot sonrası orijinal satırın `MarkAsNotLatest`'i snapshot'ı etkilemez + `ReferenceEquals(row.Data, snapshot.Data)`.

- [x] **Step 5: PASS + geniş regresyon** — yeni testler + `ScriptContextTests` + `ScriptContextRelatedTests` + merge strategy testleri + `dotnet test test/BBT.Workflow.Domain.Tests 2>&1 | tail -3` (isim karşılaştırma) + `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~FanOut|FullyQualifiedName~TaskCoordinator|FullyQualifiedName~ParallelBranch" 2>&1 | tail -3`.

- [x] **Step 6: Commit** — `perf(scripting): copy-on-write parallel branches, structural cloning, shared-JsonData snapshots`

---

## Task 5: Küçükler — B7 fast-path + B10c Lazy (B10d Task 4'te kapandı)

**Files:**
- Modify: `src/BBT.Workflow.Domain/Scripting/Models.cs` (`JsonEquivalent`)
- Modify: `src/BBT.Workflow.Domain/Scripting/Factory/Services/ScriptContextBuilder.cs` (L254-262)
- Test: mevcut dosyalara ekleme

- [x] **Step 1: B7** — `JsonEquivalent` fast-path:

```csharp
    private static bool JsonEquivalent(object? left, object? right)
    {
        if (left is JsonElement le && right is JsonElement re)
            return JsonElement.DeepEquals(le, re); // serialize'sız
        return JsonSerializer.Serialize(left, JsonScriptBodyOptions) ==
               JsonSerializer.Serialize(right, JsonScriptBodyOptions);
    }
```

(Expando girdilerinde eski yol kalır — DeepEquals'a çevirmek İKİ SerializeToElement ister, kazanç yok; yorumla belirt.)

- [x] **Step 2: B10c** — `ScriptContextBuilder`: `SetCurrentTransition(BuildScriptTransitionRequest())` eager çağrısı yerine `ScriptTransitionRequest`'in alanlarını lazy taşıyan yapı. `ScriptTransitionRequest`'in tipini/tüketicisini ÖNCE oku (`grep -rn "ScriptTransitionRequest" src/ --include="*.cs"`): script'ler `context.CurrentTransition.Data/Header` okur — record ise `Lazy<dynamic>` alanlı eşdeğer sınıfa çevirmek script sözleşmesini (üye adları/dynamic erişim) DEĞİŞTİRMEMELİ. En az riskli şekil: `ScriptTransitionRequest` sınıfına lazy-ctor eklemek (`Func<dynamic> dataFactory, Func<dynamic> headerFactory` alan; `Data`/`Header` property'leri ilk erişimde üretir). Builder factory'leri kapatır; `_instanceTransition` null ise mevcut davranış aynen.
- [x] **Step 3: Testler** — B7: JsonElement çiftiyle eşdeğerlik/farklılık (DeepEquals yolunun kullanıldığı davranışsal olarak aynı kalır — mevcut Merge çakışma testleri yeşil); B10c: CurrentTransition'a erişilMEyen context kurulumunda `Body.JsonElement.ToDynamic` çağrılmadığını mock/sayaçla pinlemek zor — pragmatik test: erişimde doğru Data/Header dönmesi + lazy'nin yalnız ilk erişimde koşması (factory çağrı sayacı).
- [x] **Step 4: Commit** — `perf(scripting): DeepEquals fast-path and lazy current-transition materialization`

---

## Task 6: B8 — audit'te task referansı + migrations kayıtları

**Files:**
- Modify: `src/BBT.Workflow.Application/Tasks/TaskExecutionEngine.cs` (L619-627)
- Modify: `vnext-meta/migrations.json`
- Test: TaskExecutionEngine'in mevcut test dosyasına ekleme (bul: `grep -rn "TaskExecutionEngine" test/ --include="*.cs" | head -3`)

- [ ] **Step 1:** L619-623 değişimi:

```csharp
        // B8: task TANIMI (mapping ScriptCode dahil) transition record'a kopyalanmaz — tanım zaten
        // component store'dadır; referans yeterli. Monitor tarafı Request'i JsonElement passthrough
        // gösterir (alan-parse etmez — keşifle doğrulandı), "Task" anahtarı korunur.
        var requestPayload = new
        {
            Task = new { task.Key, task.Version, task.Domain, task.Flow, Type = task.GetTaskType().ToString() },
            executorContext.InputResponse
        };
```

(`task` değişkeninin gerçek tipindeki üye adlarını dosyadan doğrula — `Flow` yoksa uyarlaması raporlanır.)

- [ ] **Step 2:** Test: SetRequest'e verilen JSON'da `task.key/version` var, `mapping`/`code` YOK; `InputResponse` aynen. Migrations entry (`vnext-meta/migrations.json` — mevcut şekil):

```json
    {
      "id": "task-audit-request-carries-reference-not-definition",
      "type": "behavior",
      "component": "runtime",
      "path": "instanceTask.request.task",
      "since": "0.0.81",
      "severity": "info",
      "title": "Task audit request now stores a task reference instead of the full definition",
      "description": "InstanceTask.Request previously embedded the entire task definition (including mapping script code) on every execution. It now stores { key, version, domain, flow, type }; the definition lives in the component store. InputResponse is unchanged. Monitoring passes Request through as raw JSON, so no monitor field parsing is affected.",
      "action": "If any external consumer parsed request.task.* definition fields (e.g. config, mapping code), resolve the definition from the component store by the embedded reference instead."
    }
```

İkinci entry — kabul edilmiş görünürlük değişimleri (Task 1 + Task 4):

```json
    {
      "id": "script-context-shared-tree-visibility",
      "type": "behavior",
      "component": "runtime",
      "path": "scriptContext.instance.data",
      "since": "0.0.81",
      "severity": "warning",
      "title": "Instance data reads share one materialized tree per data version",
      "description": "context.Instance.Data previously materialized a fresh dynamic tree on EVERY access, so script-side mutations of it were silently lost. Reads are now memoized per data version: a mutation becomes visible to subsequent reads within the same transition (it is still NEVER persisted — persistence remains delta-only via ScriptResponse.Data). Similarly, parallel-branch task response VALUES are now shared by reference between parent and branch (containers stay isolated); in-place mutation of a pre-existing response value inside a branch is visible to the parent after join.",
      "action": "Scripts must not mutate context.Instance.Data or pre-existing task response values in place and rely on the old silent-discard; return deltas via ScriptResponse.Data as documented."
    }
```

- [ ] **Step 3:** `vnext-meta-validator` skill'i kontrolör tarafından koşulur (görev raporunda hatırlatılır). Commit — `perf(tasks): store task reference instead of definition in audit request; document behavior notes`

---

## Task 7: Mikro yeniden ölçüm

- [x] `dotnet run -c Release --project test/BBT.Workflow.Benchmarks -- --filter "*AppendPath*|*ParallelBranch*|*InstanceDataAccess*|*AuditSerialize*" --exporters markdown` — tam koşum; `baselines/2026-08-23-master.md`'ye "Katman 2 sonrası" bölümü: önce/sonra tabloları + yorum. NOT: `ParallelBranchBenchmarks.CreateBranch` artık COW-branch üretir — ölçüm "branch yaratma" maliyetidir (yazımsız ≈ konteyner kopyaları); açıklaması yazılır. `AppendPath.Merge` legacy `JsonData.Merge`'i ölçmeye devam eder (hâlâ mevcut ve kill-switch yolu) — yeni yol için `CanonicalizeMerge` benchmark'ı EKLENİR (aynı parametrelerle).
  (`|` birleştirme BenchmarkDotNet'te 0 sonuç döndürdüğü keşfedildi — her desen ayrı `--filter` argümanı olarak verildi.)
- [x] Commit — `docs(benchmarks): Katman 2 micro before/after`

---

## Task 8: Kapanış — integration + makro + isim-diff + dokümantasyon

- [ ] **İsim-diff:** Domain+Application+Infrastructure branch'te koş, scratchpad'deki master baseline'larla isim-diff (Katman 0/1 yöntemi; App için Task-içi stash-diff yeterli olduysa o rapor).
- [ ] **Apps yeniden başlat** (eski process'ler Katman 1 binary'sinde): kontrolör durdurur, rebuild, 4 app `--launch-profile http`, health 4/4.
- [ ] **Integration (geniş set — bu katmanın şartı):** worktree'de `VNEXT_BASE_URL=http://localhost:4201 dotnet test tests/Core.IntegrationTests --filter "FullyQualifiedName~ScriptPerfLab|FullyQualifiedName~ChainBusy|FullyQualifiedName~FanOut" | tail -3` — hepsi yeşil (chain-busy publish gerekiyorsa `--publish`'li python akışı ya da suite'in kendi publish'i).
- [ ] **Kill-switch canlı testi:** orchestration+execution appsettings'e geçici `WorkflowExecution:InstanceDataWrite:LegacyAppendPipeline=true` env ile app'leri kaldırıp script-perf-lab integration testinin yeşil kaldığını doğrula (config yolu çalışıyor kanıtı), sonra normale dön. (Env değişkeni: `WorkflowExecution__InstanceDataWrite__LegacyAppendPipeline=true` — launch profile'ı bozmadan process env'iyle.)
- [ ] **Makro:** taze nonce publish → soğuk (adil: önce integration ısıtması) → sıcak 4KB+16KB (3×20) + dotnet-counters. Beklenen imza (rapor): alloc/instance ~65 MB'dan belirgin düşüş (B6+B9+B10d), 16KB p50/p95 düşüşü, LOH düşüşü; `miss=+0`, execution sayıları değişmez.
- [ ] **Dokümantasyon:** vnext-example README "Katman 2 sonrası" tablosu + TEST-SCENARIOS durum; vnext plan durum tablosu + baselines çapraz not; memory (kontrolör).
- [ ] Commit'ler iki repoda; spec §7 kriterleri işaretlenir.

---

## Riskler / kontrol noktaları

- **Parite** = Task 2 oracle korpusu + Task 3 çapraz-pipeline duplicate testi + kill-switch; `DataHash` tek canlı tüketicisi `PlanAppend` dedupe'u (keşifle netleşti) — çapraz test tam bunu pinler.
- **COW izolasyonu** = Task 4 test matrisi + yazıcı-yüzeyi snapshot testi; Dispose owned-only.
- **Alias tuzağı** (memo'lu instance-expando ↔ Body) = `MergeToBody`'nin yapısal-klon kısa devresi (asla referans almaz); Task 1'in mutasyon-görünürlük testi + Task 4 klon testleri.
- **`ObjectMerger.MergeValues`'un instance-data hedefli başka çağıranı** (memo'yu bozacak in-place merge): plan görevi — `grep -rn "MergeValues" src/ workers/ --include="*.cs"` çıktısındaki her çağıranın hedefi incelenir; `Instance.Data`/`Attributes` hedefli çağıran bulunursa savunmalı klona alınır ve raporlanır (bilinen tek hedef `ScriptContext.Body`).
- Katman genişliği → görev başına iki aşamalı inceleme; Task 8 geniş integration seti.
