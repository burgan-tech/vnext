# Instance-Data Sayısal Hassasiyet Fix'i Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Instance-data append'inin sayıları bozmasını (int64 taşması, >15 hane ondalık kaybı) opt-in bir flag arkasında düzeltmek; default davranışı ve mevcut byte-parite ağını hiç bozmadan.

**Architecture:** `JsonCanonicalizer`'a `JsonNumberPolicy` parametresi eklenir (`Legacy` = bugünkü `TryGetInt32→GetDouble`, `PreservePrecision` = `TryGetInt64 → TryGetDecimal(düz, trailing-zero'suz) → GetDouble`). Politika `WorkflowExecutionOptions.InstanceDataWrite.PreserveNumericPrecision`'dan (default `false`) `PlanAppend` üzerinden geçirilir. Legacy pipeline ve `ExpandoObjectJsonConverter` **hiç değişmez**.

**Spec:** `docs/superpowers/specs/2026-08-23-instance-data-numeric-precision-design.md` (§1'deki "tasarım düzeltmesi" dahil) · **Branch:** `feature/script-perf-katman0`

**Değişmezler:**
- `Legacy` modu bugünkü çıktının **birebir** aynısı; mevcut `JsonCanonicalizerParityTests`'in 12 vakası + 200 rastgele çifti ve `InstanceDataWriteService` testleri **değişmeden** yeşil kalmalı.
- Default `false` → dış davranış değişmez; bu turda `migrations.json` kaydı YOK.
- `LegacyAppendPipeline=true` iken hassasiyet flag'i yok sayılır (legacy yol dokunulmadığı için) — testle pinlenir.
- Kanonik form: **üstel gösterim yok** (spec §1 düzeltmesi). `InvariantCulture` zorunlu.

---

## Task 1: `JsonNumberPolicy` + canonicalizer'da sayı yazımı

**Files:**
- Modify: `src/BBT.Workflow.Domain/Shared/Merging/JsonCanonicalizer.cs`
- Test: `test/BBT.Workflow.Domain.Tests/Shared/Merging/JsonCanonicalizerNumberPolicyTests.cs` (yeni)

- [ ] **Step 1: Failing testleri yaz** — yeni dosya. Oracle'ı MEVCUT parite testinden kopyala (`JsonCanonicalizerParityTests.Oracle`, L24-30 — aynı gövde: `new JsonData(baseJson).Merge(new JsonData(deltaJson))` → `NormalizedJson` → SHA1-lowercase).

```csharp
using System;
using System.Security.Cryptography;
using System.Text;
using BBT.Workflow.Shared.Merging;
using Xunit;

namespace BBT.Workflow.Shared.Merging;

/// <summary>
/// PreservePrecision modunun sözleşmesi: (1) hassasiyet kaybı olan değerler DÜZELİR,
/// (2) E-gösterimli değerler düz gösterime geçer (bilinçli — spec §1 düzeltmesi),
/// (3) SIRADAN değerlerde çıktı Legacy ile BİREBİR aynıdır (kanonik formu kazara
/// genişletmediğimizin bekçisi).
/// </summary>
public class JsonCanonicalizerNumberPolicyTests
{
    private static (string NormalizedJson, string Hash) Oracle(string baseJson, string deltaJson)
    {
        var merged = new BBT.Workflow.JsonData(baseJson).Merge(new BBT.Workflow.JsonData(deltaJson));
        var normalized = merged.NormalizedJson;
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        return (normalized, hash);
    }

    private static string Canonical(string baseJson, string deltaJson, JsonNumberPolicy policy) =>
        JsonCanonicalizer.MergeAndCanonicalize(
            new BBT.Workflow.JsonData(baseJson).JsonElement,
            new BBT.Workflow.JsonData(deltaJson).JsonElement,
            policy).NormalizedJson;

    /// <summary>Kaybın olduğu ve düzelmesi BEKLENEN değerler.</summary>
    public static IEnumerable<object[]> LossyCorpus()
    {
        // int64 tavanı: bugün 9223372036854775808 (int64 DIŞI) yazılıyor.
        yield return new object[] { """{"v":9223372036854775807}""", """{"x":1}""", "9223372036854775807" };
        // 2^53+1: bugün ...992'ye yuvarlanıyor.
        yield return new object[] { """{"v":9007199254740993}""", """{"x":1}""", "9007199254740993" };
        // 20 haneli ondalık: bugün 17 haneye kırpılıyor.
        yield return new object[] { """{"v":0.12345678901234567890}""", """{"x":1}""", "0.1234567890123456789" };
        // Kuruş hassasiyeti: bugün ...6.8 oluyor.
        yield return new object[] { """{"v":1234567890123456.78}""", """{"x":1}""", "1234567890123456.78" };
    }

    [Theory]
    [MemberData(nameof(LossyCorpus))]
    public void PreservePrecision_FixesLossyValues(string baseJson, string deltaJson, string expectedNumberText)
    {
        var legacy = Canonical(baseJson, deltaJson, JsonNumberPolicy.Legacy);
        var preserved = Canonical(baseJson, deltaJson, JsonNumberPolicy.PreservePrecision);

        Assert.Contains($"\"v\":{expectedNumberText}", preserved);
        Assert.DoesNotContain($"\"v\":{expectedNumberText}", legacy); // bugün gerçekten bozuk
        Assert.Equal(legacy, Canonical(baseJson, deltaJson, JsonNumberPolicy.Legacy)); // determinizm
    }

    /// <summary>E-gösterimli değerler: kayıp YOK ama metin düzleşir (bilinçli).</summary>
    [Theory]
    [InlineData("""{"v":0.00001}""", "0.00001")]
    [InlineData("""{"v":1e18}""", "1000000000000000000")]
    [InlineData("""{"v":-0.00002}""", "-0.00002")]
    public void PreservePrecision_FlattensExponentNotation(string baseJson, string expectedNumberText)
    {
        var preserved = Canonical(baseJson, """{"x":1}""", JsonNumberPolicy.PreservePrecision);
        Assert.Contains($"\"v\":{expectedNumberText}", preserved);
    }

    /// <summary>SIRADAN değerler: iki mod birebir aynı (asıl invaryant).</summary>
    public static IEnumerable<object[]> OrdinaryCorpus()
    {
        yield return new object[] { """{"a":1}""", """{"b":2}""" };
        yield return new object[] { """{"n1":1.0,"n2":1e5,"n3":-0,"n4":0.10}""", """{"n5":2.50}""" };
        yield return new object[] { """{"money":1234.56,"rate":0.075}""", """{"qty":3}""" };
        yield return new object[] { """{"big":3000000000}""", """{"x":1}""" };       // int64'e sığar, double da tam
        yield return new object[] { """{"arr":[{"a":1.50}]}""", """{"arr":[{"b":2}]}""" };
        yield return new object[] { """{"deep":{"deep":{"v":[1,2.5,3]}}}""", """{"x":null}""" };
    }

    [Theory]
    [MemberData(nameof(OrdinaryCorpus))]
    public void PreservePrecision_MatchesLegacy_ForOrdinaryValues(string baseJson, string deltaJson)
    {
        Assert.Equal(
            Canonical(baseJson, deltaJson, JsonNumberPolicy.Legacy),
            Canonical(baseJson, deltaJson, JsonNumberPolicy.PreservePrecision));
    }

    [Theory]
    [MemberData(nameof(OrdinaryCorpus))]
    public void LegacyPolicy_StillMatchesTheOracle(string baseJson, string deltaJson)
    {
        var expected = Oracle(baseJson, deltaJson);
        var actual = JsonCanonicalizer.MergeAndCanonicalize(
            new BBT.Workflow.JsonData(baseJson).JsonElement,
            new BBT.Workflow.JsonData(deltaJson).JsonElement,
            JsonNumberPolicy.Legacy);
        Assert.Equal(expected.NormalizedJson, actual.NormalizedJson);
        Assert.Equal(expected.Hash, actual.DataHash);
    }

    [Fact]
    public void PreservePrecision_IsCultureInvariant()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // Virgül-ondalıklı kültür: format string InvariantCulture ile sabitlenmemişse kırar.
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            var preserved = Canonical("""{"v":1234567890123456.78}""", """{"x":1}""", JsonNumberPolicy.PreservePrecision);
            Assert.Contains("\"v\":1234567890123456.78", preserved);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }
}
```

> `LossyCorpus`'un beklenen metinleri (özellikle 20-hane ondalığın decimal'e sığan kısmı:
> `0.1234567890123456789` — decimal 28-29 anlamlı hane taşır) implementasyon sonrası **gerçek çıktıyla
> doğrulanır**; sapma varsa BEKLENTİ düzeltilir (kayıpsızlık iddiası korunmak kaydıyla: değer decimal'e
> sığıyorsa birebir, sığmıyorsa double'a düşer — o vaka lossy korpusundan çıkarılır ve §Riskler'e not düşülür).

- [ ] **Step 2: FAIL doğrula** — `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~JsonCanonicalizerNumberPolicyTests" 2>&1 | tail -5` → derleme hatası (`JsonNumberPolicy` ve 3-parametreli `MergeAndCanonicalize` yok).

- [ ] **Step 3: Implementasyon** — `JsonCanonicalizer.cs`:

1. Dosyanın başına (namespace içinde, sınıfın üstüne) politika enum'u:

```csharp
/// <summary>
/// Instance-data kanonikleştirmesinde sayı yazım politikası.
/// </summary>
public enum JsonNumberPolicy
{
    /// <summary>
    /// Tarihsel davranış: <c>TryGetInt32</c> başarılıysa int, aksi hâlde <c>GetDouble</c>. int64
    /// aralığındaki tamsayılar ve 15+ haneli ondalıklar hassasiyet kaybeder; bu politika var olan
    /// satırlarla byte-parite için korunur ve varsayılandır.
    /// </summary>
    Legacy = 0,

    /// <summary>
    /// Kayıpsız yazım: int64'e sığan tamsayılar birebir, aksi hâlde decimal'e sığan değerler düz
    /// (üstel gösterimsiz, trailing-zero'suz) ondalık olarak yazılır; hiçbirine sığmayan değerler
    /// <see cref="Legacy"/> gibi double'a düşer. Kanonik form üstel gösterim İÇERMEZ — bu yüzden
    /// çıktı, hassasiyet kaybı olan değerlerin YANI SIRA bugün E-gösterimiyle yazılan değerlerde de
    /// <see cref="Legacy"/>'den farklıdır (bilinçli; bkz. spec §1).
    /// </summary>
    PreservePrecision = 1
}
```

2. `MergeAndCanonicalize` imzası (mevcut çağıranlar bozulmasın diye default'lu):

```csharp
    public static CanonicalResult MergeAndCanonicalize(
        JsonElement baseDoc,
        JsonElement delta,
        JsonNumberPolicy numberPolicy = JsonNumberPolicy.Legacy)
```

Politika, yazım zincirinden geçirilir: `WriteObjectLevel`, `TransformAndWrite`, `WriteSortedRaw` ve varsa diğer yazım yardımcıları `JsonNumberPolicy policy` parametresi alır (private metotlar — imza değişikliği serbest; her çağrı yerini derleyici gösterir).

3. Sayı yazımı (`TransformAndWrite`'ın `JsonValueKind.Number` dalı):

```csharp
            case JsonValueKind.Number:
                WriteNumber(writer, element, policy);
                break;
```

ve yeni yardımcı:

```csharp
    /// <summary>
    /// Sayı yazımı. <see cref="JsonNumberPolicy.Legacy"/> tarihsel merdiveni birebir korur.
    /// <see cref="JsonNumberPolicy.PreservePrecision"/> önce int64, sonra decimal dener; decimal'i
    /// üstel gösterimsiz ve trailing-zero'suz sabit bir formatla yazar (kanonik form), böylece
    /// 1.0 → 1 ve 2.50 → 2.5 tarihsel çıktıyla aynı kalır. Hiçbirine sığmayan değer (decimal
    /// aralığı dışı, ör. 1e40) tarihsel double yoluna düşer.
    /// </summary>
    private static void WriteNumber(Utf8JsonWriter writer, JsonElement element, JsonNumberPolicy policy)
    {
        if (policy == JsonNumberPolicy.Legacy)
        {
            if (element.TryGetInt32(out var legacyInt)) writer.WriteNumberValue(legacyInt);
            else writer.WriteNumberValue(element.GetDouble());
            return;
        }

        if (element.TryGetInt64(out var exactInt))
        {
            writer.WriteNumberValue(exactInt);
            return;
        }

        if (element.TryGetDecimal(out var exactDecimal))
        {
            // Trailing zero'lar düşer (2.50 → 2.5), üstel gösterim ASLA kullanılmaz (0.00001 →
            // 0.00001), kültür sabittir. WriteRawValue: metni sayı token'ı olarak yazar.
            writer.WriteRawValue(
                exactDecimal.ToString("0.############################", CultureInfo.InvariantCulture),
                skipInputValidation: false);
            return;
        }

        writer.WriteNumberValue(element.GetDouble());
    }
```

`using System.Globalization;` eklenir. Sınıf XML doc'undaki "numbers get reformatted via TryGetInt32-else-GetDouble" ifadesi politikaya göre güncellenir (`TransformAndWrite`'ın doc'u dahil).

> DİKKAT — format string uzunluğu: decimal 28-29 anlamlı hane taşır; `0.############################`
> (28 `#`) ondalık kısmı 28 haneye kadar yazar. `LossyCorpus`'un 20-haneli vakası bunu doğrular; gerçek
> çıktı beklentiden farklıysa `#` sayısı ayarlanır ve testin beklentisi gerçek değere çekilir.

- [ ] **Step 4: PASS + mevcut parite ağı** — yeni testler yeşil; `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~JsonCanonicalizer" 2>&1 | tail -3` (mevcut 33 parite testi DEĞİŞMEDEN yeşil kalmalı — default `Legacy` sayesinde); `dotnet build vnext.sln` 0 error.

- [ ] **Step 5: Commit** — `feat(domain): opt-in lossless numeric canonicalization in JsonCanonicalizer`

---

## Task 2: Flag + write-service bağlama

**Files:**
- Modify: `src/BBT.Workflow.Application/BackgroundJobs/Options/WorkflowExecutionOptions.cs` (`InstanceDataWriteOptions`, L126+)
- Modify: `src/BBT.Workflow.Infrastructure/Data/InstanceDataWriteService.cs` (`PlanAppend` + `AppendCoreAsync` çağrı yeri)
- Test: `test/BBT.Workflow.Infrastructure.Tests/Data/InstanceDataWriteServicePipelineTests.cs` (mevcut dosyaya ekle)

- [ ] **Step 1: Failing testler** — mevcut dosyanın iki-options kalıbını (legacy/new pipeline için zaten var) izleyerek:

```csharp
    [Fact]
    public void PlanAppend_WithPrecisionFlag_PreservesLosslessNumbers()
    {
        var head = /* mevcut testlerdeki head kurulumu: {"v":1} içeren InstanceDataHeadRow */;
        var delta = new JsonData("""{"amount":1234567890123456.78}""");

        var withFlag = InstanceDataWriteService.PlanAppend(head, delta, VersionStrategy.None,
            legacyPipeline: false, preserveNumericPrecision: true);
        var withoutFlag = InstanceDataWriteService.PlanAppend(head, delta, VersionStrategy.None,
            legacyPipeline: false, preserveNumericPrecision: false);

        Assert.Contains("1234567890123456.78", withFlag.Content.Json);
        Assert.DoesNotContain("1234567890123456.78", withoutFlag.Content.Json); // bugünkü kayıp
    }

    [Fact]
    public void PlanAppend_LegacyPipeline_IgnoresPrecisionFlag()
    {
        var head = /* aynı kurulum */;
        var delta = new JsonData("""{"amount":1234567890123456.78}""");

        var legacyWithFlag = InstanceDataWriteService.PlanAppend(head, delta, VersionStrategy.None,
            legacyPipeline: true, preserveNumericPrecision: true);
        var legacyWithoutFlag = InstanceDataWriteService.PlanAppend(head, delta, VersionStrategy.None,
            legacyPipeline: true, preserveNumericPrecision: false);

        Assert.Equal(legacyWithoutFlag.Content.NormalizedJson, legacyWithFlag.Content.NormalizedJson);
    }
```

(`PlanAppend`'in gerçek imzası/parametre adlarını dosyadan doğrula — Katman 2'de `internal static` + `legacyPipeline: bool` olarak bırakıldı; yeni parametre **sonuna** default'suz eklenir ve mevcut 4 çağrı yeri + pipeline testleri güncellenir. Alternatif olarak default `false` verilirse mevcut çağrı yerleri değişmez — YAGNI: default ver, çağrı yerlerini dokunmadan bırak.)

- [ ] **Step 2: FAIL doğrula** — derleme hatası (yeni parametre yok).

- [ ] **Step 3: Implementasyon**

1. `InstanceDataWriteOptions`'a (`LegacyAppendPipeline`'ın hemen altına):

```csharp
    /// <summary>
    /// Opt-in: true ⇒ append, sayıları kayıpsız kanonikleştirir (int64'e sığan tamsayılar birebir,
    /// decimal'e sığan ondalıklar düz gösterimle). Default (false) tarihsel davranışı korur:
    /// <c>TryGetInt32</c>-else-<c>GetDouble</c>, yani int64 aralığındaki tamsayılar ve 15+ haneli
    /// ondalıklar hassasiyet kaybeder (bkz. vnext-meta/known-issues.json).
    /// <para>
    /// Açmanın BİR KERELİK maliyeti: bugün (a) hassasiyet kaybı yaşayan veya (b) üstel gösterimle
    /// yazılan bir değer taşıyan instance'ların sonraki append'inde içerik hash'i değişir — o
    /// instance için bir fazladan versiyon satırı ve Monitor'da bir kerelik hayalet diff. Veri kaybı
    /// yoktur; geri dönüş flag'i kapatmaktır.
    /// </para>
    /// <para>
    /// <see cref="LegacyAppendPipeline"/> true iken bu flag YOK SAYILIR: kill-switch yolu tarihsel
    /// davranışa dönmek içindir ve dokunulmamıştır.
    /// </para>
    /// </summary>
    public bool PreserveNumericPrecision { get; set; }
```

2. `PlanAppend`: yeni parametre `bool preserveNumericPrecision = false`; legacy dalı **öncesinde** değişmez; yeni dalda:

```csharp
        var policy = preserveNumericPrecision
            ? JsonNumberPolicy.PreservePrecision
            : JsonNumberPolicy.Legacy;
        var result = JsonCanonicalizer.MergeAndCanonicalize(baseElement, delta.JsonElement, policy);
```

3. `AppendCoreAsync` çağrı yeri: `executionOptions.Value.InstanceDataWrite.PreserveNumericPrecision` geçirilir (mevcut `LegacyAppendPipeline` argümanının yanına).

- [ ] **Step 4: PASS + regresyon** — yeni testler + mevcut `InstanceDataWriteService*` testleri (15) yeşil; `dotnet test test/BBT.Workflow.Infrastructure.Tests 2>&1 | tail -3` → failure isimleri bilinen 11'lik pre-existing setle aynı (EfCoreInstanceCorrelationRepository*/EfCoreInstanceTransitionRepository*).

- [ ] **Step 5: Commit** — `feat(instances): PreserveNumericPrecision opt-in flag on the append pipeline`

---

## Task 3: Rastgele üreticiyi havuzlara ayır + known-issues kaydı

**Files:**
- Modify: `test/BBT.Workflow.Domain.Tests/Shared/Merging/JsonCanonicalizerParityTests.cs`
- Modify: `vnext-meta/known-issues.json`

- [ ] **Step 1: Üretici havuzları** — `RandomDecimalLexical` (L196-213) bugün 6 stil üretiyor; stil 3 (int64-aşan tamsayı), 4 (`e-`/`E+`) ve 5 (20-hane ondalık) `PreservePrecision`'da bilinçli olarak farklı çıktı verir. Üreticiye bir mod parametresi eklenir:

```csharp
    private enum NumberPool { Ordinary, All }

    // Ordinary: yalnız stil 0,1,2 (1.50 / 1e2 / 1.0) — iki modda AYNI metin.
    // All: bugünkü 6 stil (parite testleri Legacy modunda bunu kullanmaya devam eder).
```

`RandomJson(rng, depth)` → `RandomJson(rng, depth, NumberPool pool)`; mevcut `RandomizedParity_SmallGeneratedDocuments` testi `NumberPool.All` ile **aynen** kalır (Legacy modunda oracle paritesi). Yeni test:

```csharp
    [Fact]
    public void RandomizedOrdinaryValues_PreservePrecision_MatchesLegacy()
    {
        // Sıradan havuz: PreservePrecision kanonik formu GENİŞLETMEMELİ.
        var rng = new Random(42);
        for (var i = 0; i < 200; i++)
        {
            var baseJson = RandomJson(rng, depth: 0, NumberPool.Ordinary);
            var deltaJson = RandomJson(rng, depth: 0, NumberPool.Ordinary);
            var legacy = JsonCanonicalizer.MergeAndCanonicalize(
                new JsonData(baseJson).JsonElement, new JsonData(deltaJson).JsonElement,
                JsonNumberPolicy.Legacy).NormalizedJson;
            var preserved = JsonCanonicalizer.MergeAndCanonicalize(
                new JsonData(baseJson).JsonElement, new JsonData(deltaJson).JsonElement,
                JsonNumberPolicy.PreservePrecision).NormalizedJson;
            Assert.Equal(legacy, preserved);
        }
    }
```

- [ ] **Step 2: known-issues kaydı** — `vnext-meta/known-issues.json`'a (mevcut şekil: `id/affectedVersions/severity/component/path/title/workaround/fixedIn`):

```json
    {
      "id": "instance-data-numeric-precision-loss",
      "affectedVersions": "<=0.0.85",
      "severity": "warning",
      "component": "instance",
      "path": "instanceData.attributes (numbers)",
      "title": "Instance data appends reformat every number through int/double, so integers above 2^53 (including values that no longer fit int64) and decimals beyond ~15 significant digits are persisted with reduced precision — and the rewrite touches numbers the append never referenced.",
      "workaround": "Set WorkflowExecution:InstanceDataWrite:PreserveNumericPrecision=true to canonicalize numbers losslessly (int64 exact, decimal in plain notation). Enabling it changes the content hash once for instances holding affected or exponent-formatted values: one extra version row and one phantom diff for those instances, no data loss. Alternatively carry exact monetary values as strings in the schema.",
      "fixedIn": null
    }
```

- [ ] **Step 3: Doğrula** — `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~JsonCanonicalizer" 2>&1 | tail -3` (mevcut + yeni hepsi yeşil); `python3 -c "import json; json.load(open('vnext-meta/known-issues.json')); print('OK')"` + alan-seti tutarlılığı kontrolü.

- [ ] **Step 4: Commit** — `test(domain): split the random number pool; docs(vnext-meta): record the numeric precision known issue`

---

## Task 4: Kapanış

- [ ] **Step 1: İsim-diff** — Domain + Infrastructure + Application suite'leri; scratchpad'deki master baseline'larıyla isim karşılaştırması (yeni failure ismi = regresyon).
- [ ] **Step 2: Flag'in canlı doğrulaması (opsiyonel ama önerilir)** — 4 app ayaktaysa: orchestration+execution'ı `WorkflowExecution__InstanceDataWrite__PreserveNumericPrecision=true` ile yeniden kaldır, script-perf-lab integration testini koş (yeşil kalmalı), sonra normale dön. Kanıt: flag'in config yolu çalışıyor ve akışı bozmuyor.
- [ ] **Step 3: Spec §6 kriterlerini işaretle**; plan durum notu; memory güncellemesi (kontrolör).
- [ ] **Step 4: Commit** — `docs(superpowers): numeric precision closure`

---

## Riskler / kontrol noktaları

- **Kanonik formun kazara genişlemesi** → Task 1'in `OrdinaryCorpus` + Task 3'ün rastgele "sıradan havuz" invaryantı bunun bekçisi; ikisi de kırmızıya dönerse tasarım ihlali var.
- **`WriteRawValue`'nun geçerli JSON sayı token'ı üretmesi** → format string'in üstel gösterim/virgül üretmemesi zorunlu; kültür testi (`tr-TR`) ve token doğrulaması (`skipInputValidation: false`) bunu yakalar.
- **decimal aralığı dışı değerler** (>7.9e28) bugünkü double yoluna düşer — davranış değişmez, `LossyCorpus`'a KOYULMAZ.
- **Mevcut parite ağı** default `Legacy` sayesinde dokunulmadan kalır; herhangi bir mevcut parite testinin beklentisini değiştirmek gerekiyorsa DUR — bu, `Legacy` yolunun kazara değiştiğinin işaretidir.
