# Faz 3 — Health-Check Baskısının Azaltılması (Bite-Sized Plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** `/ready` DB probe'unun her çağrıda taze bağlantı açıp havuzu/PGBouncer'ı yormasını ve yük altında flapping yapmasını engellemek; HTTP trafiği sunmayan worker'lardan DB readiness'i kaldırmak.

**Architecture:** `AddAppHealthChecks` (HttpApi.Shared) bugün her host'ta `AddNpgSql(connStr, tags:["ready"])` kaydı yapıyor; paket her probe'da yeni `NpgsqlConnection` açıyor. (1) DB check'i kısa TTL'li bir `CachedHealthCheck` decorator'ı ardına al → probe sıklığı DB'ye yansımasın. (2) `AddAppHealthChecks`'i parametrik yap → Inbox/Outbox DB check'i almasın. (3) DB check'e kısa timeout ver.

**Tech Stack:** .NET 10, `Microsoft.Extensions.Diagnostics.HealthChecks` (`IHealthCheck`), `AspNetCore.HealthChecks.NpgSql`, Aether `IClock`/`TimeProvider`, xUnit + NSubstitute + Shouldly.

**Spec kaynağı:** [2026-06-19-vnext-load-test-remediation.md](2026-06-19-vnext-load-test-remediation.md) Faz 3.

---

## Ön Kontroller (Görev 0 — subagent)

- [ ] **Test projesi yerleşimi:** `BBT.Workflow.HttpApi.Shared`'ı referanslayan bir test projesi var mı belirle. Yoksa `CachedHealthCheck`'i unit-test edilebilir kılmak için en uygun mevcut test projesine (`Application.Tests` veya `Infrastructure.Tests`) bir `ProjectReference` ekle ya da decorator'ı referanslı bir yere koy. Kararı uygula ve PR notuna yaz.
- [ ] **Paket API doğrulaması:** Kurulu `AspNetCore.HealthChecks.NpgSql` sürümünde `AddNpgSql` için `NpgsqlDataSource` ve `timeout` parametreli overload'ların varlığını teyit et (`$(HealthChecksPackageVersion)` → `Directory.Packages.props`).
- [ ] **Saat soyutlaması:** Projede `IClock` (Aether) mı yoksa `TimeProvider` mı kullanılıyor — mevcut konvansiyona uy (`grep -rin "IClock\|TimeProvider" src`).

---

## Task 1: CachedHealthCheck decorator (TDD)

**Files:**
- Create: `src/BBT.Workflow.HttpApi.Shared/HealthChecks/CachedHealthCheck.cs`
- Create: `src/BBT.Workflow.HttpApi.Shared/HealthChecks/HealthCheckCacheOptions.cs`
- Test: `<seçilen test projesi>/HealthChecks/CachedHealthCheckTests.cs`

**Davranış:** Bir iç `IHealthCheck`'i sarar; son sonucu `Ttl` süresince cache'ler. TTL içinde tekrar çağrılırsa iç check **çağrılmaz**, cache döner. TTL dolduğunda iç check yeniden değerlendirilir. Eşzamanlı çağrılarda iç check en fazla bir kez tetiklenir (basit lock yeterli).

- [ ] **Step 1: Failing test yaz** — `CachedHealthCheckTests`:

```csharp
public class CachedHealthCheckTests
{
    private static HealthCheckContext Ctx() => new()
    {
        Registration = new HealthCheckRegistration("db", Substitute.For<IHealthCheck>(), null, null)
    };

    [Fact]
    public async Task Within_ttl_inner_is_called_once()
    {
        var inner = Substitute.For<IHealthCheck>();
        inner.CheckHealthAsync(Arg.Any<HealthCheckContext>(), Arg.Any<CancellationToken>())
             .Returns(HealthCheckResult.Healthy());
        var clock = new FakeClock(DateTimeOffset.UnixEpoch);
        var sut = new CachedHealthCheck(inner, TimeSpan.FromSeconds(10), clock);

        await sut.CheckHealthAsync(Ctx());
        clock.Advance(TimeSpan.FromSeconds(5));
        var second = await sut.CheckHealthAsync(Ctx());

        second.Status.ShouldBe(HealthStatus.Healthy);
        await inner.Received(1).CheckHealthAsync(Arg.Any<HealthCheckContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task After_ttl_inner_is_reevaluated()
    {
        var inner = Substitute.For<IHealthCheck>();
        inner.CheckHealthAsync(Arg.Any<HealthCheckContext>(), Arg.Any<CancellationToken>())
             .Returns(HealthCheckResult.Healthy());
        var clock = new FakeClock(DateTimeOffset.UnixEpoch);
        var sut = new CachedHealthCheck(inner, TimeSpan.FromSeconds(10), clock);

        await sut.CheckHealthAsync(Ctx());
        clock.Advance(TimeSpan.FromSeconds(11));
        await sut.CheckHealthAsync(Ctx());

        await inner.Received(2).CheckHealthAsync(Arg.Any<HealthCheckContext>(), Arg.Any<CancellationToken>());
    }
}
```

> `FakeClock`: mevcut test yardımcısı varsa onu kullan; yoksa `TimeProvider` tabanlı bir `FakeTimeProvider` (Microsoft.Extensions.TimeProvider.Testing) veya minimal stub. Saat soyutlaması Ön-Kontrol kararına uymalı.

- [ ] **Step 2: Testi çalıştır, FAIL gör** — `dotnet test <proj> --filter FullyQualifiedName~CachedHealthCheckTests` → derleme/çalışma hatası (tip yok).

- [ ] **Step 3: Minimal implementasyon**

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BBT.Workflow.HttpApi.Shared.HealthChecks;

/// <summary>Caches an inner health check result for a TTL to avoid hitting the
/// dependency (e.g. PostgreSQL) on every probe.</summary>
public sealed class CachedHealthCheck(IHealthCheck inner, TimeSpan ttl, IClock clock) : IHealthCheck
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HealthCheckResult _last;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (clock.Now < _expiresAt)
            return _last;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (clock.Now < _expiresAt)
                return _last;
            _last = await inner.CheckHealthAsync(context, cancellationToken);
            _expiresAt = clock.Now + ttl;
            return _last;
        }
        finally { _gate.Release(); }
    }
}
```

> `IClock` yerine `TimeProvider` seçildiyse `clock.Now` → `timeProvider.GetUtcNow()` olarak uyarla. `HealthCheckCacheOptions` { `TimeSpan Ttl = TimeSpan.FromSeconds(10)` } basit POCO.

- [ ] **Step 4: Testi çalıştır, PASS gör**
- [ ] **Step 5: Commit** — `git commit -m "feat(health): add CachedHealthCheck decorator to throttle DB probes"`

---

## Task 2: AddAppHealthChecks'i parametrik yap + DB check'i cache + kısa timeout ardına al

**Files:**
- Modify: `src/BBT.Workflow.HttpApi.Shared/Microsoft/Extensions/DependencyInjection/HealthChecksServiceCollectionExtensions.cs`

**Hedef davranış:**
- İmza: `AddAppHealthChecks(this IServiceCollection services, bool includeDatabaseCheck = true)`.
- `includeDatabaseCheck` true ise DB check:
  - Mümkünse paylaşılan EF `NpgsqlDataSource` üzerinden (yeni ham bağlantı açmamak için),
  - `timeout: TimeSpan.FromSeconds(2)`,
  - `CachedHealthCheck` (TTL 10 sn) ardına alınmış,
  - `name: "database"`, `tags: ["ready"]`.
- `self` check değişmez (`tags:["live"]`).

- [ ] **Step 1:** `AddAppHealthChecks` imzasına `bool includeDatabaseCheck = true` ekle.
- [ ] **Step 2:** DB check kaydını koşula al; `CachedHealthCheck` ile sar (registration `factory` ile inner Npgsql check'i oluşturup decorator'a geç). Paket overload'ı `NpgsqlDataSource` destekliyorsa onu kullan; desteklemiyorsa connection string + `timeout:` ver ve sebebi PR notuna yaz.
- [ ] **Step 3: Derle** — `dotnet build src/BBT.Workflow.HttpApi.Shared` → başarılı.
- [ ] **Step 4 (varsa):** DI smoke testi — `AddAppHealthChecks()` sonrası `IHealthCheckPublisher`/registration sayısı ve `database` check'in `ready` tag'inde olduğu doğrulanır (mevcut test altyapısı elveriyorsa). Yoksa atla, gerekçeyi not et.
- [ ] **Step 5: Commit** — `git commit -m "feat(health): cache+timeout DB readiness check, make it optional"`

---

## Task 3: Worker'lardan DB readiness'i kaldır

**Files:**
- Modify: `workers/BBT.Workflow.Workers.Inbox/Microsoft/Extensions/DependencyInjection/InboxWorkerServiceCollectionExtensions.cs:49`
- Modify: `workers/BBT.Workflow.Workers.Outbox/Microsoft/Extensions/DependencyInjection/OutboxWorkerServiceCollectionExtensions.cs:49`

**Gerekçe:** Worker'lar HTTP trafiği sunmaz; DB readiness check'i yalnızca bağlantı yakar ve flapping'e katkı verir. `self` (live) yeterli.

- [ ] **Step 1:** Her iki worker'da `.AddAppHealthChecks()` → `.AddAppHealthChecks(includeDatabaseCheck: false)`.
- [ ] **Step 2: Derle** — `dotnet build workers/BBT.Workflow.Workers.Inbox workers/BBT.Workflow.Workers.Outbox` → başarılı.
- [ ] **Step 3: Commit** — `git commit -m "chore(health): drop DB readiness check from inbox/outbox workers"`

---

## Task 4: Bütünsel doğrulama

- [ ] **Step 1: Tüm çözüm derle** — `dotnet build` → 0 error.
- [ ] **Step 2: İlgili testler** — `dotnet test --filter "FullyQualifiedName~HealthCheck"` → yeşil.
- [ ] **Step 3: Regresyon** — `dotnet test test/BBT.Workflow.Application.Tests` → yeşil (değişen test projesine göre).
- [ ] **Step 4: Manuel teyit notu** — Orchestration host'ta `/live` yalnızca `self`; `/ready` `database` içerir ve probe'lar TTL içinde DB'ye gitmiyor; worker'larda `database` check yok.

---

## Kabul Kriterleri (faz)
- `CachedHealthCheck` TTL davranışı unit testle kanıtlı.
- Orchestration `/ready` DB check'i ≤10 sn'de bir DB'ye gidiyor (her probe'da değil), 2 sn timeout'lu.
- Inbox/Outbox `/ready` DB check içermiyor (`self` only).
- `dotnet build` ve health testleri yeşil; regresyon yok.

## Notlar / Riskler
- Cache, gerçek DB kesintisini ≤TTL gecikmeyle yansıtır — readiness için kabul edilebilir (10 sn).
- Paket `NpgsqlDataSource` overload'ı yoksa connection-string + timeout yolu kullanılır; bağlantı baskısı azalması ağırlıklı **cache**'ten gelir.
