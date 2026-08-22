# Katman 0 — Script Perf Ölçüm Altyapısı Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Script compiler'a hit/miss görünürlüğü, gerçek script-execution metrikleri ve BenchmarkDotNet mikro-baseline'ı eklemek — hiçbir mevcut metrik/davranış kırılmadan (additive-only).

**Architecture:** `IEvaluator.CompileToInstanceAsync` sonucu hit/miss + derleme süresi taşıyan bir record'a dönüşür (tek production tüketici `ScriptEngine`); `ScriptEngine` yeni `script_compilations_total{result}` sayacını ve mevcut compile histogramına eklenen `cache` label'ını besler; ölü `RecordScriptExecutionDuration`/`RecordScriptRuntimeError` dört huni noktasına bağlanır (TaskExecutorBase fazları, TransitionDataMapper, ScriptConditionEvaluator, FunctionAppService). Deprecated `script_executions_total` AYNEN akmaya devam eder.

**Tech Stack:** .NET 10, prometheus-net (mevcut `WorkflowMetrics` deseni), BenchmarkDotNet (yeni), xUnit + Moq/NSubstitute + Shouldly (mevcut test altyapısı).

**Spec:** `docs/superpowers/specs/2026-08-23-script-perf-katman0-design.md`
**Branch:** `feature/script-perf-katman0` (mevcut, spec commit'leri üzerinde)

## Yürütme durumu (2026-08-23, subagent-driven)

| Task | Durum | Commit(ler) | Not |
|---|---|---|---|
| 1 Evaluator outcome | ✅ spec+kalite onaylı | `19020dcb` | +CSharpEvaluatorConcurrencyTests mekanik `.Instance` uyarlaması |
| 2 Metrik yüzeyi | ✅ | `e8c165c0`, `2f6527ff` | counter stili dosya konvansiyonuna hizalandı |
| 3 ScriptEngine wiring | ✅ | `69b7ec73`, `7e880cf9` | gauge yalnız miss'te (sapma notu Task 3 altında) |
| 4 Huniler | ✅ | `e2fcbaca`, `ce92aade` | gate: `OnExecuteTask.Mapping.HasMappingCode`; OCE runtime-error sayılmaz; 21 executor + 7 test dosyası |
| 5 Dashboard | ✅ | `73d16c67`, `7a8ac1a6` | hit-ratio paydası `status="success"` |
| 6 vnext-meta | ✅ (validator koşuldu) | `eae47ec` | type=`behavior` (repo emsali); pre-existing drift ayrı işlere çıkarıldı |
| 7 Benchmarks | ✅ | `da360c9e`, `2d12fbb7` | +`[GcServer(true)]`, ParseJsonElement memoization-bağımlılık notu |
| 8 Mikro baseline | ✅ 5/5 suite | `d73d3d87` | Server GC konsol-doğrulamalı; LOH varyans notu baseline'da |
| 9 Kapanış | ✅ | — | Build temiz; Domain+Infra isim-diff'i master worktree'e karşı BOŞ (yeni failure yok); App.Tests Task 4'te stash-diff'le doğrulandı |

**Bilinen ölçüm asimetrileri (final review notu):** `status="failure"` süreleri yalnız task hunilerinde
üretilir (diğer üç huni yalnız success süresi yazar); exception yolları hiçbir hunide süre yazmaz
(yalnız `script_runtime_errors_total`). Hata fırtınasında p95 yalnız başarılı çalıştırmaları temsil eder.

**Baseline özet (2026-08-23, tam sonuç: `test/BBT.Workflow.Benchmarks/baselines/2026-08-23-master.md`):**
CompileHit 16KB: 12.5µs/98.8KB · Expando 200KB: 2.24ms/2.85MB · Branch 50KB: 1.29ms/1.72MB · NormalizeFresh 200KB: 6.15ms/8.43MB · Audit 50KB: 87.5µs/100.6KB

**Çalışma kuralları:**
- Her görev sonunda commit. Test komutlarında `--filter` ile hedefli koş; full suite'te master'da ~191 pre-existing failure var (çoğu AmbientServiceProvider paralel-koleksiyon sızması) — sayı değil, **isim bazlı** karşılaştır.
- `dotnet build` uyarı üretmemeli (yeni kod için).
- Politika: hiçbir mevcut metrik adı/label'ı SİLİNMEZ, hiçbir mevcut metrik kaydı KALDIRILMAZ. Sadece ekleme.

---

## Task 1: Evaluator hit/miss outcome — `EvaluatorCompilation<T>`

**Files:**
- Modify: `modules/BBT.Workflow.Modules.Scripting/BBT/Workflow/Scripting/Evaluators/IEvaluator.cs`
- Modify: `modules/BBT.Workflow.Modules.Scripting/BBT/Workflow/Scripting/Evaluators/CSharpEvaluator.cs:114-166`
- Modify: `src/BBT.Workflow.Application/Scripting/ScriptEngine.cs:376-392` (sadece derlenebilirlik için `.Instance` erişimi; metrik değişikliği Task 3'te)
- Modify: `test/BBT.Workflow.Application.Tests/Scripting/SandboxedScriptingTests.cs:393-460` (test double'lar: `DelegatingEvaluator`, `FailOnceEvaluator`, `TokenCapturingEvaluator`)
- Test: `test/BBT.Workflow.Application.Tests/Scripting/CSharpEvaluatorCompilationOutcomeTests.cs` (yeni)

- [ ] **Step 1: Failing test yaz**

Yeni dosya `test/BBT.Workflow.Application.Tests/Scripting/CSharpEvaluatorCompilationOutcomeTests.cs`:

```csharp
using BBT.Workflow.Scripting.Evaluators;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting;

public class CSharpEvaluatorCompilationOutcomeTests
{
    public interface IOutcomeProbe
    {
        int Run();
    }

    private const string ProbeSource = """
        public class OutcomeProbe : BBT.Workflow.Scripting.CSharpEvaluatorCompilationOutcomeTests.IOutcomeProbe
        {
            public int Run() => 42;
        }
        """;

    private static Microsoft.CodeAnalysis.MetadataReference[] ProbeReferences() =>
    [
        Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(
            System.Reflection.Assembly.Load("System.Runtime").Location),
        Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(
            typeof(CSharpEvaluatorCompilationOutcomeTests).Assembly.Location)
    ];

    [Fact]
    public async Task FirstCompile_ReportsCompiledTrue_WithPositiveDuration()
    {
        var evaluator = new CSharpEvaluator();

        var outcome = await evaluator.CompileToInstanceAsync<IOutcomeProbe>(
            ProbeSource, extraReferences: ProbeReferences());

        outcome.Compiled.ShouldBeTrue();
        outcome.CompileDuration.ShouldBeGreaterThan(TimeSpan.Zero);
        outcome.Instance.Run().ShouldBe(42);
    }

    [Fact]
    public async Task SecondIdenticalCompile_ReportsCompiledFalse()
    {
        var evaluator = new CSharpEvaluator();
        _ = await evaluator.CompileToInstanceAsync<IOutcomeProbe>(
            ProbeSource, extraReferences: ProbeReferences());

        var second = await evaluator.CompileToInstanceAsync<IOutcomeProbe>(
            ProbeSource, extraReferences: ProbeReferences());

        second.Compiled.ShouldBeFalse();
        second.CompileDuration.ShouldBe(TimeSpan.Zero);
        second.Instance.Run().ShouldBe(42);
    }

    [Fact]
    public async Task ConcurrentIdenticalCompiles_ExactlyOneReportsCompiled()
    {
        var evaluator = new CSharpEvaluator();
        // Farklı kaynak: diğer testlerin cache'iyle çakışmasın diye nonce'lu.
        var source = ProbeSource.Replace("42", "43");

        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            evaluator.CompileToInstanceAsync<IOutcomeProbe>(
                source, extraReferences: ProbeReferences()))).ToArray();
        var outcomes = await Task.WhenAll(tasks);

        outcomes.Count(o => o.Compiled).ShouldBe(1);
        outcomes.ShouldAllBe(o => o.Instance.Run() == 43);
    }

    [Fact]
    public void CachedTypeCount_IsExposedOnInterface()
    {
        IEvaluator evaluator = new CSharpEvaluator();
        evaluator.CachedTypeCount.ShouldBe(0);
    }
}
```

> Not: script kaynağındaki interface tam adı (`BBT.Workflow.Scripting.CSharpEvaluatorCompilationOutcomeTests.IOutcomeProbe`) test sınıfının namespace'ine göre yazıldı; derleme hatası alırsan namespace'i test dosyasındakiyle eşle.

- [ ] **Step 2: Testin FAIL ettiğini doğrula**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~CSharpEvaluatorCompilationOutcomeTests" 2>&1 | tail -20`
Expected: derleme hatası — `'Task<IOutcomeProbe>' does not contain a definition for 'Compiled'` (ve `CachedTypeCount` IEvaluator'da yok).

- [ ] **Step 3: `IEvaluator` sözleşmesini değiştir**

`IEvaluator.cs` — `CompiledHelpers` record'unun altına ekle:

```csharp
/// <summary>
/// The result of a compile-or-fetch call: the instantiated script plus whether THIS call performed
/// the actual Roslyn compile (<see cref="Compiled"/> = cache miss) and how long that compile took.
/// Exactly one caller per cache key observes <c>Compiled == true</c>; every other caller —
/// including waiters that blocked on the same in-flight compile — observes a hit.
/// </summary>
/// <typeparam name="T">The compiled instance type.</typeparam>
/// <param name="Instance">The compiled and service-injected instance.</param>
/// <param name="Compiled">True only for the single call whose factory ran the Roslyn emit.</param>
/// <param name="CompileDuration">Wall time of the Roslyn emit; <see cref="TimeSpan.Zero"/> on hits.</param>
public sealed record EvaluatorCompilation<T>(T Instance, bool Compiled, TimeSpan CompileDuration);
```

`CompileToInstanceAsync<T>` dönüş tipini değiştir (L53) ve interface'e sayaç ekle:

```csharp
    Task<EvaluatorCompilation<T>> CompileToInstanceAsync<T>(
        string code,
        IScriptServices? services = null,
        IEnumerable<MetadataReference>? extraReferences = null,
        IEnumerable<string>? usingDirectives = null,
        CancellationToken cancellationToken = default,
        AssemblyLoadContext? loadContext = null,
        IReadOnlyList<string>? sandboxGrant = null);

    /// <summary>
    /// Gets the number of cached compiled script types (unique compilation identities).
    /// </summary>
    int CachedTypeCount { get; }
```

- [ ] **Step 4: `CSharpEvaluator.CompileToInstanceAsync` implementasyonu**

`CSharpEvaluator.cs:114-166` gövdesini şu şekilde değiştir (mevcut fast-path/`GetOrAdd`/eviction yorumları AYNEN korunur; sadece dönüş şekli ve closure bayrağı eklenir):

```csharp
    /// <inheritdoc />
    public Task<EvaluatorCompilation<T>> CompileToInstanceAsync<T>(
        string code,
        IScriptServices? services = null,
        IEnumerable<MetadataReference>? extraReferences = null,
        IEnumerable<string>? usingDirectives = null,
        CancellationToken cancellationToken = default,
        AssemblyLoadContext? loadContext = null,
        IReadOnlyList<string>? sandboxGrant = null)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Code cannot be null or empty", nameof(code));

        // The caller's token gates entry only. Once a compile starts it is shared by every caller
        // waiting on the same Lazy, so one abandoned request must not fail it — the same rule
        // ScriptHelperRegistry applies to helper-set builds.
        cancellationToken.ThrowIfCancellationRequested();

        // The load context is part of the compilation identity (a type compiled into helper set A's
        // context must never be served to a caller compiling against helper set B), so the scope is
        // derived from loadContext itself rather than taken as a separate parameter.
        var cacheScope = GetCacheScope(loadContext);
        var cacheKey = GenerateCacheKey(
            code, typeof(T), extraReferences, usingDirectives, sandboxGrant, cacheScope);

        // Fast path: an already-materialised entry is the overwhelmingly common case (every script in
        // every transition after the first). Check it before GetOrAdd so the closure below — which
        // captures code/cacheKey/extraReferences/usingDirectives/sandboxGrant/loadContext — is not
        // allocated on every cache hit. Mirrors ScriptHelperRegistry.GetOrBuildHelpers.
        if (_typeCache.TryGetValue(cacheKey, out var existing) && existing.IsValueCreated)
        {
            return Task.FromResult(new EvaluatorCompilation<T>(
                CreateAndInjectServices<T>(existing.Value.CompiledType, services), false, TimeSpan.Zero));
        }

        // The flag/duration live in this call's closure: the factory body runs at most once per cache
        // key (Lazy ExecutionAndPublication), and only the call whose lambda actually created the
        // stored Lazy has its locals written — so exactly one caller reports Compiled=true per emit,
        // no matter which thread triggers materialisation.
        var compiledHere = false;
        var compileDuration = TimeSpan.Zero;
        var lazy = _typeCache.GetOrAdd(cacheKey, _ => new Lazy<CompiledScript>(
            () =>
            {
                var compileTimer = Stopwatch.StartNew();
                var result = CompileAndLoad<T>(code, cacheKey, extraReferences, usingDirectives, sandboxGrant, loadContext);
                compileTimer.Stop();
                compiledHere = true;
                compileDuration = compileTimer.Elapsed;
                return result;
            },
            LazyThreadSafetyMode.ExecutionAndPublication));

        CompiledScript compiled;
        try
        {
            compiled = lazy.Value;
        }
        catch
        {
            // Lazy<T> caches the exception as well as the value, and this evaluator is a singleton:
            // without eviction one transient failure would be replayed for the rest of the process
            // lifetime. Remove only the entry we observed — never one another caller has published.
            _typeCache.TryRemove(new KeyValuePair<string, Lazy<CompiledScript>>(cacheKey, lazy));
            throw;
        }

        return Task.FromResult(new EvaluatorCompilation<T>(
            CreateAndInjectServices<T>(compiled.CompiledType, services), compiledHere, compileDuration));
    }
```

`using System.Diagnostics;` dosyada yoksa ekle. `CachedTypeCount` zaten var (`:97`) — interface'i karşılar, dokunma.

- [ ] **Step 5: Çağıranları derlenir hale getir**

1. `ScriptEngine.cs:376-392` — evaluator çağrısının sonucunu şimdilik minimal uyarla (Task 3'te tam metrik wiring gelecek):

```csharp
            var compilation = await _evaluator.CompileToInstanceAsync<T>(
                code,
                _scriptServices,
                mergedReferences,
                mergedUsings,
                cancellationToken,
                loadContext,
                MergeDefaultGrant(sandboxGrant));

            stopwatch.Stop();
            var durationSeconds = stopwatch.Elapsed.TotalSeconds;

            // Record successful script compilation
            workflowMetrics.RecordScriptExecution(scriptType, language, "success");
            workflowMetrics.RecordScriptCompilationDuration(scriptType, language, "success", durationSeconds);

            return compilation.Instance;
```

2. `SandboxedScriptingTests.cs:393-460` test double'ları: `DelegatingEvaluator` (abstract base) — `CompileToInstanceAsync` override'larını yeni dönüş tipine geçir (inner çağrıyı sarıp aynen döndür) ve `public int CachedTypeCount => Inner.CachedTypeCount;` ekle. `FailOnceEvaluator`/`TokenCapturingEvaluator` base'i takip eder — derleyici hataları yol gösterir.

Run: `dotnet build 2>&1 | grep -E "error|Warning.*CS" | head -20`
Expected: 0 error. Başka `CompileToInstanceAsync` çağıranı çıkarsa (beklenmiyor — tek production caller ScriptEngine): aynı `.Instance` uyarlamasını uygula ve commit mesajında not düş.

- [ ] **Step 6: Testlerin PASS ettiğini doğrula**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~CSharpEvaluatorCompilationOutcomeTests" 2>&1 | tail -5`
Expected: 4 passed.

Regresyon: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~SandboxedScriptingTests|FullyQualifiedName~CSharpEvaluatorConcurrencyTests|FullyQualifiedName~ScriptEngineTests" 2>&1 | tail -5`
Expected: hepsi geçer (pre-existing failure yoksa).

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "feat(scripting): evaluator reports compile-vs-hit outcome per call

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 2: Metrik yüzeyi — `script_compilations_total` + compile histogramına `cache` label'ı

**Files:**
- Modify: `src/BBT.Workflow.Domain/Monitoring/IWorkflowMetrics.cs:456-500` (Script Engine Metrics region)
- Modify: `src/BBT.Workflow.Infrastructure/Monitoring/WorkflowMetrics.cs:531-581` (kolektörler)
- Modify: `src/BBT.Workflow.Infrastructure/Monitoring/PrometheusWorkflowMetrics.cs:496-529`
- Test: `test/BBT.Workflow.Infrastructure.Tests/Monitoring/PrometheusWorkflowMetricsTests.cs` (mevcut dosyaya ekle — bugün Script* testi hiç yok)

- [ ] **Step 1: Failing test yaz**

`PrometheusWorkflowMetricsTests.cs`'e mevcut `Should.NotThrow` desenini izleyen testler ekle:

```csharp
    [Fact]
    public void RecordScriptCompilation_ShouldNotThrow()
    {
        var metrics = new PrometheusWorkflowMetrics();
        Should.NotThrow(() => metrics.RecordScriptCompilation("hit", "success"));
        Should.NotThrow(() => metrics.RecordScriptCompilation("miss", "success"));
    }

    [Fact]
    public void RecordScriptCompilationDuration_WithCacheLabel_ShouldNotThrow()
    {
        var metrics = new PrometheusWorkflowMetrics();
        Should.NotThrow(() => metrics.RecordScriptCompilationDuration(
            "compilation", "csharp", "success", 0.12, "miss"));
    }

    [Fact]
    public void RecordScriptExecutionDurationAndRuntimeError_ShouldNotThrow()
    {
        var metrics = new PrometheusWorkflowMetrics();
        Should.NotThrow(() => metrics.RecordScriptExecutionDuration("task-input", "csharp", "success", 0.05));
        Should.NotThrow(() => metrics.RecordScriptRuntimeError("condition", "csharp", "NullReferenceException"));
    }
```

- [ ] **Step 2: FAIL doğrula**

Run: `dotnet test test/BBT.Workflow.Infrastructure.Tests --filter "FullyQualifiedName~PrometheusWorkflowMetricsTests" 2>&1 | tail -10`
Expected: derleme hatası — `RecordScriptCompilation` tanımsız, `RecordScriptCompilationDuration` 5 argüman almıyor.

- [ ] **Step 3: Implementasyon**

1. `IWorkflowMetrics.cs` Script Engine Metrics region'ına ekle; `RecordScriptCompilationDuration`'a **default'lu** parametre ekle (mevcut 4-arg çağrılar derlenmeye devam eder):

```csharp
    /// <summary>
    /// Records a compile-or-fetch call against the script type cache.
    /// </summary>
    /// <param name="result">Cache outcome: "hit" or "miss"</param>
    /// <param name="status">Call status (success/failure category)</param>
    void RecordScriptCompilation(string result, string status);
```

ve mevcut imzayı şu hale getir:

```csharp
    void RecordScriptCompilationDuration(string scriptType, string language, string status, double durationSeconds, string cache = "unknown");
```

2. `WorkflowMetrics.cs` — script bölgesine yeni counter ekle ve `ScriptCompilationDuration` histogramının `LabelNames` dizisine `"cache"` ekle (bucket'lara dokunma; mevcut counter/histogram TANIMLARI aynen kalır). Yeni counter mevcut desenle:

```csharp
    public static readonly Counter ScriptCompilations = Metrics
        .CreateCounter(
            "script_compilations_total",
            "Compile-or-fetch calls against the script type cache, split by cache outcome",
            new CounterConfiguration
            {
                LabelNames = new[] { "result", "status" }
            });
```

(Dosyadaki mevcut counter config sözdizimini birebir izle — `CounterConfiguration` yerine farklı bir kalıp kullanılıyorsa onu kopyala.)

3. `PrometheusWorkflowMetrics.cs`:

```csharp
    public void RecordScriptCompilation(string result, string status)
    {
        WorkflowMetrics.ScriptCompilations
            .WithLabels(result, status)
            .Inc();
    }

    public void RecordScriptCompilationDuration(string scriptType, string language, string status, double durationSeconds, string cache = "unknown")
    {
        WorkflowMetrics.ScriptCompilationDuration
            .WithLabels(scriptType, language, status, cache)
            .Observe(durationSeconds);
    }
```

4. `IWorkflowMetrics`'in mock'landığı testler (ör. `ScriptEngineTests` Moq `Mock<IWorkflowMetrics>`) — interface'e eklenen üyeler Moq'ta otomatik stub'lanır, değişiklik gerekmez; NSubstitute için de aynı.

- [ ] **Step 4: PASS doğrula + tam derleme**

Run: `dotnet test test/BBT.Workflow.Infrastructure.Tests --filter "FullyQualifiedName~PrometheusWorkflowMetricsTests" 2>&1 | tail -5`
Expected: hepsi geçer (yeni 3 dahil).
Run: `dotnet build 2>&1 | grep -c error`
Expected: 0.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(metrics): script_compilations_total counter and cache label on compile histogram

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 3: ScriptEngine wiring — hit/miss kaydı + cache gauge

**Files:**
- Modify: `src/BBT.Workflow.Application/Scripting/ScriptEngine.cs:351-441` (`CompileCoreAsync`)
- Test: `test/BBT.Workflow.Application.Tests/Scripting/ScriptEngineTests.cs` (mevcut dosyaya ekle)

- [ ] **Step 1: Failing test yaz**

`ScriptEngineTests`'in mevcut kurulumunu (gerçek evaluator + `Mock<IWorkflowMetrics>`, `ApplicationTestBase` DI) izleyerek ekle — dosyadaki mevcut bir testin kurulum kalıbını kopyala, script kaynağını nonce'la:

```csharp
    [Fact]
    public async Task CompileTwice_RecordsMissThenHit_AndKeepsDeprecatedCounter()
    {
        // Arrange: dosyadaki mevcut ScriptEngine kurulum kalıbını kullan (gerçek IEvaluator,
        // Mock<IWorkflowMetrics> metricsMock). Script kaynağı bu teste özel olsun ki
        // process-genelindeki singleton evaluator cache'inden hit yemesin.
        var code = "public class HitMissProbe" + Guid.NewGuid().ToString("N") + " : BBT.Workflow.Scripting.ITransitionMapping { /* dosyadaki örnek gövdeyi kopyala */ }";

        await engine.CompileToInstanceAsync<ITransitionMapping>(code);
        await engine.CompileToInstanceAsync<ITransitionMapping>(code);

        // Yeni counter: 1 miss + 1 hit
        metricsMock.Verify(m => m.RecordScriptCompilation("miss", "success"), Times.Once);
        metricsMock.Verify(m => m.RecordScriptCompilation("hit", "success"), Times.Once);
        // Histogram: cache label'lı, iki kayıt
        metricsMock.Verify(m => m.RecordScriptCompilationDuration(
            "compilation", "csharp", "success", It.IsAny<double>(), "miss"), Times.Once);
        metricsMock.Verify(m => m.RecordScriptCompilationDuration(
            "compilation", "csharp", "success", It.IsAny<double>(), "hit"), Times.Once);
        // DEPRECATED metrik aynen akmaya devam ediyor (dashboard regresyon garantisi)
        metricsMock.Verify(m => m.RecordScriptExecution("compilation", "csharp", "success"), Times.Exactly(2));
        // Gauge beslendi
        metricsMock.Verify(m => m.SetCacheEntries("script-types", It.Is<int>(n => n >= 1)), Times.AtLeastOnce);
    }
```

> Kurulum kalıbı: `ScriptEngineTests.cs`'te `Mock<IWorkflowMetrics>` L52 civarında; engine'in nasıl inşa edildiğini oradan birebir al. `ITransitionMapping` örnek script gövdesi aynı dosyadaki mevcut testlerden kopyalanır. GUID nonce sınıf adında — cache izolasyonu için şart (evaluator singleton).

- [ ] **Step 2: FAIL doğrula**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~ScriptEngineTests.CompileTwice_RecordsMissThenHit" 2>&1 | tail -10`
Expected: FAIL — `RecordScriptCompilation` hiç çağrılmadı (Times.Once beklenirken 0).

- [ ] **Step 3: `CompileCoreAsync` implementasyonu**

Success yolunu şu hale getir (Task 1 Step 5'teki minimal uyarlamanın üzerine):

```csharp
            var compilation = await _evaluator.CompileToInstanceAsync<T>(
                code,
                _scriptServices,
                mergedReferences,
                mergedUsings,
                cancellationToken,
                loadContext,
                MergeDefaultGrant(sandboxGrant));

            stopwatch.Stop();
            var durationSeconds = stopwatch.Elapsed.TotalSeconds;
            var cache = compilation.Compiled ? "miss" : "hit";

            // DEPRECATED (vnext-meta/deprecations.json): script_executions_total keeps its historical
            // compile-path semantics until consumers migrate — do not remove or repurpose here.
            workflowMetrics.RecordScriptExecution(scriptType, language, "success");
            workflowMetrics.RecordScriptCompilation(cache, "success");
            workflowMetrics.RecordScriptCompilationDuration(scriptType, language, "success", durationSeconds, cache);
            workflowMetrics.SetCacheEntries("script-types", _evaluator.CachedTypeCount);

            return compilation.Instance;
```

> **Uygulama sapması (review bulgusu):** gauge beslemesi `if (compilation.Compiled)` guard'ı ile yalnız
> miss'te yapılır — type cache hiç evict etmediği için sayı yalnız miss'te değişir; hit'te
> `ConcurrentDictionary.Count`'un tüm-stripe kilidi hot path'ten çıkarıldı.

Dört catch bloğunda: mevcut kayıtlar AYNEN kalır; her birine bir satır ekle —
`workflowMetrics.RecordScriptCompilation("miss", "<mevcut status stringi>");` ve mevcut
`RecordScriptCompilationDuration(...)` çağrısına beşinci argüman `"miss"` ekle. (Hata üreten çağrı
tanımı gereği cache'ten dönmemiştir; `OperationCanceledException` girişte iptal olabilir ama miss
sayılması kabul edilir sadelik — yorum olarak not düş.)

- [ ] **Step 4: PASS doğrula + regresyon**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~ScriptEngineTests" 2>&1 | tail -5`
Expected: yeni test dahil hepsi geçer.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(metrics): ScriptEngine records cache hit/miss and script-types gauge

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 4: Huni noktaları — execution duration + runtime error

**Files:**
- Modify: `src/BBT.Workflow.Application/Tasks/Executors/Core/TaskExecutorBase.cs:24-137`
- Modify: tüm executor ctor'ları (Step 3'te grep ile listelenir — Script/Http/Soap/Dapr*/Cache/Trigger/Notification/FanOut/StateStore…)
- Modify: `src/BBT.Workflow.Application/Execution/Transitions/Services/TransitionDataMapper.cs:13-71`
- Modify: `src/BBT.Workflow.Application/Tasks/Evaluators/ScriptConditionEvaluator.cs:14-53`
- Modify: `src/BBT.Workflow.Application/Functions/FunctionAppService.cs:27-41 (ctor), 412-451 (BuildResponseAsync)`
- Test: `test/BBT.Workflow.Application.Tests/Tasks/TaskExecutorBaseMetricsTests.cs` (yeni), `test/BBT.Workflow.Application.Tests/Tasks/ScriptConditionEvaluatorMetricsTests.cs` (yeni)

**scriptType sözlüğü (spec §1.2):** `task-input`, `task-output`, `condition`, `transition-mapping`, `function`. Dil her yerde `"csharp"`.

**Semantik notu (spec §1.3):** TaskExecutorBase ölçümü FAZ süresidir (compile-lookup + script invoke + executor'ın küçük hazırlık kodu). ScriptTask'ın output handler'ı `InvokeAsync` fazında koştuğu için `task-output` altında görünmez — bilinen, dokümante sınır; Katman 1'in ortak mapping-helper refactor'ında kapanacak.

- [ ] **Step 1: Failing test yaz — TaskExecutorBase fazları**

Yeni `test/BBT.Workflow.Application.Tests/Tasks/TaskExecutorBaseMetricsTests.cs`:

```csharp
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Coordinator;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BBT.Workflow.Tasks;

public class TaskExecutorBaseMetricsTests
{
    // Mapping'i DOLU bir ScriptTask ile faz metriklerinin yazıldığını,
    // exception'da runtime-error + rethrow olduğunu pinler.
    private sealed class ProbeExecutor(Microsoft.Extensions.Logging.ILogger logger, IWorkflowMetrics metrics)
        : TaskExecutorBase<ScriptTask>(logger, metrics)
    {
        public bool ThrowOnPrepare { get; set; }
        public override TaskType TaskType => TaskType.Script;

        protected override Task<Result<ScriptResponse?>> PrepareInputAsync(
            ScriptTask task, TaskExecutorContext context, CancellationToken ct)
            => ThrowOnPrepare
                ? throw new InvalidOperationException("boom")
                : Task.FromResult(Result<ScriptResponse?>.Ok(null));

        protected override Task<Result<TaskInvocationResult>> InvokeAsync(
            ScriptTask task, TaskExecutorContext context, CancellationToken ct)
            => Task.FromResult(Result<TaskInvocationResult>.Ok(new TaskInvocationResult { IsSuccess = true }));
    }

    [Fact]
    public async Task Execute_WithMapping_RecordsInputAndOutputPhaseDurations()
    {
        var metrics = new Mock<IWorkflowMetrics>();
        var executor = new ProbeExecutor(NullLogger.Instance, metrics.Object);
        var context = TestTaskContexts.ScriptTaskWithMapping(); // aşağıdaki nota bak

        var result = await executor.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
        metrics.Verify(m => m.RecordScriptExecutionDuration(
            "task-input", "csharp", "success", It.IsAny<double>()), Times.Once);
        metrics.Verify(m => m.RecordScriptExecutionDuration(
            "task-output", "csharp", "success", It.IsAny<double>()), Times.Once);
    }

    [Fact]
    public async Task Execute_WithoutMapping_RecordsNothing()
    {
        var metrics = new Mock<IWorkflowMetrics>();
        var executor = new ProbeExecutor(NullLogger.Instance, metrics.Object);
        var context = TestTaskContexts.ScriptTaskWithoutMapping();

        _ = await executor.ExecuteAsync(context);

        metrics.Verify(m => m.RecordScriptExecutionDuration(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()), Times.Never);
    }

    [Fact]
    public async Task Execute_PrepareThrows_RecordsRuntimeErrorAndRethrows()
    {
        var metrics = new Mock<IWorkflowMetrics>();
        var executor = new ProbeExecutor(NullLogger.Instance, metrics.Object) { ThrowOnPrepare = true };
        var context = TestTaskContexts.ScriptTaskWithMapping();

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(context));

        metrics.Verify(m => m.RecordScriptRuntimeError(
            "task-input", "csharp", nameof(InvalidOperationException)), Times.Once);
    }
}
```

> `TestTaskContexts` yardımcı sınıfını aynı dosyada yaz: `TaskExecutorContext` + `ScriptTask` (Mapping dolu/boş) kur. `TaskExecutorContext`'in zorunlu üyeleri için mevcut testlerden örnek al: `grep -rn "new TaskExecutorContext" test/ | head -5` — bulduğun kurulum kalıbını kopyala. `ScriptTask.Mapping` set edilebilir değilse (private setter), mevcut testlerin task inşa yöntemini (JSON deserialize veya builder) aynen kullan.

- [ ] **Step 2: FAIL doğrula**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~TaskExecutorBaseMetricsTests" 2>&1 | tail -10`
Expected: derleme hatası — `TaskExecutorBase<ScriptTask>` 2 argümanlı ctor yok.

- [ ] **Step 3: TaskExecutorBase implementasyonu**

1. Sınıf imzası:

```csharp
public abstract class TaskExecutorBase<TTask>(ILogger logger, IWorkflowMetrics metrics) : ITaskExecutor
    where TTask : WorkflowTask
{
    protected readonly ILogger Logger = logger;
    protected readonly IWorkflowMetrics Metrics = metrics;

    private const string ScriptLanguage = "csharp";
```

`using BBT.Workflow.Monitoring;` ekle.

2. PrepareInput fazı (`:53-58` bölgesi) — mapping'li task'ta ölç, exception'da kaydet-rethrow:

```csharp
        // 2. PrepareInput (virtual - custom per executor)
        Result<ScriptResponse?> inputResult;
        var hasMapping = context.Task.Mapping is not null;
        using (TaskExecutionActivityHelper.StartActivity(TaskExecutionActivityHelper.OperationPrepareInput, taskKey, taskTypeStr))
        {
            var phaseStart = Stopwatch.GetTimestamp();
            try
            {
                inputResult = await PrepareInputAsync(task, context, cancellationToken);
            }
            catch (Exception ex) when (hasMapping)
            {
                Metrics.RecordScriptRuntimeError("task-input", ScriptLanguage, ex.GetType().Name);
                throw;
            }
            if (hasMapping)
            {
                Metrics.RecordScriptExecutionDuration(
                    "task-input", ScriptLanguage,
                    inputResult.IsSuccess ? "success" : "failure",
                    Stopwatch.GetElapsedTime(phaseStart).TotalSeconds);
            }
        }
```

3. ProcessOutput fazı (`:114-118` bölgesi) — birebir aynı desen, `"task-output"` ile (`outputResult` üzerinden).

> `context.Task.Mapping` derlenmezse (`WorkflowTask`'ta `Mapping` yoksa): `grep -n "Mapping" src/BBT.Workflow.Domain/Definitions/WorkflowTask.cs` ile gerçek üyeyi bul; mapping taşıyan ortak üye yoksa gate'i `context.Task is not null &&` yerine kaldırıp her fazı kaydet ve plan sapmasını commit mesajına yaz.

4. Executor ctor'larını güncelle. Listele: `grep -rln ": TaskExecutorBase<" src/ --include="*.cs"`. Her birinde primary ctor'a `IWorkflowMetrics metrics` parametresi ekle ve base'e geçir; örnek (ScriptTaskExecutor):

```csharp
public class ScriptTaskExecutor(
    ILogger<ScriptTaskExecutor> logger,
    IWorkflowMetrics metrics /*, ...mevcut diğer parametreler aynen... */)
    : TaskExecutorBase<ScriptTask>(logger, metrics)
```

Run: `dotnet build 2>&1 | grep -E "error CS" | head -30` — kalan her hata bir executor'dır; hepsini aynı desenle düzelt, 0 error olana kadar.

- [ ] **Step 4: Diğer üç huni**

1. **TransitionDataMapper** — ctor'a ekle + invoke ölçümü:

```csharp
public sealed class TransitionDataMapper(
    IScriptEngine scriptEngine,
    IScriptContextFactory scriptContextFactory,
    IInstanceRepository instanceRepository,
    IWorkflowMetrics workflowMetrics) : ITransitionDataMapper
```

`ExecuteMappingScriptAsync` içindeki TryAsync gövdesinde (`:58-62`):

```csharp
                var mappingInstance = await CompileMappingScriptAsync(transition, workflow.Scripts, ct);
                var scriptContext = await BuildScriptContextAsync(
                    payload, transition, workflow, instance, runtimeInfoProvider, headers, ct);

                var executeStart = Stopwatch.GetTimestamp();
                try
                {
                    var mapped = await mappingInstance.Handler(scriptContext);
                    workflowMetrics.RecordScriptExecutionDuration(
                        "transition-mapping", "csharp", "success",
                        Stopwatch.GetElapsedTime(executeStart).TotalSeconds);
                    return mapped;
                }
                catch (Exception ex)
                {
                    workflowMetrics.RecordScriptRuntimeError("transition-mapping", "csharp", ex.GetType().Name);
                    throw; // TryAsync mevcut hata eşlemesini (CreateMappingError) uygulamaya devam eder
                }
```

`using System.Diagnostics;` ve `using BBT.Workflow.Monitoring;` ekle.

2. **ScriptConditionEvaluator** — ctor'a `IWorkflowMetrics metrics` ekle (mevcut açık ctor'a üçüncü parametre + `_metrics` field); TryAsync gövdesi (`:41-47`):

```csharp
                var scriptRunner = await _scriptEngine.CompileToInstanceAsync<IConditionMapping>(
                    script,
                    flowScripts: context.Workflow?.Scripts,
                    cancellationToken: ct);

                var executeStart = Stopwatch.GetTimestamp();
                try
                {
                    var result = await scriptRunner.Handler(context);
                    _metrics.RecordScriptExecutionDuration(
                        "condition", "csharp", "success",
                        Stopwatch.GetElapsedTime(executeStart).TotalSeconds);
                    return result;
                }
                catch (Exception ex)
                {
                    _metrics.RecordScriptRuntimeError("condition", "csharp", ex.GetType().Name);
                    throw;
                }
```

3. **FunctionAppService** — primary ctor'a (`:27-41`) `IWorkflowMetrics workflowMetrics` parametresi ekle. `BuildResponseAsync` (`:421-423`):

```csharp
                var handler = await scriptEngine.CompileToInstanceAsync<IOutputHandler>(
                    function.Output, flowScripts: scriptContext.Workflow?.Scripts, cancellationToken: cancellationToken);
                var executeStart = Stopwatch.GetTimestamp();
                var scriptResponse = await handler.OutputHandler(scriptContext);
                workflowMetrics.RecordScriptExecutionDuration(
                    "function", "csharp", "success",
                    Stopwatch.GetElapsedTime(executeStart).TotalSeconds);
```

Mevcut catch bloğunun (`:438`) başına ekle:

```csharp
                workflowMetrics.RecordScriptRuntimeError("function", "csharp", ex.GetType().Name);
```

4. Yeni ScriptConditionEvaluator testi — `test/BBT.Workflow.Application.Tests/Tasks/ScriptConditionEvaluatorMetricsTests.cs`: `IScriptEngine`'i mock'la (`ResourceLockStepTests.cs:190` NSubstitute kalıbı), başarıda `RecordScriptExecutionDuration("condition", ...)`, handler fırlatınca `RecordScriptRuntimeError("condition", ...)` + `Result.IsSuccess == false` (TryAsync yutar, exception DIŞARI sızmaz — mevcut davranış korunur) doğrula.

- [ ] **Step 5: PASS + regresyon doğrula**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~TaskExecutorBaseMetricsTests|FullyQualifiedName~ScriptConditionEvaluatorMetricsTests" 2>&1 | tail -5`
Expected: hepsi geçer.
Run: `dotnet test test/BBT.Workflow.Application.Tests 2>&1 | tail -3` — failure İSİMLERİNİ master baseline'ıyla karşılaştır (yeni isim = senin regresyonun).

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat(metrics): wire script execution duration and runtime errors at funnel sites

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 5: Grafana dashboard — yeni paneller (vnext)

**Files:**
- Modify: `etc/docker/config/grafana/dashboards/workflow-metrics.json` (panel id 18 `:1392-1477` DOKUNMA; sonrasına ekle)

- [ ] **Step 1: Mevcut max panel id'yi bul**

Run: `python3 -c "import json; d=json.load(open('etc/docker/config/grafana/dashboards/workflow-metrics.json')); print(max(p['id'] for p in d['panels']))"`
Expected: bir sayı (örn. 30 civarı) — yeni paneller `max+1`, `max+2`.

- [ ] **Step 2: İki panel ekle**

Panel id 18'in JSON bloğunu şablon al (aynı `datasource`, `fieldConfig`, `type: "timeseries"`); `panels` dizisine, id 18 objesinin hemen ARKASINA iki obje ekle:

1. **Script Cache Hit Ratio** — `"id": <max+1>`, `"gridPos": {"h": 6, "w": 6, "x": 0, "y": 38}`, targets:
   `"expr": "sum(rate(script_compilations_total{result=\"hit\"}[5m])) / sum(rate(script_compilations_total[5m]))"`,
   `"legendFormat": "hit ratio"`, `fieldConfig.defaults.unit`: `"percentunit"`, `max: 1`.
2. **Script Execution Duration p95 (s)** — `"id": <max+2>`, `"gridPos": {"h": 6, "w": 6, "x": 6, "y": 38}`, targets:
   `"expr": "histogram_quantile(0.95, sum(rate(script_execution_duration_seconds_bucket[5m])) by (le, script_type))"`,
   `"legendFormat": "{{script_type}}"`, unit `"s"`.

Diğer panellerin `gridPos.y` değerleri kaydırılmaz (Grafana çakışmayı kendisi çözer); id 18 ve tüm mevcut paneller byte-for-byte aynı kalır.

- [ ] **Step 3: JSON geçerliliğini doğrula**

Run: `python3 -c "import json; d=json.load(open('etc/docker/config/grafana/dashboards/workflow-metrics.json')); ids=[p['id'] for p in d['panels']]; assert len(ids)==len(set(ids)), 'duplicate id'; print('OK', len(ids), 'panels')"`
Expected: `OK <n> panels`.

- [ ] **Step 4: Commit**

```bash
git add etc/docker/config/grafana/dashboards/workflow-metrics.json && git commit -m "feat(observability): script cache hit-ratio and execution p95 dashboard panels

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

> Helm-charts karşılığı bu planın DIŞINDA (spec §4 adım 3): vnext release'inden sonra `grafana-dashboard-config.yaml`'a aynı iki panel eklenecek; acele yok — eski panel kırılmıyor.

---

## Task 6: vnext-meta — deprecation + migration kayıtları

**Files:**
- Modify: `vnext-meta/deprecations.json` (tek entry'li `{"items": [...]}` yapısı)
- Modify: `vnext-meta/migrations.json` (`{"items": [...]}`)

- [ ] **Step 1: deprecations.json'a entry ekle**

`items` dizisine (mevcut entry'nin şeklini birebir izleyerek):

```json
    {
      "id": "metric-script-executions-total-compile-semantics",
      "type": "metric",
      "component": "runtime",
      "path": "prometheus.script_executions_total",
      "deprecatedSince": "0.0.81",
      "removedAt": null,
      "replacement": "script_compilations_total (compile-or-fetch calls, result=hit|miss) and script_execution_duration_seconds_count (true script executions by script_type)",
      "severity": "warning",
      "message": "script_executions_total has always been incremented on the COMPILE path (cache hits included), not on script execution. It keeps emitting unchanged for backward compatibility; migrate dashboards/alerts to script_compilations_total and script_execution_duration_seconds, then this metric will be removed in a future release."
    }
```

> `deprecatedSince`: `common.props` `<Version>` şu an 0.0.80; bu geliştirmenin çıkacağı sürüm 0.0.81 varsayıldı — commit anında `common.props`'taki güncel değerin BİR SONRAKİ minor'ı neyse onu yaz.

- [ ] **Step 2: migrations.json'a entry ekle**

```json
    {
      "id": "script-metrics-hit-miss-and-execution",
      "type": "behavior",
      "component": "runtime",
      "path": "prometheus.script_*",
      "since": "0.0.81",
      "severity": "info",
      "title": "Script metrics: cache hit/miss split and true execution metrics added",
      "description": "New metrics: script_compilations_total{result=hit|miss,status}; script_execution_duration_seconds{script_type,language,status} and script_runtime_errors_total{script_type,language,error_type} are now recorded at execution funnels (task-input, task-output, condition, transition-mapping, function); script_compilation_duration_seconds gained a 'cache' label (existing aggregate queries unaffected); workflow_cache_entries gained cache_name='script-types'. script_executions_total is deprecated but unchanged.",
      "action": "Point compile-rate panels at rate(script_compilations_total[5m]); read clean compile latency with {cache=\"miss\"}; use script_execution_duration_seconds_count for true execution counts. No urgent action: old metric still emits."
    }
```

- [ ] **Step 3: Doğrula**

Skill `vnext-meta-validator`'ı çağır (meta JSON değişikliği sonrası zorunlu tetik). Validator `type: "metric"` enum'unu reddederse mevcut şemadaki en yakın tipe (`"field"` yerine `"behavior"`) çevir ve commit mesajında not düş.

- [ ] **Step 4: Commit**

```bash
git add vnext-meta/ && git commit -m "docs(vnext-meta): deprecate compile-path script_executions_total, document new script metrics

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 7: BenchmarkDotNet projesi

**Files:**
- Create: `test/BBT.Workflow.Benchmarks/BBT.Workflow.Benchmarks.csproj`
- Create: `test/BBT.Workflow.Benchmarks/Program.cs`
- Create: `test/BBT.Workflow.Benchmarks/CompileHitPathBenchmarks.cs`
- Create: `test/BBT.Workflow.Benchmarks/InstanceDataAccessBenchmarks.cs`
- Create: `test/BBT.Workflow.Benchmarks/ParallelBranchBenchmarks.cs`
- Create: `test/BBT.Workflow.Benchmarks/AppendPathBenchmarks.cs`
- Create: `test/BBT.Workflow.Benchmarks/AuditSerializeBenchmarks.cs`
- Create: `test/BBT.Workflow.Benchmarks/PayloadFactory.cs`
- Modify: `vnext.sln` + `BBT.Workflow.slnx` (proje ekleme)

**Spec sapmaları (bilinçli, baseline md'ye not düşülecek):** (1) CompileHitPath, engine yerine `CSharpEvaluator`'ı doğrudan ölçer — engine katmanının LINQ maliyeti makro lab'de görünür; helper'lı varyant mikroda yok (registry kurulumu DI ister), makro lab kapsar. (2) InstanceDataAccess, `Instance` aggregate'i yerine aynı sıcak maliyeti taşıyan `JsonData.JsonElement` + `ToDynamic()` primitiflerini ölçer. (3) AppendPath, `InstanceDataWriteService` yerine `JsonData.Merge` + `NormalizedJson` primitiflerini ölçer.

- [ ] **Step 1: csproj + Program**

`test/BBT.Workflow.Benchmarks/BBT.Workflow.Benchmarks.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="..\..\common.props" />
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <RootNamespace>BBT.Workflow.Benchmarks</RootNamespace>
    <IsPackable>false</IsPackable>
    <ServerGarbageCollection>true</ServerGarbageCollection>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="BenchmarkDotNet" Version="0.14.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\BBT.Workflow.Application\BBT.Workflow.Application.csproj" />
    <ProjectReference Include="..\..\modules\BBT.Workflow.Modules.Scripting\BBT.Workflow.Modules.Scripting.csproj" />
  </ItemGroup>
</Project>
```

> Modules.Scripting csproj'unun gerçek dosya adını doğrula: `ls modules/BBT.Workflow.Modules.Scripting/*.csproj`. `dotnet add package BenchmarkDotNet` daha yeni bir sürüm önerirse onu kullan.

`Program.cs`:

```csharp
using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

public partial class Program;
```

Projeyi solution'lara ekle:

```bash
dotnet sln vnext.sln add test/BBT.Workflow.Benchmarks/BBT.Workflow.Benchmarks.csproj
dotnet sln BBT.Workflow.slnx add test/BBT.Workflow.Benchmarks/BBT.Workflow.Benchmarks.csproj
```

- [ ] **Step 2: PayloadFactory (deterministik sentetik JSON)**

`PayloadFactory.cs`:

```csharp
using System.Text;

namespace BBT.Workflow.Benchmarks;

/// <summary>Deterministic nested-JSON payloads of a requested approximate size.</summary>
public static class PayloadFactory
{
    /// <summary>~<paramref name="approxKb"/> KB'lik, iç içe objeler/diziler içeren JSON üretir.</summary>
    public static string Json(int approxKb)
    {
        var sb = new StringBuilder(approxKb * 1024 + 256);
        sb.Append("{\"customer\":{\"name\":\"Benchmark User\",\"segment\":\"retail\"},\"items\":[");
        var i = 0;
        while (sb.Length < approxKb * 1024)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(i)
              .Append(",\"sku\":\"SKU-").Append(i.ToString("D8"))
              .Append("\",\"amount\":").Append((i * 37 % 10000) / 100.0)
              .Append(",\"tags\":[\"a\",\"b\",\"c\"],\"meta\":{\"channel\":\"web\",\"retry\":false}}");
            i++;
        }
        sb.Append("]}");
        return sb.ToString();
    }
}
```

- [ ] **Step 3: CompileHitPath suite**

`CompileHitPathBenchmarks.cs`:

```csharp
using System.Reflection;
using BenchmarkDotNet.Attributes;
using BBT.Workflow.Scripting.Evaluators;
using Microsoft.CodeAnalysis;

namespace BBT.Workflow.Benchmarks;

public interface IBenchScript
{
    int Run();
}

/// <summary>
/// Sıcak cache'te CompileToInstanceAsync'in çağrı başına sabit bedeli:
/// GenerateCacheKey (tam-kaynak SHA256 + OrderBy'lar) + fast path + Activator.CreateInstance.
/// Analiz A1/A3/A4 — ai-docs/script-perf-analysis-2026-08-23.md.
/// </summary>
[MemoryDiagnoser]
public class CompileHitPathBenchmarks
{
    private CSharpEvaluator _evaluator = null!;
    private string _source = null!;
    private MetadataReference[] _references = null!;

    [Params(1, 4, 16)]
    public int SourceKb { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _evaluator = new CSharpEvaluator();
        _references =
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location),
            MetadataReference.CreateFromFile(typeof(IBenchScript).Assembly.Location)
        ];
        // Kaynağı istenen boyuta yorum satırlarıyla şişir: cache-key üretimi tam metni tarar,
        // dolayısıyla boyut ölçümün ana parametresidir.
        var padding = new string('/', 80) + "\n";
        var pad = string.Concat(Enumerable.Repeat(padding, SourceKb * 1024 / padding.Length + 1));
        _source = $$"""
            {{pad}}
            public class HitPathProbe : BBT.Workflow.Benchmarks.IBenchScript
            {
                public int Run() => 42;
            }
            """;
        // Isıt: ilk (ve tek) gerçek derleme burada olur; ölçülen iterasyonlar hep hit'tir.
        _ = _evaluator.CompileToInstanceAsync<IBenchScript>(_source, extraReferences: _references)
            .GetAwaiter().GetResult();
    }

    [Benchmark]
    public IBenchScript WarmCompileToInstance()
        => _evaluator.CompileToInstanceAsync<IBenchScript>(_source, extraReferences: _references)
            .GetAwaiter().GetResult().Instance;
}
```

- [ ] **Step 4: InstanceDataAccess suite**

`InstanceDataAccessBenchmarks.cs`:

```csharp
using BenchmarkDotNet.Attributes;

namespace BBT.Workflow.Benchmarks;

/// <summary>
/// Instance data okuma primitifleri: JsonData.JsonElement (her erişimde tam parse, B1)
/// ve ToDynamic (tam ExpandoObject ağacı inşası, B2/B3).
/// </summary>
[MemoryDiagnoser]
public class InstanceDataAccessBenchmarks
{
    private BBT.Workflow.JsonData _data = null!;

    [Params(10, 50, 200)]
    public int DocKb { get; set; }

    [GlobalSetup]
    public void Setup() => _data = new BBT.Workflow.JsonData(PayloadFactory.Json(DocKb));

    [Benchmark]
    public System.Text.Json.JsonElement ParseJsonElement() => _data.JsonElement;

    [Benchmark]
    public object? ParseAndBuildExpando() => _data.JsonElement.ToDynamic();
}
```

> `ToDynamic` extension'ının namespace'ini doğrula: `grep -rn "static.*ToDynamic" src/BBT.Workflow.Domain/ --include="*.cs"` — gerekirse `using` ekle.

- [ ] **Step 5: ParallelBranch suite**

`ParallelBranchBenchmarks.cs`:

```csharp
using BenchmarkDotNet.Attributes;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using Microsoft.Extensions.Logging.Abstractions;

namespace BBT.Workflow.Benchmarks;

/// <summary>
/// ScriptContext.CreateParallelBranch maliyeti (B6): Body + TaskResponse'ların JSON round-trip
/// derin kopyası. FanOut item başına bir kez ödenir ve dal merge edilmeden atılır.
/// </summary>
[MemoryDiagnoser]
public class ParallelBranchBenchmarks
{
    private ScriptContext _context = null!;

    [Params(10, 50)]
    public int BodyKb { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _context = new ScriptContext(NullLogger<ScriptContext>.Instance);
        // Body'yi executor'ların yaptığı yoldan doldur: SetStandardResponse → MergeToBody.
        var payload = System.Text.Json.JsonSerializer.Deserialize<System.Dynamic.ExpandoObject>(
            PayloadFactory.Json(BodyKb));
        _context.SetStandardResponse(new StandardTaskResponse { IsSuccess = true, Data = payload }, "seedTask");
    }

    [Benchmark]
    public ScriptContext CreateBranch() => _context.CreateParallelBranch();
}
```

> `SetStandardResponse` imzası `Models.cs`'te (`TaskExecutorBase.cs:308` çağrısındaki gibi `(StandardTaskResponse, string)`); `StandardTaskResponse` namespace'i derleyicinin gösterdiği yerden `using`'e eklenir. `ScriptContext` ctor'u `(ILogger<ScriptContext>)` — `Models.cs:176`.

- [ ] **Step 6: AppendPath + AuditSerialize suite'leri**

`AppendPathBenchmarks.cs`:

```csharp
using BenchmarkDotNet.Attributes;

namespace BBT.Workflow.Benchmarks;

/// <summary>
/// Instance data append primitifleri (B9): Merge (2 parse + expando merge + serialize) ve
/// NormalizedJson (node başına SerializeToElement ile kanonik yeniden inşa).
/// Transition başına task sayısı kadar, büyüyen doküman üzerinde koşar → O(n²) profili.
/// </summary>
[MemoryDiagnoser]
public class AppendPathBenchmarks
{
    private BBT.Workflow.JsonData _accumulated = null!;
    private BBT.Workflow.JsonData _delta = null!;

    [Params(10, 50, 200)]
    public int DocKb { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _accumulated = new BBT.Workflow.JsonData(PayloadFactory.Json(DocKb));
        _delta = new BBT.Workflow.JsonData("""{"taskResult":{"status":"ok","score":87,"note":"appended"}}""");
    }

    [Benchmark]
    public BBT.Workflow.JsonData Merge() => _accumulated.Merge(_delta);

    [Benchmark]
    public string NormalizeFresh() => new BBT.Workflow.JsonData(_accumulated.Json).NormalizedJson;
}
```

`AuditSerializeBenchmarks.cs`:

```csharp
using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace BBT.Workflow.Benchmarks;

/// <summary>
/// Task audit serileştirmeleri (B8): RawInvocationResultJson benzeri payload'ın
/// JsonSerializerConstants.JsonOptions (IgnoreCycles) ile tam serialize maliyeti.
/// </summary>
[MemoryDiagnoser]
public class AuditSerializeBenchmarks
{
    private object _payload = null!;

    [Params(50)]
    public int ResponseKb { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var data = JsonSerializer.Deserialize<System.Dynamic.ExpandoObject>(PayloadFactory.Json(ResponseKb));
        _payload = new { IsSuccess = true, StatusCode = 200, Data = data };
    }

    [Benchmark]
    public string SerializeAudit()
        => JsonSerializer.Serialize(_payload, BBT.Workflow.JsonSerializerConstants.JsonOptions);
}
```

- [ ] **Step 7: Derle ve smoke-run**

```bash
dotnet build test/BBT.Workflow.Benchmarks -c Release
```
Expected: 0 error. (Tip/namespace hataları çıkarsa: her suite dosyasındaki grep notlarını uygula — API'ler bu plandaki analiz oturumunda doğrulandı ama namespace'ler derleyiciyle netleşir.)

```bash
dotnet run -c Release --project test/BBT.Workflow.Benchmarks -- --filter "*ParseJsonElement*" --job Dry
```
Expected: benchmark koşar, tablo basar (Dry = 1 iterasyon, hızlı doğrulama).

- [ ] **Step 8: Commit**

```bash
git add -A && git commit -m "feat(benchmarks): BenchmarkDotNet project with script hot-path baseline suites

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 8: Mikro baseline koşumu ve kaydı

**Files:**
- Create: `test/BBT.Workflow.Benchmarks/baselines/<bugünün-tarihi>-master.md`

- [ ] **Step 1: Tam koşum**

```bash
dotnet run -c Release --project test/BBT.Workflow.Benchmarks -- --filter "*" --exporters markdown 2>&1 | tail -40
```
Expected: 5 suite'in tümü tamamlanır; `BenchmarkDotNet.Artifacts/results/*.md` üretilir. (Toplam ~10-20 dk; makinede başka ağır iş koşmasın.)

- [ ] **Step 2: Baseline dosyasını derle**

`test/BBT.Workflow.Benchmarks/baselines/` klasörünü oluştur; tüm `BenchmarkDotNet.Artifacts/results/*-report-github.md` içeriklerini tek dosyada birleştir. Dosya başına şu başlığı koy:

```markdown
# Script Perf Micro Baseline — <tarih>, branch feature/script-perf-katman0 (Katman 0, optimizasyon öncesi)

Makine: (BenchmarkDotNet çıktısındaki host satırını yapıştır)
Spec: docs/superpowers/specs/2026-08-23-script-perf-katman0-design.md
Sapmalar: CompileHitPath evaluator-direkt (engine LINQ katmanı hariç, helper'sız);
InstanceDataAccess/AppendPath primitif seviyesinde (aggregate/servis katmanı hariç).
Katman 1-3 kıyaslamaları bu tabloya karşı yapılır — suite parametreleri değiştirilmez.
```

- [ ] **Step 3: Commit**

```bash
git add test/BBT.Workflow.Benchmarks/baselines/ && git commit -m "docs(benchmarks): micro baseline before optimization layers

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 9: Kapanış — tam doğrulama

- [ ] **Step 1: Tam build + hedefli test süiti**

```bash
dotnet build 2>&1 | grep -cE "error"
```
Expected: 0.

```bash
dotnet test test/BBT.Workflow.Application.Tests 2>&1 | tail -3
dotnet test test/BBT.Workflow.Infrastructure.Tests 2>&1 | tail -3
dotnet test test/BBT.Workflow.Domain.Tests 2>&1 | tail -3
```
Expected: failure'lar YALNIZ master baseline'ındaki isimler (memory: ~191 pre-existing, AmbientServiceProvider sızması). Şüphede: master worktree'de aynı filtreyle koşup İSİM diff'i al — boş olmalı.

- [ ] **Step 2: Spec başarı kriterlerini işaretle**

Spec §5 checklist'ini gözden geçir: hit/miss ✓ (Task 3), execution duration ✓ (Task 4), benchmark + baseline ✓ (Task 7-8), davranış korunumu ✓ (deprecated metrik testi Task 3, funnel exception testleri Task 4). Makro baseline (spec §3) bu planın DIŞINDA — Plan 2 (vnext-example script-perf-lab).

- [ ] **Step 3: Superpowers finishing akışı**

`superpowers:finishing-a-development-branch` skill'ini çağır (merge/PR kararı kullanıcıya sunulur; PR istenirse `create-github-pr` skill'i kullanılır).
