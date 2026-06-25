# Health Check Optimization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Kaldır DB health check'i Inbox/Outbox worker'larından; Orchestration'a 60 sn TTL'li `CachedHealthCheck` singleton ekle.

**Architecture:** `AddAppHealthChecks()` (HttpApi.Shared) yalnızca `"self"` check içerecek şekilde daraltılır. Orchestration, kendi DI metodunda `CachedHealthCheck` singleton'ı register eder ve `"database"` check'i buna bağlar — Inbox/Outbox hiç dokunulmaz ve otomatik temizlenir.

**Tech Stack:** .NET 10 / ASP.NET Core Health Checks (`Microsoft.Extensions.Diagnostics.HealthChecks`), `AspNetCore.HealthChecks.NpgSql`, `prometheus-net.AspNetCore.HealthChecks`, xUnit, Shouldly.

---

## Dosya Haritası

| İşlem | Dosya |
|-------|-------|
| **Oluştur** | `src/BBT.Workflow.HttpApi.Shared/HealthChecks/HealthCheckCacheOptions.cs` |
| **Oluştur** | `src/BBT.Workflow.HttpApi.Shared/HealthChecks/CachedHealthCheck.cs` |
| **Oluştur** | `test/BBT.Workflow.Application.Tests/HealthChecks/CachedHealthCheckTests.cs` |
| **Oluştur** | `test/BBT.Workflow.Application.Tests/HealthChecks/AppHealthChecksRegistrationTests.cs` |
| **Değiştir** | `src/BBT.Workflow.HttpApi.Shared/Microsoft/Extensions/DependencyInjection/HealthChecksServiceCollectionExtensions.cs` |
| **Değiştir** | `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Microsoft/Extensions/DependencyInjection/OrchestrationApiServiceCollectionExtensions.cs` |

---

### Task 1: `HealthCheckCacheOptions` sınıfını oluştur

**Files:**
- Create: `src/BBT.Workflow.HttpApi.Shared/HealthChecks/HealthCheckCacheOptions.cs`

- [ ] **Step 1: Dosyayı oluştur**

```csharp
namespace BBT.Workflow.HttpApi.Shared.HealthChecks;

/// <summary>Database health check caching configuration.</summary>
public sealed class HealthCheckCacheOptions
{
    public const string SectionName = "HealthChecks:Database";

    /// <summary>How long (in seconds) the last DB probe result is reused. Default: 60.</summary>
    public int CacheTtlSeconds { get; set; } = 60;

    /// <summary>Convenience accessor returning CacheTtlSeconds as a TimeSpan.</summary>
    public TimeSpan Ttl => TimeSpan.FromSeconds(CacheTtlSeconds);
}
```

- [ ] **Step 2: Build kontrolü**

```bash
dotnet build src/BBT.Workflow.HttpApi.Shared/BBT.Workflow.HttpApi.Shared.csproj
```
Beklenen: **Build succeeded**

- [ ] **Step 3: Commit**

```bash
git add src/BBT.Workflow.HttpApi.Shared/HealthChecks/HealthCheckCacheOptions.cs
git commit -m "feat(health): add HealthCheckCacheOptions with 60s default TTL"
```

---

### Task 2: `CachedHealthCheck` — önce failing testler

**Files:**
- Create: `test/BBT.Workflow.Application.Tests/HealthChecks/CachedHealthCheckTests.cs`

- [ ] **Step 1: Test dosyasını oluştur**

```csharp
using BBT.Workflow.HttpApi.Shared.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;

namespace BBT.Workflow.HealthChecks;

public sealed class CachedHealthCheckTests
{
    private static HealthCheckContext MakeContext() => new()
    {
        Registration = new HealthCheckRegistration(
            "test",
            _ => Task.FromResult(HealthCheckResult.Healthy()),
            HealthStatus.Unhealthy,
            null)
    };

    // FakeTimeProvider: 1 tick = 1 saniye (TimestampFrequency = 1)
    private sealed class FakeTimeProvider : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, (long)by.TotalSeconds);
    }

    private sealed class StubHealthCheck(Func<HealthCheckResult> factory) : IHealthCheck
    {
        public int CallCount { get; private set; }

        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(factory());
        }
    }

    [Fact]
    public async Task FirstCall_HitsInnerCheck()
    {
        var inner = new StubHealthCheck(() => HealthCheckResult.Healthy());
        var sut = new CachedHealthCheck(inner, TimeSpan.FromSeconds(60), new FakeTimeProvider());

        await sut.CheckHealthAsync(MakeContext());

        inner.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task SecondCallWithinTtl_ReturnsCachedResult_NoExtraDbHit()
    {
        var inner = new StubHealthCheck(() => HealthCheckResult.Healthy());
        var fake = new FakeTimeProvider();
        var sut = new CachedHealthCheck(inner, TimeSpan.FromSeconds(60), fake);

        await sut.CheckHealthAsync(MakeContext());
        fake.Advance(TimeSpan.FromSeconds(59));
        await sut.CheckHealthAsync(MakeContext());

        inner.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task CallAfterTtlExpires_HitsInnerCheckAgain()
    {
        var inner = new StubHealthCheck(() => HealthCheckResult.Healthy());
        var fake = new FakeTimeProvider();
        var sut = new CachedHealthCheck(inner, TimeSpan.FromSeconds(60), fake);

        await sut.CheckHealthAsync(MakeContext()); // t=0, hit inner
        fake.Advance(TimeSpan.FromSeconds(60));     // t=60, TTL expired
        await sut.CheckHealthAsync(MakeContext()); // hit inner again

        inner.CallCount.ShouldBe(2);
    }

    [Fact]
    public async Task CachesUnhealthyResult_UntilTtlExpires()
    {
        var inner = new StubHealthCheck(() => HealthCheckResult.Unhealthy("db down"));
        var fake = new FakeTimeProvider();
        var sut = new CachedHealthCheck(inner, TimeSpan.FromSeconds(60), fake);

        var first = await sut.CheckHealthAsync(MakeContext());
        fake.Advance(TimeSpan.FromSeconds(30));
        var second = await sut.CheckHealthAsync(MakeContext());

        first.Status.ShouldBe(HealthStatus.Unhealthy);
        second.Status.ShouldBe(HealthStatus.Unhealthy);
        inner.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task ConcurrentCalls_HitInnerOnlyOnce()
    {
        var inner = new StubHealthCheck(() =>
        {
            Thread.Sleep(10); // simulate slow DB
            return HealthCheckResult.Healthy();
        });
        var sut = new CachedHealthCheck(inner, TimeSpan.FromSeconds(60), new FakeTimeProvider());
        var context = MakeContext();

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => sut.CheckHealthAsync(context))
            .ToArray();
        await Task.WhenAll(tasks);

        inner.CallCount.ShouldBe(1);
    }
}
```

- [ ] **Step 2: Testlerin fail ettiğini doğrula** (`CachedHealthCheck` henüz yok)

```bash
dotnet test test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj \
  --filter "FullyQualifiedName~CachedHealthCheckTests" --no-build 2>&1 | tail -20
```
Beklenen: **build error** (tip bulunamadı)

- [ ] **Step 3: Commit (failing tests — TDD checkpoint)**

```bash
git add test/BBT.Workflow.Application.Tests/HealthChecks/CachedHealthCheckTests.cs
git commit -m "test(health): add failing CachedHealthCheck TTL and concurrency tests"
```

---

### Task 3: `CachedHealthCheck` implementasyonu

**Files:**
- Create: `src/BBT.Workflow.HttpApi.Shared/HealthChecks/CachedHealthCheck.cs`

- [ ] **Step 1: Sınıfı oluştur**

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BBT.Workflow.HttpApi.Shared.HealthChecks;

/// <summary>
/// Throttles a wrapped <see cref="IHealthCheck"/> by caching its result for a configurable TTL.
/// Must be registered as a singleton so the TTL state persists across probes.
/// A SemaphoreSlim prevents thundering-herd: only one live DB query runs at a time.
/// </summary>
public sealed class CachedHealthCheck : IHealthCheck, IDisposable
{
    private readonly IHealthCheck _inner;
    private readonly long _ttlTicks;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HealthCheckResult? _cached;
    private long _expiresAt = long.MinValue;

    public CachedHealthCheck(IHealthCheck inner, TimeSpan ttl, TimeProvider timeProvider)
    {
        _inner = inner;
        _timeProvider = timeProvider;
        _ttlTicks = (long)(ttl.TotalSeconds * timeProvider.TimestampFrequency);
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // Fast path — no lock needed if result is still fresh
        if (_cached.HasValue && _timeProvider.GetTimestamp() < _expiresAt)
            return _cached.Value;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Re-check inside lock (another thread may have refreshed while we waited)
            if (_cached.HasValue && _timeProvider.GetTimestamp() < _expiresAt)
                return _cached.Value;

            _cached = await _inner.CheckHealthAsync(context, cancellationToken);
            _expiresAt = _timeProvider.GetTimestamp() + _ttlTicks;
            return _cached.Value;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
```

- [ ] **Step 2: Testleri çalıştır — geçmeli**

```bash
dotnet test test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj \
  --filter "FullyQualifiedName~CachedHealthCheckTests"
```
Beklenen: **5 passed**

- [ ] **Step 3: Commit**

```bash
git add src/BBT.Workflow.HttpApi.Shared/HealthChecks/CachedHealthCheck.cs
git commit -m "feat(health): implement CachedHealthCheck with SemaphoreSlim + TTL"
```

---

### Task 4: `AddAppHealthChecks()`'den DB check'i kaldır

**Files:**
- Modify: `src/BBT.Workflow.HttpApi.Shared/Microsoft/Extensions/DependencyInjection/HealthChecksServiceCollectionExtensions.cs`
- Create: `test/BBT.Workflow.Application.Tests/HealthChecks/AppHealthChecksRegistrationTests.cs`

- [ ] **Step 1: Önce failing testi yaz**

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Shouldly;

namespace BBT.Workflow.HealthChecks;

public sealed class AppHealthChecksRegistrationTests
{
    [Fact]
    public void AddAppHealthChecks_RegistersSelfCheckOnly_NoDatabaseCheck()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddAppHealthChecks();

        var sp = services.BuildServiceProvider();
        var registrations = sp.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;

        registrations.ShouldNotContain(r => r.Name == "database",
            "DB health check must not be registered in the shared extension");
        registrations.ShouldContain(r => r.Name == "self" && r.Tags.Contains("live"));
    }
}
```

- [ ] **Step 2: Testi çalıştır — fail etmeli** (`"database"` check hâlâ kayıtlı)

```bash
dotnet test test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj \
  --filter "FullyQualifiedName~AppHealthChecksRegistrationTests"
```
Beklenen: **FAIL**

- [ ] **Step 3: `HealthChecksServiceCollectionExtensions.cs`'i güncelle** — `AddNpgSql` satırını kaldır, `GetConfiguration()` çağrısına da artık gerek yok

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Prometheus;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extensions for configuring health checks in Workflow API applications.
/// Registers only the lightweight "self" liveness check.
/// Database readiness check is added separately by hosts that own a DB connection (Orchestration).
/// </summary>
public static class HealthChecksServiceCollectionExtensions
{
    /// <summary>
    /// Adds the base health checks shared by all Workflow application hosts:
    /// a "self" liveness check (tagged "live") and Prometheus forwarding.
    /// Does NOT include a database check — add that per-host where needed.
    /// </summary>
    public static IServiceCollection AddAppHealthChecks(this IServiceCollection services)
    {
        services
            .AddHealthChecks()
            .ForwardToPrometheus()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);

        return services;
    }
}
```

- [ ] **Step 4: Testi çalıştır — pass etmeli**

```bash
dotnet test test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj \
  --filter "FullyQualifiedName~AppHealthChecksRegistrationTests"
```
Beklenen: **1 passed**

- [ ] **Step 5: Commit**

```bash
git add src/BBT.Workflow.HttpApi.Shared/Microsoft/Extensions/DependencyInjection/HealthChecksServiceCollectionExtensions.cs \
        test/BBT.Workflow.Application.Tests/HealthChecks/AppHealthChecksRegistrationTests.cs
git commit -m "feat(health): remove DB check from AddAppHealthChecks — self-only baseline"
```

---

### Task 5: Orchestration'a cached DB health check ekle

**Files:**
- Modify: `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Microsoft/Extensions/DependencyInjection/OrchestrationApiServiceCollectionExtensions.cs`

- [ ] **Step 1: `using` direktiflerini ekle** — dosyanın başına

```csharp
using BBT.Workflow.Caching;
using BBT.Workflow.Controllers.Instances;
using BBT.Workflow.HostedServices;
using BBT.Workflow.HttpApi.Shared.HealthChecks;
using BBT.Workflow.Orchestration.Services;
using HealthChecks.NpgSql;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
```

- [ ] **Step 2: `AddOrchestrationApiModule` içinde chain'e `AddOrchestrationDbHealthCheck()` ekle** — `.AddAppHealthChecks()` sonrasına

```csharp
public static IServiceCollection AddOrchestrationApiModule(this IServiceCollection services)
{
    var configuration = services.GetConfiguration();
    
    services
        .AddFunctionHandlers()
        .AddDomainModule()
        .AddApplicationModule()
        .AddInfrastructureModule(configuration)
        .AddAspNetCoreModules(configuration)
        .AddResultResilience(configuration)
        .AddDaprClients()
        .AddEventBus(configuration)
        .AddWorkflowEventHooks()
        .AddDomainEventsInfrastructure()
        .AddInfrastructureRuntimeServices()
        .AddDbContext(configuration)
        .AppMapper()
        .AddTelemetry(configuration)
        .AddDistributedCache(configuration)
        .AddDistributedLock(configuration)
        .AddTransitionLockScope()
        .AddBackgroundJob()
        .AddRedis()
        .AddExceptionHandling()
        .AddRuntimeMiddleware()
        .AddHeaderService()
        .AddHostedServices()
        .AddAppHealthChecks()
        .AddOrchestrationDbHealthCheck(configuration);   // ← yeni satır
    return services;
}
```

- [ ] **Step 3: Private `AddOrchestrationDbHealthCheck` metodunu ekle** — sınıfın sonuna, `AddHostedServices`'in üstüne

```csharp
private static IServiceCollection AddOrchestrationDbHealthCheck(
    this IServiceCollection services,
    IConfiguration configuration)
{
    var connectionString = configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException(
            "Connection string 'Default' is required for the database health check.");

    // Singleton: TTL state + SemaphoreSlim must survive across probes.
    services.TryAddSingleton<CachedHealthCheck>(sp =>
    {
        var ttl = sp.GetService<IOptions<HealthCheckCacheOptions>>()?.Value.Ttl
                  ?? new HealthCheckCacheOptions().Ttl;

        IHealthCheck inner = new NpgSqlHealthCheck(new NpgSqlHealthCheckOptions(connectionString));
        return new CachedHealthCheck(inner, ttl, TimeProvider.System);
    });

    services.AddHealthChecks().Add(new HealthCheckRegistration(
        name: "database",
        factory: sp => sp.GetRequiredService<CachedHealthCheck>(),
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"],
        timeout: TimeSpan.FromSeconds(2)));

    return services;
}
```

- [ ] **Step 4: Build kontrolü**

```bash
dotnet build orchestration/BBT.Workflow.Orchestration.HttpApi.Host/BBT.Workflow.Orchestration.HttpApi.Host.csproj
```
Beklenen: **Build succeeded**

- [ ] **Step 5: Tüm testleri çalıştır**

```bash
dotnet test test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj
```
Beklenen: tüm testler **passed**

- [ ] **Step 6: Commit**

```bash
git add orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Microsoft/Extensions/DependencyInjection/OrchestrationApiServiceCollectionExtensions.cs
git commit -m "feat(health): add CachedHealthCheck singleton (60s TTL) to Orchestration DB readiness probe"
```

---

### Task 6: appsettings.json'a HealthChecks konfigürasyonu ekle (opsiyonel override)

**Files:**
- Modify: `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json`

- [ ] **Step 1: `HealthChecks` bölümünü ekle** — appsettings.json içine herhangi bir yere

```json
"HealthChecks": {
  "Database": {
    "CacheTtlSeconds": 60
  }
}
```

> Not: Bu bölüm opsiyoneldir — `HealthCheckCacheOptions` default olarak 60 sn kullanır. Sadece ortam bazlı override yapmak istendiğinde (`appsettings.Production.json` vb.) gereklidir.

- [ ] **Step 2: IOptions bağlamasını kaydet** — `AddOrchestrationDbHealthCheck` metodunun başına şu satırı ekle

```csharp
services.Configure<HealthCheckCacheOptions>(
    configuration.GetSection(HealthCheckCacheOptions.SectionName));
```

Bu satır `TryAddSingleton<CachedHealthCheck>` çağrısından ÖNCE gelmelidir ki `IOptions<HealthCheckCacheOptions>` resolve edildiğinde değer hazır olsun.

- [ ] **Step 3: Build + test**

```bash
dotnet build orchestration/BBT.Workflow.Orchestration.HttpApi.Host/BBT.Workflow.Orchestration.HttpApi.Host.csproj
dotnet test test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj
```
Beklenen: **Build succeeded, all tests passed**

- [ ] **Step 4: Commit**

```bash
git add orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json \
        orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Microsoft/Extensions/DependencyInjection/OrchestrationApiServiceCollectionExtensions.cs
git commit -m "feat(health): wire HealthCheckCacheOptions from appsettings for runtime TTL override"
```

---

## Kontrol Listesi (implementation sonrası)

- [ ] `dotnet test` — tüm testler green
- [ ] `dotnet build` — solution build succeeds
- [ ] Inbox worker'da `/ready` → sadece self check response geldiğini doğrula (curl veya Swagger)
- [ ] Orchestration `/ready` → response'da `database` entry var, arka arkaya iki istek arasında DB log yok
