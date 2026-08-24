# Katman 1 — Compiler Hit-Yolu Optimizasyonları Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Compile-cache hit yolunun çağrı başına bedelini (~10µs + ~27KB alloc) ve helper'lı yolun çağrı başına ön-işini davranışı değiştirmeden öldürmek; per-task çift compile-lookup'ı teke indirmek.

**Architecture:** Anahtar üretimi iki parçaya ayrılır: `sourceHash` (kaynak SHA256 — inline'da `ScriptCode.ContentHash` lazy-memo, REF'te çağrı başına) + `profile` (refs/usings/grant/scope'un sıralanmış birleşimi — helper-set/grant başına BİR KEZ). Tek algoritma evaluator'da kalır (`BuildProfile` + `ComputeCacheKey`); engine memo'lar. Helper-set çözümlemesi generation-token bekçili memo'ya alınır (authored ref asla kimlik değil — floating çözümleme korunur). `TaskExecutorBase.GetOrCompileMappingAsync` context-ömürlü factory memo'suyla input+output'u tek compile'a düşürür; instance üretimi `ScriptActivator` (Expression-derlenmiş ctor + Services setter) ile allocation-hafif.

**Tech Stack:** Mevcut — Roslyn evaluator, prometheus-net metrikleri (DOKUNULMAZ), xUnit+Shouldly/Moq, BenchmarkDotNet.

**Spec:** `docs/superpowers/specs/2026-08-23-script-perf-katman1-design.md` · **Branch:** `feature/script-perf-katman0`
**Baseline referansları:** mikro `test/BBT.Workflow.Benchmarks/baselines/2026-08-23-master.md`; makro vnext-example `core/Workflows/script-perf-lab/README.md` § Sonuçlar.

**Değişmezler (her görevde geçerli):**
- #888 yarış-fix yapısı (`_typeCache` Lazy/eviction/`GetCacheScope`/assembly-adı-tam-anahtar) byte-byte korunur; yalnız anahtarın NASIL üretildiği değişir.
- Dış sözleşmeler değişmez: `IScriptEngine` mevcut üyeleri, metrik yüzeyi, davranış. Eklemeler additive.
- Cache-key FORMATI değişir (in-process cache — deploy'da doğal sıfırlanır, sorun değil) ama tek algoritmadan üretilir; "aynı girdi → aynı anahtar" property testi zorunlu.
- Bileşen-çözümleme memo ilkesi: authored ref tuple kimlik DEĞİL; ya obje/içerik kimliği ya generation-token bekçisi (spec kararı).
- Testler hedefli koşulur; kapanışta isim-diff (Katman 0 yöntemi).

---

## Task 1: `ScriptCode` memo alanları (A2 + ContentHash)

**Files:**
- Modify: `src/BBT.Workflow.Domain/Definitions/ScriptCode.cs`
- Test: `test/BBT.Workflow.Domain.Tests/Definitions/ScriptCodeTests.cs` (mevcut dosyaya ekle; xUnit `Assert` konvansiyonu)

- [ ] **Step 1: Failing testleri yaz** — `ScriptCodeTests.cs`'e ekle:

```csharp
    [Fact]
    public void DecodedCode_ShouldBeMemoized_SameReferenceAcrossAccesses()
    {
        var code = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("public class M {}"));
        var scriptCode = new ScriptCode("loc", code);

        var first = scriptCode.DecodedCode;
        var second = scriptCode.DecodedCode;

        Assert.Same(first, second); // memo: aynı string referansı, her erişimde yeni decode değil
    }

    [Fact]
    public void ContentHash_ShouldBeStable_AndDifferForDifferentSources()
    {
        var a1 = ScriptCode.FromNative("public class A {}");
        var a2 = ScriptCode.FromNative("public class A {}");
        var b = ScriptCode.FromNative("public class B {}");

        Assert.Equal(a1.ContentHash, a2.ContentHash);   // içerik-türevli, deterministik
        Assert.NotEqual(a1.ContentHash, b.ContentHash);
        Assert.Same(a1.ContentHash, a1.ContentHash);    // instance-başına bir kez hesaplanır
        Assert.Equal(64, a1.ContentHash.Length);        // SHA256 hex
    }

    [Fact]
    public void MemoFields_ShouldNotAffectValueEquality()
    {
        var x = ScriptCode.FromNative("public class M {}");
        var y = ScriptCode.FromNative("public class M {}");
        _ = x.ContentHash; // yalnız birinde memo tetiklenir
        _ = x.DecodedCode;

        Assert.Equal(x, y); // GetAtomicValues memo alanlarını içermez
    }
```

- [ ] **Step 2: FAIL doğrula** — `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~ScriptCodeTests" 2>&1 | tail -5` → `ContentHash` tanımsız (derleme hatası).

- [ ] **Step 3: Implementasyon** — `ScriptCode.cs`:

`DecodedCode` property'sini memo'lu hale getir (mevcut gövde `ComputeDecodedCode()` adlı private metoda taşınır — gövde BYTE-BYTE aynı kalır):

```csharp
    private string? _decodedCode;

    public string DecodedCode => _decodedCode ??= ComputeDecodedCode();
```

Altına ekle:

```csharp
    private string? _contentHash;

    /// <summary>
    /// SHA-256 hex of <see cref="DecodedCode"/>, computed once per instance. Content-derived —
    /// safe as a cache-identity component regardless of how this instance was materialized
    /// (fresh deserialization per read included). Empty-source scripts hash the empty string.
    /// Benign race: concurrent first accesses may compute twice and publish the same value.
    /// </summary>
    [JsonIgnore]
    public string ContentHash => _contentHash ??=
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(DecodedCode)));
```

`GetAtomicValues` DOKUNULMAZ (memo alanları girmez — test pinler). `using System.Text.Json.Serialization;` zaten var.

- [ ] **Step 4: PASS + regresyon** — `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~ScriptCodeTests" 2>&1 | tail -3` (mevcut 188 satırlık suite + 3 yeni: hepsi geçer); `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~ScriptCodeModelTests" 2>&1 | tail -3`.

- [ ] **Step 5: Commit** — `feat(domain): memoize ScriptCode decode and content hash`

---

## Task 2: Evaluator — `BuildProfile` + `ComputeCacheKey` + `precomputedCacheKey` + `ScriptActivator` (A4)

**Files:**
- Modify: `modules/BBT.Workflow.Modules.Scripting/BBT/Workflow/Scripting/Evaluators/IEvaluator.cs`
- Modify: `modules/BBT.Workflow.Modules.Scripting/BBT/Workflow/Scripting/Evaluators/CSharpEvaluator.cs`
- Create: `modules/BBT.Workflow.Modules.Scripting/BBT/Workflow/Scripting/Evaluators/ScriptActivator.cs`
- Test: `test/BBT.Workflow.Application.Tests/Scripting/CSharpEvaluatorCacheKeyTests.cs` (yeni), `ScriptActivatorTests.cs` (yeni)

- [ ] **Step 1: Failing testler**

`CSharpEvaluatorCacheKeyTests.cs` (Shouldly; `CSharpEvaluatorCompilationOutcomeTests`'in probe/references kalıbını kopyala — `IOutcomeProbe` benzeri yerel `ICacheKeyProbe` + `ProbeReferences()`):

```csharp
    [Fact]
    public async Task PrecomputedKey_MustEqualComputedKey_AndServeSameCompiledType()
    {
        var evaluator = new CSharpEvaluator();
        var source = "public class KeyProbe : BBT.Workflow.Scripting.CSharpEvaluatorCacheKeyTests.ICacheKeyProbe { public int Run() => 7; }";
        var refs = ProbeReferences();

        // 1) Normal yol derler
        var first = await evaluator.CompileToInstanceAsync<ICacheKeyProbe>(source, extraReferences: refs);
        first.Compiled.ShouldBeTrue();

        // 2) Precomputed yol: profile + sourceHash'ten üretilen anahtar AYNI derlenmiş tipe hit etmeli
        var profile = evaluator.BuildProfile(refs, usingDirectives: null, sandboxGrant: null, loadContext: null);
        var sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(source)));
        var key = evaluator.ComputeCacheKey(sourceHash, typeof(ICacheKeyProbe), profile);

        var second = await evaluator.CompileToInstanceAsync<ICacheKeyProbe>(
            source, extraReferences: refs, precomputedCacheKey: key);

        second.Compiled.ShouldBeFalse(); // hit — iki yol aynı anahtarı üretti
        second.Instance.GetType().ShouldBe(first.Instance.GetType());
    }

    [Fact]
    public void BuildProfile_IsOrderInsensitive_ForUsingsAndGrant()
    {
        var evaluator = new CSharpEvaluator();
        var p1 = evaluator.BuildProfile(null, new[] { "System", "System.Linq" }, new[] { "B", "A" }, null);
        var p2 = evaluator.BuildProfile(null, new[] { "System.Linq", "System" }, new[] { "a", "b" }, null);
        p1.ShouldBe(p2); // OrderBy + OrdinalIgnoreCase grant — bugünkü GenerateCacheKey semantiği
    }
```

`ScriptActivatorTests.cs`:

```csharp
    [Fact]
    public void Create_ReturnsFreshInstances_WithServicesInjected()
    {
        var services = Mock.Of<IScriptServices>();
        var i1 = ScriptActivator.Create<ActivatorProbe>(typeof(ActivatorProbe), services);
        var i2 = ScriptActivator.Create<ActivatorProbe>(typeof(ActivatorProbe), services);

        i1.ShouldNotBeSameAs(i2);                 // her çağrı taze instance
        i1.ExposedServices.ShouldBeSameAs(services); // ScriptBase.SetServices çağrıldı
    }

    public class ActivatorProbe : ScriptBase
    {
        public IScriptServices? ExposedServices => Services; // Services protected ise proxy property; gerçek üye adını ScriptBase'ten doğrula
    }
```

> `ScriptBase`'in Services üyesinin erişilebilirliğini oku (`grep -n "Services" src/.../ScriptBase.cs`); test probe'unu gerçek üyeye göre uyarla.

- [ ] **Step 2: FAIL doğrula** — filter `CSharpEvaluatorCacheKeyTests|ScriptActivatorTests` → derleme hatası (BuildProfile/ComputeCacheKey/precomputedCacheKey/ScriptActivator yok).

- [ ] **Step 3: `ScriptActivator`** — yeni dosya:

```csharp
using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using BBT.Workflow.Scripting.Functions;

namespace BBT.Workflow.Scripting.Evaluators;

/// <summary>
/// Allocation-light activator for compiled script types: a compiled parameterless-ctor delegate is
/// built once per <see cref="Type"/> and reused (replaces per-call <see cref="Activator.CreateInstance(Type)"/>).
/// Service injection semantics are identical to the evaluator's historical behaviour: a fresh
/// instance per call, <see cref="ScriptBase.SetServices"/> when applicable.
/// </summary>
public static class ScriptActivator
{
    private static readonly ConcurrentDictionary<Type, Func<object>> Factories = new();

    public static T Create<T>(Type compiledType, IScriptServices? services)
    {
        var factory = Factories.GetOrAdd(compiledType, static t =>
            Expression.Lambda<Func<object>>(Expression.Convert(Expression.New(t), typeof(object))).Compile());

        var instance = (T)factory();
        if (instance is ScriptBase scriptBase && services != null)
        {
            scriptBase.SetServices(services);
        }
        return instance;
    }
}
```

`CSharpEvaluator.CreateAndInjectServices<T>` gövdesi tek satıra iner: `=> ScriptActivator.Create<T>(compiledType, services);` (metot ve çağrı yerleri aynen kalır).

- [ ] **Step 4: Anahtar üretimini yeniden yapılandır** — `CSharpEvaluator.cs`:

1. `GenerateCacheKey`'in gövdesi ikiye bölünür — davranışsal içerik AYNEN taşınır:

```csharp
    /// <summary>
    /// Builds the profile half of the cache key: everything EXCEPT the source and target type —
    /// sandbox flag, load-context scope, sorted grant, sorted usings, sorted reference displays.
    /// Deterministic and order-insensitive so callers may compute it once per stable input set
    /// (helper set / grant profile) and reuse it across compiles.
    /// </summary>
    public string BuildProfile(
        IEnumerable<MetadataReference>? extraReferences,
        IEnumerable<string>? usingDirectives,
        IReadOnlyList<string>? sandboxGrant,
        AssemblyLoadContext? loadContext)
    {
        var cacheScope = GetCacheScope(loadContext);
        var sb = new StringBuilder();
        sb.Append("sbx:").Append(_sandbox.Enabled ? '1' : '0');
        if (!string.IsNullOrEmpty(cacheScope)) sb.Append("|alc:").Append(cacheScope);
        if (sandboxGrant != null)
            foreach (var grant in sandboxGrant.OrderBy(g => g, StringComparer.OrdinalIgnoreCase))
                sb.Append("|@@").Append(grant);
        if (usingDirectives != null)
            foreach (var directive in usingDirectives.OrderBy(u => u))
                sb.Append('|').Append(directive);
        if (extraReferences != null)
            foreach (var reference in extraReferences.OrderBy(r => r.Display))
                sb.Append('|').Append(reference.Display);
        return sb.ToString();
    }

    /// <summary>
    /// Combines a source hash (SHA-256 hex of the exact source text), the target type and a
    /// <see cref="BuildProfile"/> result into the final cache key. THE single key algorithm:
    /// the raw-string path routes through here too, so a precomputed key can never diverge.
    /// </summary>
    public string ComputeCacheKey(string sourceHashHex, Type targetType, string profile)
    {
        var material = string.Concat(sourceHashHex, "|", targetType.AssemblyQualifiedName, "|", profile);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
```

2. `GenerateCacheKey` bu ikisinin bileşimi olur (raw yol için tek doğruluk kaynağı):

```csharp
    private string GenerateCacheKey(
        string code, Type targetType,
        IEnumerable<MetadataReference>? extraReferences,
        IEnumerable<string>? usingDirectives,
        IReadOnlyList<string>? sandboxGrant,
        AssemblyLoadContext? loadContext)
    {
        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
        return ComputeCacheKey(sourceHash, targetType, BuildProfile(extraReferences, usingDirectives, sandboxGrant, loadContext));
    }
```

(İmza değişikliği: `cacheScope` parametresi kalkar — loadContext'ten türetme `BuildProfile` içine indi; `CompileToInstanceAsync` çağrı yeri buna göre sadeleşir. `GetCacheScope`'un CWT'si ve yorumları AYNEN kalır.)

3. `CompileToInstanceAsync<T>`'ye parametre: `string? precomputedCacheKey = null` (imzanın SONUNA). Gövdede:

```csharp
        var cacheKey = precomputedCacheKey ?? GenerateCacheKey(
            code, typeof(T), extraReferences, usingDirectives, sandboxGrant, loadContext);
```

(fast-path/GetOrAdd/eviction blokları DEĞİŞMEZ). `IEvaluator`'a üç ekleme: `precomputedCacheKey` parametresi (XML doc: "caller MUST have produced it via BuildProfile+ComputeCacheKey with the very same inputs; a divergent key serves the wrong compiled type"), `BuildProfile`, `ComputeCacheKey`. Test double'lar (`DelegatingEvaluator`) derleyici-güdümlü güncellenir (delege eder).

- [ ] **Step 5: PASS + kritik regresyon** — filter `CSharpEvaluatorCacheKeyTests|ScriptActivatorTests|CSharpEvaluatorConcurrencyTests|CSharpEvaluatorCompilationOutcomeTests|SandboxedScriptingTests` → HEPSİ geçer (özellikle ConcurrencyTests: `CompileInvocationCount==1` ve ALC-scope davranışı — anahtar yapılandırması değişse de dedup/scope invariantları korunmalı). `dotnet build vnext.sln` 0 error.

- [ ] **Step 6: Commit** — `feat(scripting): split cache key into source hash + reusable profile; expression-compiled activator`

---

## Task 3: Engine — profil memo'ları + precomputed anahtar (A1+A3)

**Files:**
- Modify: `src/BBT.Workflow.Application/Scripting/ScriptEngine.cs`
- Test: `test/BBT.Workflow.Application.Tests/Scripting/ScriptEngineKeyMemoTests.cs` (yeni)

- [ ] **Step 1: Failing test** — gerçek evaluator + mock'lu engine kurulumu (`ScriptEngineHelperSetIsolationTests.cs:84-111` kalıbını kopyala):

```csharp
    [Fact]
    public async Task InlineScriptCode_SecondCompile_UsesPrecomputedKey_AndHits()
    {
        // Aynı ScriptCode ile iki compile: ikincisi hit olmalı (Compiled=false zaten Katman 0'da
        // pinli); bu test ENGINE yolunun ScriptCode.ContentHash + profile memo'su üzerinden
        // precomputed anahtar ürettiğini, davranışın raw yolla birebir kaldığını pinler:
        // aynı ScriptCode'u RAW string yoluyla derleyen üçüncü çağrı da AYNI tipe hit etmelidir.
        var sc = ScriptCode.FromNative(
            "public class KeyMemoProbe" + Guid.NewGuid().ToString("N") +
            " : ITransitionMapping { public Task<dynamic> Handler(ScriptContext context) => Task.FromResult<dynamic>(1); }");

        var viaScriptCode = await engine.CompileToInstanceAsync<ITransitionMapping>(sc);
        var viaScriptCode2 = await engine.CompileToInstanceAsync<ITransitionMapping>(sc);
        var viaRaw = await engine.CompileToInstanceAsync<ITransitionMapping>(sc.DecodedCode);

        viaScriptCode.GetType().ShouldBe(viaScriptCode2.GetType());
        viaRaw.GetType().ShouldBe(viaScriptCode.GetType()); // iki yol aynı anahtara çıkar
    }
```

(`ITransitionMapping.Handler` imzasını dosyadan doğrula — TransitionDataMapper `mappingInstance.Handler(scriptContext)` çağırıyor; mevcut testlerdeki örnek gövdeyi kopyala.)

- [ ] **Step 2: FAIL değil DAVRANIŞ testi** — bu test bugün de GEÇER (anahtar zaten deterministik). Kırmızı adım Task 2'nin property testiydi; burada test önce yazılır, implementasyon sonrası da yeşil kalması "davranış değişmedi" kanıtıdır. Koş, geçtiğini not et.

- [ ] **Step 3: Implementasyon** — `ScriptEngine.cs`:

1. Statik memo alanları:

```csharp
    /// <summary>Profile for the no-helper path, keyed by effective-grant identity (see GrantKeyOf).</summary>
    private static readonly ConcurrentDictionary<string, string> BaseProfiles = new();

    /// <summary>Profile per process-shared helper set (registry-cached ⇒ stable object identity).</summary>
    private static readonly ConditionalWeakTable<HelperSet, string> HelperProfiles = new();

    /// <summary>MergeDefaultGrant result per grant-list object (definition objects are replaced on publish).</summary>
    private static readonly ConditionalWeakTable<IReadOnlyList<string>, IReadOnlyList<string>> MergedGrants = new();

    private static string GrantKeyOf(IReadOnlyList<string>? grant) =>
        grant is null or { Count: 0 } ? "" : string.Join("|", grant.OrderBy(g => g, StringComparer.OrdinalIgnoreCase));
```

2. `MergeDefaultGrant` çağrıları memo'lu sarmalayıcıdan geçer:

```csharp
    private static IReadOnlyList<string> MergedGrantOf(IReadOnlyList<string>? grant)
        => grant is null or { Count: 0 }
            ? DefaultReferenceAssemblyNames.Value
            : MergedGrants.GetValue(grant, static g => MergeDefaultGrant(g));
```

3. ScriptCode overload'ında (L207+), helper'SIZ dalda:

```csharp
        if (effective?.HasHelpers != true)
        {
            var profile = BaseProfiles.GetOrAdd(GrantKeyOf(grant), _ =>
                _evaluator.BuildProfile(DefaultReferences.Value, DefaultUsings, MergedGrantOf(grant), loadContext: null));
            var sourceHash = scriptCode.IsReference
                ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))
                : scriptCode.ContentHash;
            var key = _evaluator.ComputeCacheKey(sourceHash, typeof(T), profile);
            return await CompileCoreAsync<T>(body, extraReferences: null, usingDirectives: null,
                loadContext: null, grant, cancellationToken, precomputedCacheKey: key);
        }
```

> DİKKAT — doğruluk şartı: `BuildProfile`'a verilen girdiler, `CompileCoreAsync`'in evaluator'a fiilen geçirdiği girdilerle AYNI olmalı. `CompileCoreAsync` bugün `mergedReferences = (extra ?? []).Concat(DefaultReferences.Value).Distinct()` ve `mergedUsings = (usings ?? []).Concat(DefaultUsings).Distinct()` üretir; extra/usings null iken bunlar `DefaultReferences.Value`/`DefaultUsings`'e eşdeğerdir (Distinct sırayı korur, Default setleri zaten tekildir — bir kerelik assert ile Step 5'te doğrulanır). `precomputedCacheKey` YALNIZ extra-ref/using'lerin null olduğu (engine'in kendi doldurduğu) dallarda geçilir; dış çağıran extraReferences verdiyse (public API) precomputed yol KULLANILMAZ — raw davranış.

4. Helper'LI dalda (mevcut helperSet alındıktan sonra):

```csharp
            var helperProfile = HelperProfiles.GetValue(helperSet, hs =>
                _evaluator.BuildProfile(
                    DefaultReferences.Value.Append(hs.Reference),
                    DefaultUsings.Concat(hs.Namespaces),
                    MergedGrantOf(grant),
                    hs.LoadContext));
```

ve compile çağrısı `precomputedCacheKey: _evaluator.ComputeCacheKey(sourceHash, typeof(T), helperProfile)` ile. NOT: `HelperProfiles` CWT'si helper set'in grant'a bağlı kimliğiyle çelişmez — `HelperSet` zaten `(kaynaklar+grant)` hash'iyle cache'lendiğinden aynı obje = aynı grant; yine de profile grant'ı içerir (çifte güvence). `FromCache=true` kopyaları `with` ile YENİ obje üretir (record) — CWT anahtarı orijinal set olmalı: registry dönüşünün `with { FromCache = true }` kopyasında CWT ıskalanır ve profil yeniden üretilir (~ilk maliyet, doğruluk etkisi yok). Bunu önlemek için profili `helperSet.FromCache` kopyası yerine registry'nin döndürdüğü objenin kendisiyle anahtarla; pratik çözüm: profile üretimini `ComputeCacheKey` öncesinde `hs.Reference` CWT'siyle yap (`MetadataReference` kopyalanmaz): `ConditionalWeakTable<MetadataReference, string>` kullan — plan tercihimiz BU (Reference objesi set kopyalarında aynı kalır).

5. `CompileCoreAsync`'e parametre: `string? precomputedCacheKey = null`; evaluator çağrısına geçirilir. Metrik satırları DEĞİŞMEZ.

- [ ] **Step 4: Doğruluk assert'i (Step 3 notu)** — `ScriptEngineKeyMemoTests`'e ikinci test: helper'LI yolda da raw-vs-precomputed aynı tipe çıkar (IsolationTests'in helper kurulumunu kopyala; aynı mapping'i bir kez ScriptCode yoluyla, bir kez engine'in eski davranışını taklit için — mevcut testlerdeki gibi — derle ve `evaluator.CachedTypeCount` artışının 1'de kaldığını assert et: iki yol tek cache girdisi üretir).

- [ ] **Step 5: PASS + regresyon** — filter `ScriptEngineKeyMemoTests|ScriptEngineTests|ScriptEngineHelperSetIsolationTests|FanOutMappingScriptCompilationTests` hepsi yeşil; build 0 error.

- [ ] **Step 6: Commit** — `perf(scripting): precomputed cache keys via content hash + memoized profiles`

---

## Task 4: Engine — generation-token bekçili helper-set memo'su (A7)

**Files:**
- Modify: `src/BBT.Workflow.Application/Scripting/ScriptEngine.cs`
- Test: `test/BBT.Workflow.Application.Tests/Scripting/ScriptEngineHelperMemoTests.cs` (yeni)

- [ ] **Step 0: API teyidi** — `IComponentGenerationProvider` DI kaydını ve mappings için `componentTypeKey` sabitini bul: `grep -rn "IComponentGenerationProvider" src/ --include="*.cs" | grep -i "AddSingleton\|AddScoped"` ve `grep -rn "componentTypeKey\|GetAsync(" src/BBT.Workflow.Application/Caching/CacheSet.cs | head`. Mappings CacheSet'inin token okurken kullandığı TAM (componentTypeKey, domain, key) üçlüsünü not et — memo bekçisi AYNI üçlüyü kullanmalı (farklı anahtar = bekçi hiç tetiklenmez).

- [ ] **Step 1: Failing testler** — mock `IComponentCacheStore` + mock `IComponentGenerationProvider` ile engine kur (IsolationTests kalıbı + provider mock'u):

```csharp
    [Fact]
    public async Task HelperResolution_SecondCompile_SkipsStoreWhenTokensUnchanged()
    {
        // token sabit → ikinci compile'da GetMappingAsync HİÇ çağrılmamalı
        await engine.CompileToInstanceAsync<IHelperValueMapping>(scriptCodeWithHelper);
        componentStore.Invocations.Clear();

        await engine.CompileToInstanceAsync<IHelperValueMapping>(scriptCodeWithHelper);

        componentStore.Verify(s => s.GetMappingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HelperResolution_TokenBump_TriggersFullReResolve_AndNewHelperContentWins()
    {
        await engine.CompileToInstanceAsync<IHelperValueMapping>(scriptCodeWithHelper);

        // hotfix simülasyonu: token değişir + store yeni içerik döndürür
        generationProvider.Setup(p => p.GetAsync(TypeKey, Domain, HelperKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync("gen-2");
        SetupStoreToReturn(helperSourceV2); // Value() artık "v2" döndüren helper

        var result = await engine.CompileToInstanceAsync<IHelperValueMapping>(scriptCodeWithHelper);

        componentStore.Verify(s => s.GetMappingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        result.GetValue().ShouldBe("v2"); // floating çözümleme görünürlüğü birebir
    }
```

(Kurulum ayrıntıları — helper ScriptCode'u, `IHelperValueMapping` arayüzü, store setup'ı — `ScriptEngineHelperSetIsolationTests.cs`'ten kopyalanır; provider `services.AddSingleton(generationProvider.Object)` ile engine'in `_serviceProvider`'ına girer.)

- [ ] **Step 2: FAIL doğrula** — ilk test kırmızı (bugün her compile store'a gider).

- [ ] **Step 3: Implementasyon** — `ScriptEngine.cs`:

```csharp
    private sealed record ResolvedHelperSet(
        IReadOnlyList<HelperSource> Sources,
        string[] Tokens,
        HelperSet Set);

    /// <summary>
    /// Helper-set resolution memo, guarded by component generation tokens. The authored reference
    /// list is NEVER the identity by itself (versions resolve floating — a hotfix publish must be
    /// visible on the next resolution); each use re-reads the per-helper generation tokens and any
    /// mismatch forces a full re-resolve. Keyed per (authored refs + grant) string.
    /// </summary>
    private static readonly ConcurrentDictionary<string, ResolvedHelperSet> HelperSetMemo = new();
```

Helper'lı dalda `ResolveHelperSourcesAsync` + `GetOrBuildHelpers` bloğu şu akışa alınır:

```csharp
        var memoKey = string.Join("|", effective.Helpers!.Select(h => h.ToString())) + "||" + GrantKeyOf(grant);
        var generationProvider = _serviceProvider.GetRequiredService<IComponentGenerationProvider>();

        var tokens = new string[effective.Helpers!.Count];
        for (var i = 0; i < effective.Helpers.Count; i++)
        {
            var h = effective.Helpers[i];
            tokens[i] = await generationProvider.GetAsync(MappingsTypeKey, h.Domain, h.Key, cancellationToken);
        }

        HelperSet helperSet;
        if (HelperSetMemo.TryGetValue(memoKey, out var memo) && memo.Tokens.AsSpan().SequenceEqual(tokens))
        {
            helperSet = memo.Set; // ne store turu ne HashOf: tek sözlük + N ucuz token karşılaştırması
            _logger.ScriptHelperSetCacheHit(memo.Sources.Count, memoKey);
        }
        else
        {
            var helperSources = await ResolveHelperSourcesAsync(effective.Helpers!, cancellationToken);
            helperSet = _helperRegistry.GetOrBuildHelpers(
                helperSources, MergedGrantOf(grant), DefaultReferences.Value, DefaultUsings, cancellationToken);
            HelperSetMemo[memoKey] = new ResolvedHelperSet(helperSources, tokens, helperSet with { FromCache = false });
            // log satırları mevcut FromCache dallanmasıyla aynen
        }
```

`MappingsTypeKey` sabiti Step 0'daki teyide göre tanımlanır. Mevcut `FromCache` log dallanması korunur (memo-hit → CacheHit logu). NOT: token okuma maliyeti bugünkü `GetMappingAsync` yolunun zaten ödediği token okumasıyla aynıdır (CacheSet her get'te okur) — net kazanç L1-deserialize + DecodedCode + HashOf + kaynak listesi kurulumudur. `ComponentCache:GenerationMemoSeconds` (mevcut config, default 0) açılırsa token okumaları da in-process olur — runbook notu yazılır, default DEĞİŞTİRİLMEZ.

- [ ] **Step 4: PASS + regresyon** — yeni testler + `ScriptEngineHelperSetIsolationTests` (izolasyon invariantı: farklı helper setleri farklı tip — memo bunu bozmamalı) + `ScriptEngineTests`.

- [ ] **Step 5: Commit** — `perf(scripting): generation-token guarded helper-set resolution memo`

---

## Task 5: `IScriptEngine.CompileToFactoryAsync<T>` + `TaskExecutorBase.GetOrCompileMappingAsync<T>` + executor süpürmesi

**Files:**
- Modify: `src/BBT.Workflow.Domain/Scripting/IScriptEngine.cs` (arayüzün gerçek dosya yolunu doğrula: `grep -rn "interface IScriptEngine" src/`)
- Modify: `src/BBT.Workflow.Application/Scripting/ScriptEngine.cs`
- Modify: `src/BBT.Workflow.Application/Tasks/Executors/Core/TaskExecutorBase.cs` + `TaskExecutorContext` (dosya: `grep -rn "sealed record TaskExecutorContext" src/`)
- Modify: 14 executor dosyasındaki 28 compile çağrısı (liste: `grep -rn "CompileToInstanceAsync" src/BBT.Workflow.Application/Tasks/Executors/ --include="*.cs"`)
- Test: `test/BBT.Workflow.Application.Tests/Tasks/TaskExecutorMappingMemoTests.cs` (yeni)

- [ ] **Step 1: Failing test**

```csharp
    [Fact]
    public async Task SamePipeline_InputAndOutput_CompileOnce_ButGetFreshInstances()
    {
        var engine = new Mock<IScriptEngine>();
        var created = 0;
        engine.Setup(e => e.CompileToFactoryAsync<IMapping>(It.IsAny<ScriptCode>(), It.IsAny<ScriptSettings?>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(() => { return () => { created++; return Mock.Of<IMapping>(); }; });

        var context = TestTaskContexts.ScriptTaskWithMapping(); // Katman 0'daki fixture'ı yeniden kullan/kopyala

        var probe = new MemoProbeExecutor(NullLogger.Instance, Mock.Of<IWorkflowMetrics>(), engine.Object);
        var a = await probe.CallGetOrCompile(context);
        var b = await probe.CallGetOrCompile(context);

        engine.Verify(e => e.CompileToFactoryAsync<IMapping>(It.IsAny<ScriptCode>(), It.IsAny<ScriptSettings?>(), It.IsAny<CancellationToken>()), Times.Once);
        a.ShouldNotBeSameAs(b);   // her faz taze instance
        created.ShouldBe(2);

        var context2 = TestTaskContexts.ScriptTaskWithMapping();
        await probe.CallGetOrCompile(context2);
        engine.Verify(e => e.CompileToFactoryAsync<IMapping>(It.IsAny<ScriptCode>(), It.IsAny<ScriptSettings?>(), It.IsAny<CancellationToken>()), Times.Exactly(2)); // memo context-ömürlü
    }
```

(`MemoProbeExecutor`: `TaskExecutorBase<ScriptTask>` alt sınıfı, `CallGetOrCompile` → `GetOrCompileMappingAsync<IMapping>(engineField, context, ct)` — helper'ın engine'i parametre aldığı imzayla.)

- [ ] **Step 2: Implementasyon**

1. `IScriptEngine`'e ADDITIVE üye (+XML doc):

```csharp
    /// <summary>
    /// Compiles (or cache-hits) the mapping once and returns a factory producing a FRESH,
    /// service-injected instance per call. Compile-time behaviour and metrics are identical to
    /// <see cref="CompileToInstanceAsync{T}(ScriptCode, ScriptSettings?, ...)"/> — this exists so a
    /// caller invoking the same mapping in multiple phases pays the engine exactly once.
    /// </summary>
    Task<Func<T>> CompileToFactoryAsync<T>(ScriptCode scriptCode, ScriptSettings? flowScripts = null, CancellationToken cancellationToken = default);
```

`ScriptEngine` implementasyonu: mevcut ScriptCode overload'ının gövdesini izleyerek İLK instance'ı normal yoldan üretir, tipini alır ve `() => ScriptActivator.Create<T>(compiledType, _scriptServices)` döndürür; ilk instance israf edilmez:

```csharp
    public async Task<Func<T>> CompileToFactoryAsync<T>(ScriptCode scriptCode, ScriptSettings? flowScripts = null, CancellationToken cancellationToken = default)
    {
        var first = await CompileToInstanceAsync<T>(scriptCode, flowScripts, cancellationToken: cancellationToken);
        var compiledType = first!.GetType();
        var consumedFirst = 0;
        return () => Interlocked.Exchange(ref consumedFirst, 1) == 0
            ? first
            : ScriptActivator.Create<T>(compiledType, _scriptServices);
    }
```

(`ScriptActivator` modules asm'de public — Application zaten Modules.Scripting'e referanslı; değilse `grep ProjectReference src/BBT.Workflow.Application/*.csproj` ile teyit, değilse factory'yi evaluator üzerinden expose et ve planı DONE_WITH_CONCERNS raporla.)

2. `TaskExecutorContext`'e memo alanı (mutable prop emsali `InputResponse`):

```csharp
    /// <summary>Per-execution compiled-mapping factory memo — see TaskExecutorBase.GetOrCompileMappingAsync.</summary>
    public Dictionary<(ScriptCode Mapping, Type Target), object>? CompiledMappingFactories { get; set; }
```

3. `TaskExecutorBase`'e helper:

```csharp
    /// <summary>
    /// Compiles the task's mapping once per task execution and hands out FRESH instances per phase.
    /// The factory is memoized on the TaskExecutorContext, so PrepareInput and ProcessOutput/Invoke
    /// share a single engine call; instance-per-phase semantics are unchanged (a user script holding
    /// instance fields observes exactly today's behaviour).
    /// </summary>
    protected static async Task<T> GetOrCompileMappingAsync<T>(
        IScriptEngine scriptEngine, TaskExecutorContext context, CancellationToken cancellationToken)
        where T : class
    {
        var mapping = context.OnExecuteTask.Mapping;
        var key = (mapping, typeof(T));
        context.CompiledMappingFactories ??= new Dictionary<(ScriptCode, Type), object>();
        if (!context.CompiledMappingFactories.TryGetValue(key, out var boxed))
        {
            boxed = await scriptEngine.CompileToFactoryAsync<T>(
                mapping, context.ScriptContext.Workflow?.Scripts, cancellationToken);
            context.CompiledMappingFactories[key] = boxed;
        }
        return ((Func<T>)boxed)();
    }
```

(Pipeline tek-thread'li per task execution — sözlükte kilit gerekmez; yorumda belirt.)

4. **Süpürme:** 28 çağrı yerinde `await scriptEngine.CompileToInstanceAsync<X>(task.Mapping/context.OnExecuteTask.Mapping, flowScripts: context.ScriptContext.Workflow?.Scripts, cancellationToken: ct)` deseni → `await GetOrCompileMappingAsync<X>(scriptEngine, context, ct)`. YALNIZ mapping'i `context.OnExecuteTask.Mapping`/`task.Mapping` olan ve flowScripts'i `context.ScriptContext.Workflow?.Scripts` olan çağrılar dönüştürülür — farklı kaynaklı compile'lar (örn. FanOut'un item-mapping özel akışı farklıysa) OLDUKLARI GİBİ bırakılır ve raporlanır. Derleyici + `grep` ile tamamlanır; her dosyada davranış değişikliği YOK (yalnız çağrı şekli).

- [ ] **Step 3: PASS + geniş regresyon** — yeni test + `TaskExecutorBaseMetricsTests` (fazlar hâlâ kaydediyor) + `dotnet test test/BBT.Workflow.Application.Tests 2>&1 | tail -3` (isim karşılaştırma: Task 4-Katman 0'daki 20 pre-existing seti).

- [ ] **Step 4: Commit** — `perf(tasks): single compile per task execution via context-scoped mapping factories`

---

## Task 6: Mikro benchmark — `CompileHitPathIdentityBenchmarks` + önce/sonra

**Files:**
- Create: `test/BBT.Workflow.Benchmarks/CompileHitPathIdentityBenchmarks.cs`
- Modify: `test/BBT.Workflow.Benchmarks/baselines/2026-08-23-master.md` (Katman 1 bölümü ekle)

- [ ] **Step 1: Suite** — engine-seviyesi, `ScriptEngineHelperSetIsolationTests`'in mock'lu engine kurulumunu benchmark'a uyarla (Mock yerine basit stub sınıfları — BenchmarkDotNet child process'inde Moq yerine elde yazılmış no-op `IScriptServices`/`IWorkflowMetrics`/`ILogger` stub'ları kullan; Application.Tests'e referans YOK):

```csharp
[MemoryDiagnoser]
[GcServer(true)]
public class CompileHitPathIdentityBenchmarks
{
    private ScriptEngine _engine = null!;
    private ScriptCode _scriptCode = null!;

    [Params(1, 4, 16)]
    public int SourceKb { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var evaluator = new CSharpEvaluator();
        var services = new ServiceCollection().BuildServiceProvider();
        _engine = new ScriptEngine(
            evaluator,
            new NoopScriptServices(),          // elde yazılmış stub: her üye no-op/null
            new NoopWorkflowMetrics(),         // elde yazılmış stub: tüm Record*/Set* boş gövde
            new ScriptHelperRegistry(evaluator),
            new ScriptHelpersOptions { Enabled = false },
            services,
            NullLogger<ScriptEngine>.Instance);

        // Hedef tip ITransitionMapping: assembly'si engine'in DefaultReferences'ında olduğundan
        // extraReferences GEREKMEZ → precomputed-key (identity) yolu ölçülür. IBenchScript gibi
        // benchmark-assembly'sinden bir tip kullanmak extraReferences gerektirir ve Task 3 kuralı
        // gereği çağrıyı raw yola düşürürdü.
        var padding = new string('/', 80) + "\n";
        var pad = string.Concat(Enumerable.Repeat(padding, SourceKb * 1024 / padding.Length + 1));
        _scriptCode = ScriptCode.FromNative(
            pad + "\npublic class IdProbe : ITransitionMapping { public Task<dynamic> Handler(ScriptContext context) => Task.FromResult<dynamic>(42); }\n");
        // (ITransitionMapping.Handler'ın gerçek imzasını src'den doğrula; Task 3 testindeki gövdeyle aynı kalıbı kullan.)

        _ = _engine.CompileToInstanceAsync<ITransitionMapping>(_scriptCode).GetAwaiter().GetResult();
    }

    // NoopScriptServices / NoopWorkflowMetrics: bu dosyanın altında elde yazılmış, tüm üyeleri
    // no-op iki stub sınıf (Moq benchmark child-process'ine taşınmaz).

    [Benchmark]
    public object WarmCompileViaScriptCode()
        => _engine.CompileToInstanceAsync<ITransitionMapping>(_scriptCode).GetAwaiter().GetResult();
}
```

(IBenchScript mevcut; ScriptCode target tipi için engine'in ScriptCode overload'u kullanılır. Engine ctor bağımlılıkları: evaluator=new CSharpEvaluator(), registry=new ScriptHelperRegistry(evaluator), options=new ScriptHelpersOptions{Enabled=false}, minimal ServiceCollection.)

- [ ] **Step 2: Koşum + kayıt**

```bash
dotnet run -c Release --project test/BBT.Workflow.Benchmarks -- --filter "*CompileHitPath*" --exporters markdown 2>&1 | tail -20
```

(Eski `CompileHitPathBenchmarks` raw-evaluator suite'i de koşulur — raw yol İYİLEŞEBİLİR (kaynak artık SB'ye kopyalanmıyor); sözleşme aynı, sayılar rapor edilir.) `baselines/2026-08-23-master.md`'ye "Katman 1 sonrası (tarih)" bölümü: iki suite'in önce/sonra tablosu + kısa yorum.

- [ ] **Step 3: Commit** — `docs(benchmarks): Katman 1 micro before/after`

---

## Task 7: Kapanış — makro yeniden ölçüm + helper-hotfix doğrulaması + isim-diff + dokümantasyon

- [ ] **Step 1: İsim-diff regresyonu** — Katman 0 yöntemi: Domain/Application/Infrastructure suite'leri branch'te koş, master worktree'de aynılarını koş, isim comm -23 BOŞ olmalı.
- [ ] **Step 2: Makro yeniden ölçüm** — 4 app'i YENİDEN BAŞLAT (yeni build; ayaktakiler eski kodu koşuyor — önce durdur), Faz A runbook'uyla: taze nonce publish → soğuk → `--parallel 20 --iterations 3` × payload 4KB ve 16KB + dotnet-counters. Beklenen imza: hit/instance **33 → ~23**, `miss=+0`, p50/p95 ve alloc/GC düşüşü. Sonuçlar vnext-example README "Sonuçlar" bölümüne "Katman 1 sonrası" alt tablosu olarak eklenir (baseline tablosu SİLİNMEZ).
- [ ] **Step 3: Helper-hotfix el doğrulaması** — apps AÇIKKEN: script-perf-lab chunk helper'ının stamp'ine v-işareti ekleyip helper'ı YENİ versiyonla publish et (`perf-stamp-helper` 1.0.1), workflow'a DOKUNMA; yeni bir instance başlat → attributes'taki stamp yeni içeriği göstermeli (token bekçisi + floating çözümleme kanıtı). Sonucu README'ye tek satır not düş; helper'ı sonra eski haline getirme (fixture ileri sürümde kalabilir — nonce üretici zaten versiyonları yönetiyor; değişikliği vnext-example branch'ine commit et).
- [ ] **Step 4: Dokümantasyon + durum** — Katman 0 plan durum tablosuna Katman 1 satırı; spec §6 kriterlerini işaretle; memory güncellemesi (kontrolör).
- [ ] **Step 5: Commit'ler** — vnext: `docs(perf): Katman 1 closure — macro before/after`; vnext-example: `docs(script-perf-lab): Katman 1 rerun results + helper hotfix verification`.

---

## Riskler / kontrol noktaları

- **Anahtar sapması** = yanlış script: Task 2 property testi + Task 3'ün raw-vs-precomputed eş-tip testleri + makro `miss=+0` + hotfix doğrulaması. `precomputedCacheKey` YALNIZ engine'in kendi kontrol ettiği girdi setlerinde kullanılır (dış extraReferences'lı çağrılar raw yolda kalır).
- **CWT/obje-kimliği varsayımları**: HelperSet→registry-cache paylaşımlı (teyitli); `MetadataReference` set kopyalarında sabit (Task 3.4 tercihi bu yüzden); grant listesi definition objesi (yeni publish → yeni obje). Workflow/ScriptCode objelerinin süreç-paylaşımlılığı Task 3'te davranışı DEĞİL yalnız isabet oranını etkiler.
- **Token üçlüsü yanlışsa bekçi çalışmaz** → Task 4 Step 0 zorunlu teyit + hotfix testi bunu yakalar.
- 28-çağrılık süpürme mekanik ama geniş → derleyici-güdümlü + tam suite isim-diff.
